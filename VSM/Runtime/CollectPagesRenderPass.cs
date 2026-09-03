using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace vsm
{
    public class CollectPagesRenderPass : ScriptableRenderPass, IDisposable
    {
        private readonly ProfilingSampler _profilingSampler;
        private const string CollectPagesCmdName = "VSMCollectPages";
        private const string HandlePagesCmdName = "VSMHandleRequiredPages";
        private readonly VSMConfig _vsmConfig;
        private readonly PageTableManager _pageTableManager;
        private readonly Camera _lightCamera;

        private readonly ComputeShader _computeShader;
        private readonly int _kernelIdxClearPageCount;
        private readonly int _kernelIdxCollectPages;
        private readonly int _totalVirPageCount;

        private static readonly int NameIdPageCountBuffer = Shader.PropertyToID("_VSMPageCountBuffer");
        private static readonly int NameIdTotalPageCount = Shader.PropertyToID("_TotalPageCount");
        private static readonly int NameIdScreenSize = Shader.PropertyToID("_VSMScreenSize");
        private static readonly int NameIdLightVpMatrix = Shader.PropertyToID("_VSMLightMatrix");
        private static readonly int NameIdPageTableSize = Shader.PropertyToID("_PageTableSize");
        private static readonly int NameIdCollectPageDebugTexture = Shader.PropertyToID("_CollectPageDebugTexture");
        private static readonly int NameIdMipCount = Shader.PropertyToID("_VSMMipCount");
        private static readonly int NameIdDistanceSensitivity = Shader.PropertyToID("_VSMDistanceSensitivity");
        private static readonly int NameIdVSMMainCameraWorldPos = Shader.PropertyToID("_VSMMainCameraWorldPos");

        private ComputeBuffer _pageCountBuffer;
        private RenderTexture _collectPageDebugTexture;
        private readonly int _mipCount;
        private readonly float _distanceSensitivity;

        private const int KernelClearThreadCount = 64;
        private const int KernelCollectBlockSizeX = 8;
        private const int KernelCollectBlockSizeY = 8;

        public CollectPagesRenderPass(VSMConfig vsmConfig, Camera lightCamera, PageTableManager pageTableManager)
        {
            _profilingSampler = new ProfilingSampler("CollectPagesRenderPass");
            _vsmConfig = vsmConfig;
            _lightCamera = lightCamera;
            _pageTableManager = pageTableManager;
            _distanceSensitivity = vsmConfig.distanceSensitivity;

            _computeShader =
                AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.shadow.vsm/Shaders/CollectPages.compute");
            if (_computeShader)
            {
                _kernelIdxClearPageCount = _computeShader.FindKernel("ClearPageCount");
                _kernelIdxCollectPages = _computeShader.FindKernel("CollectPages");
            }

            var virtualTextureGridSize = vsmConfig.virtualTextureGridSize;
            _mipCount = vsmConfig.GetMipCount();
            _totalVirPageCount = virtualTextureGridSize.x * virtualTextureGridSize.y * _mipCount;
        }

        private void AllocPageCountBufferIfNeeded()
        {
            if (_totalVirPageCount <= 0) return;

            if (_pageCountBuffer != null && _pageCountBuffer.count == _totalVirPageCount)
                return;

            _pageCountBuffer?.Release();
            _pageCountBuffer = new ComputeBuffer(_totalVirPageCount, sizeof(int));
            _pageCountBuffer.name = "[VSM] PageCountBuffer";
        }

        private void AllocCollectPageDebugTextureIfNeeded(int width, int height)
        {
            if (_collectPageDebugTexture != null
                && _collectPageDebugTexture.width == width
                && _collectPageDebugTexture.height == height) return;

            _collectPageDebugTexture?.Release();
            _collectPageDebugTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "[VSM] CollectPageDebugTexture",
                enableRandomWrite = true
            };
            _collectPageDebugTexture.Create();
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            AllocPageCountBufferIfNeeded();

            if (!_computeShader || !_lightCamera || _pageCountBuffer == null)
            {
                return;
            }

            var cmd = CommandBufferPool.Get(CollectPagesCmdName);
            using (new ProfilingScope(cmd, _profilingSampler))
            {
                cmd.SetGlobalVector(NameIdVSMMainCameraWorldPos, renderingData.cameraData.worldSpaceCameraPos);

                // 1. ClearPageCount
                cmd.SetComputeBufferParam(_computeShader, _kernelIdxClearPageCount,
                    NameIdPageCountBuffer, _pageCountBuffer);
                cmd.SetComputeIntParam(_computeShader, NameIdTotalPageCount, _totalVirPageCount);
                var clearGroups =
                    (_totalVirPageCount + KernelClearThreadCount - 1) / KernelClearThreadCount; // Round up
                cmd.DispatchCompute(_computeShader, _kernelIdxClearPageCount, clearGroups, 1, 1);

                // 2. CollectPages
                var cameraData = renderingData.cameraData;
                var width = cameraData.cameraTargetDescriptor.width;
                var height = cameraData.cameraTargetDescriptor.height;
                AllocCollectPageDebugTextureIfNeeded(width, height);
                cmd.SetComputeIntParam(_computeShader, NameIdMipCount, _mipCount);
                cmd.SetComputeFloatParam(_computeShader, NameIdDistanceSensitivity, _distanceSensitivity);
                cmd.SetComputeBufferParam(_computeShader, _kernelIdxCollectPages,
                    NameIdPageCountBuffer, _pageCountBuffer);
                cmd.SetComputeTextureParam(_computeShader, _kernelIdxCollectPages,
                    NameIdCollectPageDebugTexture, _collectPageDebugTexture);
                cmd.SetComputeVectorParam(_computeShader, NameIdScreenSize,
                    new Vector4(width, height, 0, 0));

                var gpuProj = GL.GetGPUProjectionMatrix(_lightCamera.projectionMatrix, false);
                var viewMatrix = _lightCamera.worldToCameraMatrix;
                var lightMatrix = gpuProj * viewMatrix;
                cmd.SetComputeMatrixParam(_computeShader, NameIdLightVpMatrix, lightMatrix);
                cmd.SetComputeVectorParam(_computeShader, NameIdPageTableSize,
                    new Vector4(_vsmConfig.virtualTextureGridSize.x, _vsmConfig.virtualTextureGridSize.y, 0.0f, 0.0f));

                var groupX = (width + KernelCollectBlockSizeX - 1) / KernelCollectBlockSizeX; // Round up
                var groupY = (height + KernelCollectBlockSizeY - 1) / KernelCollectBlockSizeY; // Round up
                cmd.DispatchCompute(_computeShader, _kernelIdxCollectPages, groupX, groupY, 1);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            // 3. Pass buffer to PageTableManager (GPU-Driven, no CPU readback )
            var handlePagesCmd = CommandBufferPool.Get(HandlePagesCmdName);
            _pageTableManager.HandleRequiredVirPagesGPU(handlePagesCmd, context, _pageCountBuffer);
            CommandBufferPool.Release(handlePagesCmd);
        }

        public void Dispose()
        {
            if (_pageCountBuffer != null)
            {
                _pageCountBuffer.Release();
                _pageCountBuffer = null;
            }

            if (_collectPageDebugTexture)
            {
                _collectPageDebugTexture.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(_collectPageDebugTexture);
                else UnityEngine.Object.DestroyImmediate(_collectPageDebugTexture);
                _collectPageDebugTexture = null;
            }
        }
    }
}
using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;

namespace vsm
{
    public class RenderPagesRenderPass : ScriptableRenderPass, IDisposable
    {
        private struct LightCameraState
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float OrthographicSize;
            public float Aspect;
        }

        private readonly VSMConfig _vsmConfig;
        private readonly Camera _lightCamera;
        private readonly PageTableManager _pageTableManager;
        private readonly Material _castShadowMaterial;
        private readonly ProfilingSampler _profilingSampler;
        private const string RenderPagesCmdName = "VSMRenderPages";
        private readonly ShaderTagId _shaderTag = new("DepthOnly");
        private static readonly int NameIdLightMatrix = Shader.PropertyToID("_VSMLightMatrix");
        private static readonly int NameIdVSMLightDir = Shader.PropertyToID("_VSMLightDir");
        private static readonly int NameIdVSMPhysicalTexture = Shader.PropertyToID("_VSMPhysicalTexture");
        private static readonly int NameIdPageGridParams = Shader.PropertyToID("_VSMPageGridParams");
        private static readonly int NameIdVirPageStatusBuffer = Shader.PropertyToID("_VSMVirPageStatusBuffer");

        private static readonly int NameIdPhyPageStatusDebugBuffer =
            Shader.PropertyToID("_VSMPhyPageStatusDebugBuffer");

        private static readonly int NameIdVirPageStatusBufferCount =
            Shader.PropertyToID("_VSMVirPageStatusBufferCount");

        private static readonly int NameIdDistanceSensitivity = Shader.PropertyToID("_VSMDistanceSensitivity");
        private static readonly int NameIdMipCount = Shader.PropertyToID("_VSMMipCount");
        private RenderTexture _physicalTexture;

        public RenderPagesRenderPass(VSMConfig vsmConfig, Camera lightCamera, PageTableManager pageTableManager)
        {
            _vsmConfig = vsmConfig;
            _lightCamera = lightCamera;
            _pageTableManager = pageTableManager;
            _castShadowMaterial = new Material(Shader.Find("VSM/CastShadow"));
            _profilingSampler = new ProfilingSampler(RenderPagesCmdName);
            CreatePhysicalTexture(vsmConfig.PhysicalTextureResolution);
        }

        private void CreatePhysicalTexture(int2 physicalTextureResolution)
        {
            _physicalTexture = new RenderTexture(physicalTextureResolution.x, physicalTextureResolution.y,
                24, RenderTextureFormat.Depth)
            {
                name = "[VSM] Physical Texture",
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _physicalTexture.Create();
        }

        private LightCameraState CaptureLightCameraState()
        {
            var transform = _lightCamera.transform;
            return new LightCameraState
            {
                Position = transform.position,
                Rotation = transform.rotation,
                OrthographicSize = _lightCamera.orthographicSize,
                Aspect = _lightCamera.aspect
            };
        }

        private void RestoreLightCamera(in LightCameraState state)
        {
            _lightCamera.transform.SetPositionAndRotation(state.Position, state.Rotation);
            _lightCamera.orthographicSize = state.OrthographicSize;
            _lightCamera.aspect = state.Aspect;
        }

        private void UpdateLightCamera(VirPageLoc virPageLoc, in LightCameraState baseState)
        {
            var transform = _lightCamera.transform;
            transform.SetPositionAndRotation(baseState.Position, baseState.Rotation);

            var mipScale = 1 << virPageLoc.Mip;
            var currentGridSizeX = Mathf.Max(1.0f, _vsmConfig.virtualTextureGridSize.x / (float)mipScale);
            var currentGridSizeY = Mathf.Max(1.0f, _vsmConfig.virtualTextureGridSize.y / (float)mipScale);

            var totalHeight = baseState.OrthographicSize * 2f;
            var totalWidth = totalHeight * baseState.Aspect;
            var normalizedX = (virPageLoc.X + 0.5f) / currentGridSizeX - 0.5f;
            var normalizedY = (virPageLoc.Y + 0.5f) / currentGridSizeY - 0.5f;

            var offsetX = normalizedX * totalWidth;
            var offsetY = normalizedY * totalHeight;
            transform.position = baseState.Position + transform.right * offsetX + transform.up * offsetY;
            var tileWorldHeight = totalHeight / currentGridSizeY;
            var tileWorldWidth = totalWidth / currentGridSizeX;
            _lightCamera.orthographicSize = tileWorldHeight * 0.5f;
            _lightCamera.aspect = tileWorldHeight > 0 ? tileWorldWidth / tileWorldHeight : baseState.Aspect;
        }

        private void DrawShadow(Rect viewport, Matrix4x4 lightMatrix, CommandBuffer cmd,
            ScriptableRenderContext context)
        {
            if (!_lightCamera)
                return;

            if (!_lightCamera.TryGetCullingParameters(out var cullingParams))
                return;

            var cullResults = context.Cull(ref cullingParams);
            var desc = new RendererListDesc(_shaderTag, cullResults, _lightCamera)
            {
                renderQueueRange = RenderQueueRange.opaque,
                sortingCriteria = SortingCriteria.CommonOpaque,
                overrideMaterial = _castShadowMaterial,
                overrideMaterialPassIndex = 0
            };

            cmd.SetGlobalMatrix(NameIdLightMatrix, lightMatrix);
            cmd.SetViewport(viewport);
            cmd.DrawRendererList(context.CreateRendererList(desc));
        }

        private Rect CalcViewport(PhyPageLoc phyPageLoc)
        {
            var phyTextureGridSize = _vsmConfig.physicalTextureGridSize;

            if (phyPageLoc.X < 0 || phyPageLoc.X >= phyTextureGridSize.x || phyPageLoc.Y < 0 ||
                phyPageLoc.Y >= phyTextureGridSize.y)
            {
                return Rect.zero;
            }

            var viewportWidth = _vsmConfig.pageResolution;
            var viewportHeight = _vsmConfig.pageResolution;
            var originX = phyPageLoc.X * viewportWidth;
            var originY = (phyTextureGridSize.y - 1 - phyPageLoc.Y) * viewportHeight;

            return new Rect(originX, originY, viewportWidth, viewportHeight);
        }

        private void DrawShadowPage(VirPageLoc virPageLoc, PhyPageLoc phyPageLoc, in LightCameraState baseState,
            CommandBuffer cmd, ScriptableRenderContext context)
        {
            var target = _physicalTexture;
            if (!target) return;

            var viewport = CalcViewport(phyPageLoc);
            if (viewport.width <= 0f || viewport.height <= 0f) return;
            cmd.SetViewport(viewport);
            cmd.ClearRenderTarget(true, false, Color.clear, 1.0f);
            UpdateLightCamera(virPageLoc, baseState);
            var lightMatrix = Utilities.BuildLightMatrix(_lightCamera);
            DrawShadow(viewport, lightMatrix, cmd, context);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!_lightCamera || !_physicalTexture) return;

            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, _profilingSampler))
            {
                cmd.SetRenderTarget(_physicalTexture);
                var baseState = CaptureLightCameraState();

                var pageRequestCount = _pageTableManager.GetPageRequestCount();
                if (pageRequestCount > 0)
                {
                    var pageRequests = _pageTableManager.GetPageRequests();
                    for (var i = 0; i < pageRequestCount; i++)
                    {
                        var request = pageRequests[i];
                        DrawShadowPage(request.VirPageLoc, request.PhyPageLoc, baseState, cmd, context);
                    }
                }

                RestoreLightCamera(baseState);
                var projMatrix = GL.GetGPUProjectionMatrix(_lightCamera.projectionMatrix, false);
                var viewMatrix = _lightCamera.worldToCameraMatrix;
                var lightMatrix = projMatrix * viewMatrix;
                cmd.SetGlobalMatrix(NameIdLightMatrix, lightMatrix);
                cmd.SetGlobalVector(NameIdVSMLightDir, _lightCamera.transform.forward);
                cmd.SetGlobalTexture(NameIdVSMPhysicalTexture, _physicalTexture);
                var pageGridParams = new Vector4(
                    _vsmConfig.virtualTextureGridSize.x,
                    _vsmConfig.virtualTextureGridSize.y,
                    _vsmConfig.physicalTextureGridSize.x,
                    _vsmConfig.physicalTextureGridSize.y
                );
                cmd.SetGlobalVector(NameIdPageGridParams, pageGridParams);
                var virPageStatusBuffer = _pageTableManager.GetVirPageStatusBuffer();
                if (virPageStatusBuffer != null)
                {
                    cmd.SetGlobalBuffer(NameIdVirPageStatusBuffer, virPageStatusBuffer);
                    cmd.SetGlobalInt(NameIdVirPageStatusBufferCount, virPageStatusBuffer.count);
                }

                var phyPageStatusDebugBuffer = _pageTableManager.GetPhyPageStatusDebugBuffer();
                if (phyPageStatusDebugBuffer != null)
                {
                    cmd.SetGlobalBuffer(NameIdPhyPageStatusDebugBuffer, phyPageStatusDebugBuffer);
                }

                cmd.SetGlobalFloat(NameIdDistanceSensitivity, _vsmConfig.distanceSensitivity);
                cmd.SetGlobalInt(NameIdMipCount, _vsmConfig.GetMipCount());
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public Texture GetPhyPageTexture()
        {
            return _physicalTexture;
        }

        public void Dispose()
        {
            if (_physicalTexture)
            {
                _physicalTexture.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(_physicalTexture);
                else UnityEngine.Object.DestroyImmediate(_physicalTexture);
                _physicalTexture = null;
            }

            if (!_castShadowMaterial) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(_castShadowMaterial);
            else UnityEngine.Object.DestroyImmediate(_castShadowMaterial);
        }
    }
}
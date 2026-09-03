using System;
using Unity.Collections;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace vsm
{
    public struct VirPageLoc
    {
        public int X;
        public int Y;
        public int Mip;
    }

    public struct PhyPageLoc
    {
        public int X;
        public int Y;
    }

    public struct PageRequest
    {
        public VirPageLoc VirPageLoc;
        public PhyPageLoc PhyPageLoc;

        public PageRequest(VirPageLoc virPageLoc, PhyPageLoc phyPageLoc)
        {
            VirPageLoc = virPageLoc;
            PhyPageLoc = phyPageLoc;
        }
    }

    public struct VirPageStatus
    {
        public const int Unloaded = 0;
        public const int Loaded = 1;
        public const int Indirect = 2;

        public PhyPageLoc PhyPageLoc;
        public int Status;
        public int AncestorMip;

        public static int GetSize()
        {
            return Marshal.SizeOf(typeof(VirPageStatus));
        }
    }

    public struct PhyPageStatus
    {
        public const int Free = 0;
        public const int Used = 1;

        public int VirPageIdx;
        public int Status;

        public static int GetSize()
        {
            return Marshal.SizeOf(typeof(PhyPageStatus));
        }
    }

    public class PageStat
    {
        public int CachedVirPageCount;
        public int SwapInVirPageCount;
        public int SwapOutVirPageCount;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"CachedVirPageCount: {CachedVirPageCount}\n");
            sb.Append($"SwapInVirPageCount: {SwapInVirPageCount}\n");
            sb.Append($"SwapOutVirPageCount: {SwapOutVirPageCount}");
            return sb.ToString();
        }
    }

    public class PageTableManager : IDisposable
    {
        private readonly ProfilingSampler _pageAllocationSampler;
        private readonly VSMConfig _vsmConfig;
        private readonly PageRequest[] _pageRequests;

        private int _pageRequestCount;

        // Async GPU readback (frame-delayed).
        private NativeArray<int> _pageRequestCountNative;
        private NativeArray<PageRequest> _pageRequestsNativeArray;
        private AsyncGPUReadbackRequest _countRequest;
        private AsyncGPUReadbackRequest _dataRequest;
        private bool _readbackInFlight;
        private bool _hasAppliedAnyAsyncReadback;
        
        private ComputeBuffer _virPageStatusBuffer;
        private ComputeBuffer _phyPageStatusBuffer;
        private readonly VirPageStatus[] _virPageStatusArray;
        private readonly PhyPageStatus[] _phyPageStatusArray;
        // private readonly int[] _swapInVirPagesArray;
        // private readonly int[] _fallbackIndices;
        private readonly PageStat _pageStat;
        private readonly Texture2D[] _virPageStatusDebugTextures; // For debug

        // [GPU]
        private const int RootPhyIdx = 0;
        private readonly ComputeShader _allocationShader;
        private readonly int _kernelClearPhyStatus;
        private readonly int _kernelMarkCacheHits;
        private readonly int _kernelAllocate;
        private ComputeBuffer _pageRequestsBuffer;
        private ComputeBuffer _pageRequestCounterBuffer; // for reading AppendBuffer count
        private ComputeBuffer _nextPhyPageIndexBuffer; // atomic counter for round-robin allocation

        private static readonly int NameIdTotalVirPages = Shader.PropertyToID("_TotalVirPages");
        private static readonly int NameIdTotalPhyPages = Shader.PropertyToID("_TotalPhyPages");
        private static readonly int NameIdRootPhyIdx = Shader.PropertyToID("_RootPhyIdx");
        private static readonly int NameIdRootVirIdx = Shader.PropertyToID("_RootVirIdx");
        private static readonly int NameIdMipCount = Shader.PropertyToID("_MipCount");
        private static readonly int NameIdVSMVirtualGridSize = Shader.PropertyToID("_VSMVirtualGridSize");
        private static readonly int NameIdVSMPhysicalGridSize = Shader.PropertyToID("_VSMPhysicalGridSize");
        private static readonly int NameIdPhyPageStatus = Shader.PropertyToID("_PhyPageStatus");
        private static readonly int NameIdPageCountBuffer = Shader.PropertyToID("_PageCountBuffer");
        private static readonly int NameIdVirPageStatus = Shader.PropertyToID("_VirPageStatus");
        private static readonly int NameIdPageRequests = Shader.PropertyToID("_PageRequests");
        private static readonly int NameIdNextPhyPageIndex = Shader.PropertyToID("_NextPhyPageIndex");

        public PageTableManager(VSMConfig vsmConfig)
        {
            _pageAllocationSampler = new ProfilingSampler("VSMPageAllocation");
            _vsmConfig = vsmConfig;
            var virtualTextureGridSize = vsmConfig.virtualTextureGridSize;
            var mipCount = vsmConfig.GetMipCount();
            var totalVirPages = virtualTextureGridSize.x * virtualTextureGridSize.y * mipCount;
            _pageRequests = new PageRequest[totalVirPages];
            _virPageStatusArray = new VirPageStatus[totalVirPages];
            // _swapInVirPagesArray = new int[totalVirPages];
            // _fallbackIndices = new int[totalVirPages];
            _pageStat = new PageStat();

            _phyPageStatusArray =
                new PhyPageStatus[vsmConfig.physicalTextureGridSize.x * vsmConfig.physicalTextureGridSize.y];
            for (var i = 0; i < _phyPageStatusArray.Length; i++)
            {
                _phyPageStatusArray[i].VirPageIdx = -1;
            }

            // Load compute shader for GPU page allocation
            _allocationShader =
                AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.shadow.vsm/Shaders/PageAllocation.compute");
            if (_allocationShader != null)
            {
                _kernelClearPhyStatus = _allocationShader.FindKernel("ClearPhyStatus");
                _kernelMarkCacheHits = _allocationShader.FindKernel("MarkCacheHits");
                _kernelAllocate = _allocationShader.FindKernel("AllocatePages");
            }
            _virPageStatusBuffer = new ComputeBuffer(totalVirPages, VirPageStatus.GetSize());
            _virPageStatusBuffer.name = "[VSM] VirPageStatusBuffer";
            _virPageStatusBuffer.SetData(_virPageStatusArray);
            
            // GPU allocation requires PhyPageStatus buffer (always create it)
            _phyPageStatusBuffer = new ComputeBuffer(_phyPageStatusArray.Length, PhyPageStatus.GetSize());
            _phyPageStatusBuffer.name = "[VSM] PhyPageStatusDebugBuffer";
            _phyPageStatusBuffer.SetData(_phyPageStatusArray);
            _pageRequestsBuffer = new ComputeBuffer(totalVirPages, Marshal.SizeOf(typeof(PageRequest)),
                ComputeBufferType.Append);
            _pageRequestsBuffer.name = "[VSM] PageRequestsBuffer";
            _pageRequestCounterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
            _pageRequestCounterBuffer.name = "[VSM] PageRequestCounterBuffer";
            _nextPhyPageIndexBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
            _nextPhyPageIndexBuffer.name = "[VSM] NextPhyPageIndexBuffer";
            _nextPhyPageIndexBuffer.SetData(new uint[] { 1 }); // Start from 1
            
            _pageRequestCountNative = new NativeArray<int>(1, Allocator.Persistent);
            _pageRequestsNativeArray = new NativeArray<PageRequest>(totalVirPages, Allocator.Persistent);

            _virPageStatusDebugTextures = new Texture2D[mipCount];
            for (var i = 0; i < mipCount; i++)
            {
                var currentGridSizeX = Mathf.Max(1, _vsmConfig.virtualTextureGridSize.x >> i);
                var currentGridSizeY = Mathf.Max(1, _vsmConfig.virtualTextureGridSize.y >> i);

                _virPageStatusDebugTextures[i] = new Texture2D(
                    currentGridSizeX,
                    currentGridSizeY, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    name = $"PageAllocation_Mip{i}"
                };
            }
        }

        public void HandleRequiredVirPagesGPU(CommandBuffer cmd, ScriptableRenderContext context,
            ComputeBuffer pageCountBuffer)
        {
            if (_allocationShader == null || pageCountBuffer == null || cmd == null) return;

            ApplyStagedReadbackIfReady();

            if (_readbackInFlight) return;

            WarmupRootPageRequest();

            cmd.SetBufferCounterValue(_pageRequestsBuffer, 0);
            cmd.SetComputeIntParam(_allocationShader, NameIdTotalVirPages, _virPageStatusArray.Length);
            cmd.SetComputeIntParam(_allocationShader, NameIdTotalPhyPages, _phyPageStatusArray.Length);
            cmd.SetComputeIntParam(_allocationShader, NameIdRootPhyIdx, RootPhyIdx);
            cmd.SetComputeIntParam(_allocationShader, NameIdRootVirIdx, GetRootVirPageIdx());
            cmd.SetComputeIntParam(_allocationShader, NameIdMipCount, _vsmConfig.GetMipCount());
            cmd.SetComputeIntParams(_allocationShader, NameIdVSMVirtualGridSize, _vsmConfig.virtualTextureGridSize.x,
                _vsmConfig.virtualTextureGridSize.y);
            cmd.SetComputeIntParams(_allocationShader, NameIdVSMPhysicalGridSize, _vsmConfig.physicalTextureGridSize.x,
                _vsmConfig.physicalTextureGridSize.y);

            var threadGroupsXPhy = Mathf.CeilToInt(_phyPageStatusArray.Length / 64.0f);
            var threadGroupsXVirAll = Mathf.CeilToInt(_virPageStatusArray.Length / 64.0f);
            
            using (new ProfilingScope(cmd, _pageAllocationSampler))
            {
                // Stage 1: clear physical status buffer
                cmd.SetComputeBufferParam(_allocationShader, _kernelClearPhyStatus, NameIdPhyPageStatus,
                    _phyPageStatusBuffer);
                cmd.DispatchCompute(_allocationShader, _kernelClearPhyStatus, threadGroupsXPhy, 1, 1);

                // Stage 2: mark cache hits + allocate pages (full scan with early-out)
                cmd.SetComputeBufferParam(_allocationShader, _kernelMarkCacheHits, NameIdPageCountBuffer,
                    pageCountBuffer);
                cmd.SetComputeBufferParam(_allocationShader, _kernelMarkCacheHits, NameIdVirPageStatus,
                    _virPageStatusBuffer);
                cmd.SetComputeBufferParam(_allocationShader, _kernelMarkCacheHits, NameIdPhyPageStatus,
                    _phyPageStatusBuffer);
                cmd.DispatchCompute(_allocationShader, _kernelMarkCacheHits, threadGroupsXVirAll, 1, 1);

                cmd.SetComputeBufferParam(_allocationShader, _kernelAllocate, NameIdPageCountBuffer,
                    pageCountBuffer);
                cmd.SetComputeBufferParam(_allocationShader, _kernelAllocate, NameIdVirPageStatus,
                    _virPageStatusBuffer);
                cmd.SetComputeBufferParam(_allocationShader, _kernelAllocate, NameIdPhyPageStatus,
                    _phyPageStatusBuffer);
                cmd.SetComputeBufferParam(_allocationShader, _kernelAllocate, NameIdPageRequests,
                    _pageRequestsBuffer);
                cmd.SetComputeBufferParam(_allocationShader, _kernelAllocate, NameIdNextPhyPageIndex,
                    _nextPhyPageIndexBuffer);
                cmd.DispatchCompute(_allocationShader, _kernelAllocate, threadGroupsXVirAll, 1, 1);

                // Copy AppendBuffer counter to a readable buffer (must be part of the same cmd for correct ordering)
                cmd.CopyCounterValue(_pageRequestsBuffer, _pageRequestCounterBuffer, 0);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            // Submit requests using STATIC polling-based API (0 GC)
            if (!_readbackInFlight)
            {
                // 0 GC readback api
                _countRequest =
                    AsyncGPUReadback.RequestIntoNativeArray(ref _pageRequestCountNative, _pageRequestCounterBuffer);
                _dataRequest =
                    AsyncGPUReadback.RequestIntoNativeArray(ref _pageRequestsNativeArray, _pageRequestsBuffer);
                _readbackInFlight = true;
            }
        }

        private void WarmupRootPageRequest()
        {
            if (_hasAppliedAnyAsyncReadback) return;

            _pageRequestCount = 1;
            _pageRequests[0] = new PageRequest(
                new VirPageLoc { X = 0, Y = 0, Mip = _vsmConfig.GetMipCount() - 1 },
                new PhyPageLoc { X = 0, Y = 0 }
            );
        }

        private void ApplyStagedReadbackIfReady()
        {
            if (!_readbackInFlight) return;
            if (!_countRequest.done || !_dataRequest.done) return; // 非阻塞查询

            if (_countRequest.hasError || _dataRequest.hasError)
            {
                _readbackInFlight = false;
                return;
            }

            var count = _pageRequestCountNative[0];
            _pageRequestCount = Mathf.Clamp(count, 0, _pageRequests.Length);

            if (_pageRequestCount > 0)
            {
                for (var i = 0; i < _pageRequestCount; i++)
                {
                    _pageRequests[i] = _pageRequestsNativeArray[i];
                }
            }

            _hasAppliedAnyAsyncReadback = true;
            _readbackInFlight = false;
        }

        private void UpdateVirPageStatusTextures()
        {
            if (_virPageStatusDebugTextures == null || _virPageStatusDebugTextures.Length == 0) return;

            var colorBlack = new Color32(0, 0, 0, 255);
            var colorGreen = new Color32(0, 255, 0, 255);
            var colorRed = new Color32(255, 0, 0, 255);

            foreach (var texture in _virPageStatusDebugTextures)
            {
                if (!texture) continue;
                var data = texture.GetRawTextureData<Color32>();
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] = colorBlack;
                }
            }

            for (var i = 0; i < _virPageStatusArray.Length; i++)
            {
                if (_virPageStatusArray[i].Status == VirPageStatus.Unloaded) continue;
                var status = _virPageStatusArray[i].Status;
                var virPageLoc = CalcVirPageLoc(i);
                if (virPageLoc.Mip >= _virPageStatusDebugTextures.Length) continue;

                var texture = _virPageStatusDebugTextures[virPageLoc.Mip];
                var data = texture.GetRawTextureData<Color32>();
                var pixelIndex = virPageLoc.Y * texture.width + virPageLoc.X;

                if (pixelIndex >= 0 && pixelIndex < data.Length)
                {
                    if (status == VirPageStatus.Loaded)
                        data[pixelIndex] = colorGreen;
                    else if (status == VirPageStatus.Indirect)
                        data[pixelIndex] = colorRed;
                }
            }

            foreach (var texture in _virPageStatusDebugTextures)
            {
                if (texture != null) texture.Apply(false, false);
            }
        }

        private VirPageLoc CalcVirPageLoc(int virPageIdx)
        {
            var gridW = _vsmConfig.virtualTextureGridSize.x;
            var gridH = _vsmConfig.virtualTextureGridSize.y;
            var layerSize = gridW * gridH;
            var mip = virPageIdx / layerSize;
            var indexInLayer = virPageIdx % layerSize;
            return new VirPageLoc
            {
                Mip = mip,
                X = indexInLayer % gridW,
                Y = indexInLayer / gridW
            };
        }

        private int GetRootVirPageIdx()
        {
            // Logic: MipOffset + 0.
            var maxMip = _vsmConfig.GetMipCount() - 1;
            var gridW = _vsmConfig.virtualTextureGridSize.x;
            var gridH = _vsmConfig.virtualTextureGridSize.y;
            var layerSize = gridW * gridH;

            return maxMip * layerSize;
        }

        public ComputeBuffer GetVirPageStatusBuffer() => _virPageStatusBuffer;

        public ComputeBuffer GetPhyPageStatusDebugBuffer() => _phyPageStatusBuffer;

        public Texture2D[] GetVirPageStatusTextures() => _virPageStatusDebugTextures;

        public PageStat GetPageStat() => _pageStat;

        public PageRequest[] GetPageRequests() => _pageRequests;

        public int GetPageRequestCount() => _pageRequestCount;

        public void Dispose()
        {
            if (_readbackInFlight)
            {
                if (!_countRequest.done) _countRequest.WaitForCompletion();
                if (!_dataRequest.done) _dataRequest.WaitForCompletion();
            }

            _readbackInFlight = false;

            if (_virPageStatusBuffer != null)
            {
                _virPageStatusBuffer.Dispose();
                _virPageStatusBuffer = null;
            }

            if (_phyPageStatusBuffer != null)
            {
                _phyPageStatusBuffer.Dispose();
                _phyPageStatusBuffer = null;
            }

            if (_pageRequestsBuffer != null)
            {
                _pageRequestsBuffer.Dispose();
                _pageRequestsBuffer = null;
            }

            if (_pageRequestCounterBuffer != null)
            {
                _pageRequestCounterBuffer.Dispose();
                _pageRequestCounterBuffer = null;
            }

            if (_nextPhyPageIndexBuffer != null)
            {
                _nextPhyPageIndexBuffer.Dispose();
                _nextPhyPageIndexBuffer = null;
            }

            if (_pageRequestCountNative.IsCreated) _pageRequestCountNative.Dispose();
            if (_pageRequestsNativeArray.IsCreated) _pageRequestsNativeArray.Dispose();

            if (_virPageStatusDebugTextures != null)
            {
                foreach (var texture in _virPageStatusDebugTextures)
                {
                    if (texture != null)
                    {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
                        else UnityEngine.Object.DestroyImmediate(texture);
                    }
                }

                Array.Clear(_virPageStatusDebugTextures, 0, _virPageStatusDebugTextures.Length);
            }
        }
    }
}
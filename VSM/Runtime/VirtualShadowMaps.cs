using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace vsm
{
    public class VirtualShadowMaps : ScriptableRendererFeature
    {
        [SerializeField] private VSMConfig vsmConfig;
        private PageTableManager _pageTableManager;
        private CollectPagesRenderPass _collectPagesRenderPass;
        private RenderPagesRenderPass _renderPagesRenderPass;
        private Camera _lightCamera;

        public override void Create()
        {
            // ALWAYS dispose previous instances first to prevent leaks
            _collectPagesRenderPass?.Dispose();
            _collectPagesRenderPass = null;
            _renderPagesRenderPass?.Dispose();
            _renderPagesRenderPass = null;
            _pageTableManager?.Dispose();
            _pageTableManager = null;

            _lightCamera = FindMainLightCamera();
            if (!_lightCamera)
            {
                return;
            }
            
            _pageTableManager = new PageTableManager(vsmConfig);

            _collectPagesRenderPass = new CollectPagesRenderPass(vsmConfig, _lightCamera, _pageTableManager)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingGbuffer
            };

            _renderPagesRenderPass = new RenderPagesRenderPass(vsmConfig, _lightCamera, _pageTableManager)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingGbuffer
            };
            
            VSMDebugData.VSMConfig = vsmConfig;
            VSMDebugData.VirPageStatusTextures = _pageTableManager.GetVirPageStatusTextures();
            VSMDebugData.PhyPageTexture = _renderPagesRenderPass.GetPhyPageTexture();
            VSMDebugData.PageStat = _pageTableManager.GetPageStat();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (Application.isPlaying)
            {
                if (!UniversalRenderPipeline.IsGameCamera(renderingData.cameraData.camera))
                {
                    return;
                }
            }
            else
            {
                if (!renderingData.cameraData.isSceneViewCamera)
                {
                    return;
                }
            }
            
            if (!_lightCamera)
            {
                return;
            }

            renderer.EnqueuePass(_collectPagesRenderPass);
            renderer.EnqueuePass(_renderPagesRenderPass);
        }

        private void OnDisable()
        {
            DisposeResources();
        }

        private void OnDestroy()
        {
            DisposeResources();
        }

        protected override void Dispose(bool disposing)
        {
            DisposeResources();
        }

        private void DisposeResources()
        {
            if (_collectPagesRenderPass != null)
            {
                _collectPagesRenderPass.Dispose();
                _collectPagesRenderPass = null;
            }

            if (_renderPagesRenderPass != null)
            {
                _renderPagesRenderPass.Dispose();
                _renderPagesRenderPass = null;
            }

            if (_pageTableManager != null)
            {
                _pageTableManager.Dispose();
                _pageTableManager = null;
            }
        }

        private static Camera FindMainLightCamera()
        {
            var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (var light in lights)
            {
                if (light.type != LightType.Directional) continue;
                var lightCamera = light.GetComponent<Camera>();
                if (!lightCamera) continue;
                lightCamera.aspect = 1.0f;
                return lightCamera;
            }

            Debug.Log("No main light camera found!");
            return null;
        }
    }
}
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace vsm
{
    public class VSMDebugUI : MonoBehaviour
    {
        private TextMeshProUGUI _vsmStatusText;
        private RawImage _phyPageImage;
        private RawImage[] _virPageStatusImages;

        private void Start()
        {
            if (VSMDebugData.VSMConfig == null) return;
            var canvas = CreateCanvasAndEventSystemIfNeeded();
            
            _vsmStatusText = CreateStatusPanel(canvas.transform);
            _phyPageImage = CreatePhyPageImage(canvas.transform);
            _virPageStatusImages = CreateVirPageStatusImages(canvas.transform);
        }

        public void Update()
        {
            if (_vsmStatusText && VSMDebugData.PageStat != null)
            {
                _vsmStatusText.text = VSMDebugData.PageStat.ToString();
            }

            // TODO
            {
                _phyPageImage.texture = VSMDebugData.PhyPageTexture;

                if (VSMDebugData.VirPageStatusTextures != null)
                {
                    for (var i = 0; i < _virPageStatusImages.Length; i++)
                    {
                        if (i < VSMDebugData.VirPageStatusTextures.Length)
                        {
                            _virPageStatusImages[i].texture = VSMDebugData.VirPageStatusTextures[i];
                        }
                    }
                }
            }
        }

        private void OnDestroy()
        {
            if (_phyPageImage && _phyPageImage.material)
            {
                if (Application.isPlaying) Destroy(_phyPageImage.material);
                else DestroyImmediate(_phyPageImage.material);
            }
        }
        
        private static Canvas CreateCanvasAndEventSystemIfNeeded()
        {
            var canvas = FindObjectOfType<Canvas>();
            if (canvas) return canvas;
            var canvasGo = new GameObject("Canvas");
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            if (FindObjectOfType<EventSystem>()) return canvas;
            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<EventSystem>();
            eventSystemGo.AddComponent<StandaloneInputModule>();
            return canvas;
        }
        
        private static TextMeshProUGUI CreateStatusPanel(Transform parent)
        {
            var panelGo = new GameObject("VSMStatusPanel");
            {
                panelGo.transform.SetParent(parent, false);
                
                var image = panelGo.AddComponent<Image>();
                image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
                image.type = Image.Type.Sliced;
                image.color = new Color(1.0f, 1.0f, 1.0f, 0.5f);

                var rt = panelGo.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 1);
                rt.anchorMax = new Vector2(0, 1);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(215, -120);
                rt.sizeDelta = new Vector2(420, 220);
            }

            var textGo = new GameObject("VSMStatus");
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            {
                textGo.transform.SetParent(panelGo.transform, false);
                
                tmp.text = "VSM Status Info...\nLine 2\nLine 3";
                tmp.fontSize = 24;
                tmp.color = Color.white;
                tmp.alignment = TextAlignmentOptions.MidlineLeft;

                var rt = textGo.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 1);
                rt.anchorMax = new Vector2(0, 1);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(210, -110);
                rt.sizeDelta = new Vector2(400, 200);
            }

            return tmp;
        }

        private static RawImage CreatePhyPageImage(Transform parent)
        {
            var visPhyTextureMat = new Material(Shader.Find("VSM/VisPhyTexture"))
            {
                name = "VisPhyTexture_Mat"
            };

            var gridSize = VSMDebugData.VSMConfig.physicalTextureGridSize;
            const float spacing = 20.0f;
            const float width = 400.0f;
            var height = width * gridSize.y / gridSize.x;
            var phyPageImage = CreateRawImage(
                parent,
                "PhyPages",
                new Vector2(1, 0),
                new Vector2(1, 0),
                new Vector2(-(width * 0.5f + spacing), height * 0.5f + spacing),
                new Vector2(width, height),
                visPhyTextureMat
            );
            return phyPageImage;
        }

        private static RawImage[] CreateVirPageStatusImages(Transform parent)
        {
            const int mipMaxSize = 256;
            const float spacing = 20f;
            var xPos = spacing + mipMaxSize * 0.5f;
            var yPos = spacing + mipMaxSize * 0.5f;
            var mipCount = VSMDebugData.VSMConfig.GetMipCount();
            var virPageStatusImages = new RawImage[mipCount];
            for (var i = 0; i < mipCount; i++)
            {
                var mipName = $"PageAllocation_Mip{i}";
                var currMipSize = mipMaxSize / Mathf.Pow(2, i);
                
                virPageStatusImages[i] = CreateRawImage(
                    parent,
                    mipName,
                    new Vector2(0, 0),
                    new Vector2(0, 0),
                    new Vector2(xPos, yPos),
                    new Vector2(currMipSize, currMipSize),
                    null
                );

                xPos += spacing + currMipSize * 0.75f;
                yPos = spacing + currMipSize * 0.25f;
            }

            return virPageStatusImages;
        }
        
        private static RawImage CreateRawImage(Transform parent, string rawImageName, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 anchoredPos, Vector2 sizeDelta, Material mat)
        {
            var go = new GameObject(rawImageName);
            go.transform.SetParent(parent, false);

            var rawImage = go.AddComponent<RawImage>();
            rawImage.color = Color.white;
            
            if (mat)
            {
                rawImage.material = mat;
            }
            
            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = anchorMin;
            rectTransform.anchorMax = anchorMax;
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.anchoredPosition = anchoredPos;
            rectTransform.sizeDelta = sizeDelta;
            
            return rawImage;
        }
    }
}
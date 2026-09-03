using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace vsm.Editor
{
    public class VSMSetupEditorWindow : EditorWindow
    {
        private GUIStyle _paddingBox;
        private Light _light;
        private float _aspect = 1.0f;
        private float _boxSize = 20.0f;
        private float _boxLength = 100.0f;
        private float _depthOffset = 50.0f;

        [MenuItem("VSM/VSMSetupEditorWindow")]
        private static void CreateWindow()
        {
            GetWindow<VSMSetupEditorWindow>(nameof(VSMSetupEditorWindow));
        }

        public void OnEnable()
        {
            _paddingBox = new GUIStyle
            {
                padding = new RectOffset(15, 15, 15, 15)
            };
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginVertical(_paddingBox);

            GUILayout.Label("Light Camera Setup", EditorStyles.boldLabel);
            _light = (Light)EditorGUILayout.ObjectField(_light, typeof(Light), true);
            _aspect = EditorGUILayout.FloatField("Aspect", _aspect);
            _boxSize = EditorGUILayout.FloatField("Box Size", _boxSize);
            _boxLength = EditorGUILayout.FloatField("Box Length", _boxLength);
            _depthOffset = EditorGUILayout.FloatField("Depth Offset", _depthOffset);

            if (GUILayout.Button("Create Light Camera"))
            {
                CreateLightCamera();
            }

            if (GUILayout.Button("Add Mesh Colliders"))
            {
                var meshFilters = Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
                Debug.Log($"Num Meshes: {meshFilters.Length}");
                int count = 0;
                foreach (var meshFilter in meshFilters)
                {
                    try
                    {
                        if (meshFilter.GetComponent<MeshCollider>() != null) continue;
                        var meshCollider = meshFilter.gameObject.AddComponent<MeshCollider>();
                        meshCollider.convex = true;
                        count++;
                    }
                    catch (Exception e)
                    {
                        continue;
                    }
                }
                Debug.Log($"Added: {count}");
            }

            EditorGUILayout.EndVertical();
        }

        private void CreateLightCamera()
        {
            if (!_light)
            {
                Debug.LogWarning("Light is null!");
                return;
            }

            if (_light.type != LightType.Directional)
            {
                Debug.LogWarning("Light type is not directional!");
                return;
            }

            var cam = _light.gameObject.GetComponent<Camera>();
            if (cam)
            {
                Debug.Log("Camera already exists!");
            }
            else
            {
                cam = _light.gameObject.AddComponent<Camera>();
                Undo.RegisterCreatedObjectUndo(cam, "Create Light Camera");
            }

            cam.enabled = false;
            cam.orthographic = true;
            cam.aspect = 1.0f;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.backgroundColor = Color.black;
            cam.orthographicSize = _boxSize * 0.5f;
            var halfLen = _boxLength * 0.5f;
            cam.nearClipPlane = -halfLen + _depthOffset;
            cam.farClipPlane = halfLen + _depthOffset;

            Debug.Log("Camera created/updated!");
        }
    }
}
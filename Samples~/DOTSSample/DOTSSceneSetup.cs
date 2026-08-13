using UnityEngine;

namespace DOTSSample
{
    /// <summary>
    /// One-click scene bootstrap: creates camera, light, ground plane, and the DOTS
    /// spawner. Attach to any GameObject in an empty scene — or use the menu item.
    /// </summary>
    public sealed class DOTSSceneSetup : MonoBehaviour
    {
        private void Awake()
        {
            // Camera — top-down orthographic
            if (Camera.main == null)
            {
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                var cam = camGo.AddComponent<Camera>();
                cam.transform.position = new Vector3(0f, 20f, 0f);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.orthographic = true;
                cam.orthographicSize = 12f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.08f, 0.08f, 0.12f);
            }

            // Directional light
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            light.intensity = 1.2f;

            // Ground plane (GameObject, not ECS)
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(2.5f, 1f, 2.5f);
            var groundMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ??
                                         Shader.Find("Standard"));
            groundMat.color = new Color(0.15f, 0.18f, 0.22f);
            ground.GetComponent<Renderer>().material = groundMat;

            // DOTS Spawner
            var spawner = gameObject.GetComponent<DOTSSpawner>();
            if (spawner == null)
                gameObject.AddComponent<DOTSSpawner>();

            Debug.Log("[DOTSSceneSetup] Scene ready — 5 ECS entities will spawn on Start");
        }
    }
}

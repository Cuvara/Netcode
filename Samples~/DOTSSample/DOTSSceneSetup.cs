using UnityEngine;

namespace DOTSSample
{
    /// <summary>
    /// One-click scene bootstrap: creates camera, light, ground plane, and the DOTS
    /// spawner. Attach to any GameObject in an empty scene — or use the menu item.
    /// </summary>
    public sealed class DOTSSceneSetup : MonoBehaviour
    {
        [Header("Network")]
        [Tooltip("Maps the network bridge offers at startup. One entry connects to it " +
                 "straight away; two or more draw the map selector and wait for a click. " +
                 "Ignored if the GameObject already carries a DOTSNetworkBridge — that " +
                 "one keeps its own inspector values.")]
        [SerializeField] private string[] availableMaps = { "map_01", "map_02" };

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
            var groundMat = Resources.Load<Material>("DOTSGroundMaterial");
            if (groundMat != null)
                ground.GetComponent<Renderer>().material = groundMat;

            // Combat — enemies, bullets, auto-attack
            var combat = gameObject.GetComponent<CombatBootstrap>();
            if (combat == null)
                gameObject.AddComponent<CombatBootstrap>();

            // Network bridge — connects to gateway → game server and renders
            // replicated entities as ECS entities alongside the local demo ones.
            // A component added here carries only its field initializers, so the map set
            // has to be handed over explicitly; without that the scene could never
            // configure it and the selector would always appear.
            var bridge = gameObject.GetComponent<DOTSNetworkBridge>();
            if (bridge == null)
            {
                bridge = gameObject.AddComponent<DOTSNetworkBridge>();
                bridge.ConfigureMaps(availableMaps);
            }

            Debug.Log("[DOTSSceneSetup] Scene ready — DOTS entities + combat + network bridge");
        }
    }
}

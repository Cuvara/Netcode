using UnityEngine;
using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Bootstrap
{
    /// <summary>
    /// Everything <see cref="NetworkBootstrap"/> needs to reach a local backend,
    /// in one asset so it can be changed without touching a scene or recompiling.
    /// </summary>
    /// <remarks>
    /// Defaults match the backend's own defaults: gateway <c>:8000</c>,
    /// <c>JWT_SECRET=dev-secret-change-me</c>, <c>--map-id=map_01</c>. The game
    /// server address is deliberately absent — the gateway hands it back from
    /// <c>enter_world</c>, and hardcoding it here would be the client bypassing the
    /// assignment step it exists to test (ADR-3).
    /// </remarks>
    [CreateAssetMenu(
        fileName = "NetworkBootstrapConfig",
        menuName = "RPG MMO/Network Bootstrap Config",
        order = 0)]
    public sealed class NetworkBootstrapConfig : ScriptableObject
    {
        [Header("Gateway")]
        [Tooltip("Host of the gateway's auth listener. The game server address comes from enter_world, not from here.")]
        [SerializeField] private string gatewayHost = "127.0.0.1";

        [Tooltip("Gateway port. The backend default is 8000 (GATEWAY_ADDR=:8000).")]
        [SerializeField] private int gatewayPort = 8000;

        [Header("Identity (development only)")]
        [Tooltip("Subject claim of the minted JWT. Any string the gateway has not already got a live session for.")]
        [SerializeField] private string userId = "dev-player";

        [Tooltip("HS256 secret shared with the gateway. The backend default is JWT_SECRET=dev-secret-change-me. " +
                 "In production Nakama issues the token and the client never sees a secret.")]
        [SerializeField] private string jwtSecret = "dev-secret-change-me";

        [Tooltip("Lifetime of the minted token in seconds.")]
        [SerializeField] private int jwtLifetimeSeconds = 3600;

        [Header("World")]
        [Tooltip("Map to request. The backend default is --map-id=map_01.")]
        [SerializeField] private string mapId = "map_01";

        [Header("Input")]
        [Tooltip("Input sends per second. Matches the server's simulation rate; GameConstants.DefaultTickRate is 15.")]
        [SerializeField] private int inputRateHz = GameConstants.DefaultTickRate;

        [Tooltip("Send a slow circular movement direction so positions visibly change. " +
                 "Off sends a zero vector, which the server treats as 'no movement'.")]
        [SerializeField] private bool sendSyntheticInput = true;

        [Header("Logging")]
        [Tooltip("Log every Nth snapshot. Every snapshot at 15 Hz floods the console; 15 is roughly one line a second.")]
        [SerializeField] private int snapshotLogInterval = 15;

        [Tooltip("Connect on Start. Off means calling Connect() yourself from the inspector or another script.")]
        [SerializeField] private bool connectOnStart = true;

        public string GatewayHost => gatewayHost;

        public int GatewayPort => gatewayPort;

        public string UserId => userId;

        public string JwtSecret => jwtSecret;

        public int JwtLifetimeSeconds => jwtLifetimeSeconds < 1 ? 3600 : jwtLifetimeSeconds;

        public string MapId => mapId;

        public int InputRateHz => inputRateHz < 1 ? GameConstants.DefaultTickRate : inputRateHz;

        public bool SendSyntheticInput => sendSyntheticInput;

        public int SnapshotLogInterval => snapshotLogInterval < 1 ? 1 : snapshotLogInterval;

        public bool ConnectOnStart => connectOnStart;
    }
}

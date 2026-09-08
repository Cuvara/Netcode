using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Json;
using Cuvara.Netcode.Prediction;
using UnityEngine;
using UnityEngine.Networking;

namespace Cuvara.Netcode.Tests.PlayMode
{
    /// <summary>
    /// Where the live backend is, for the PlayMode measurements. Every value can be
    /// overridden by an environment variable so a run can be pointed elsewhere without
    /// editing and recompiling the test.
    /// </summary>
    public static class LiveBackendConfig
    {
        public static string GatewayHost => Env("CUVARA_GATEWAY_HOST", "127.0.0.1");
        public static int GatewayPort => EnvInt("CUVARA_GATEWAY_PORT", 8000);
        public static string NakamaScheme => Env("CUVARA_NAKAMA_SCHEME", "http");
        public static string NakamaHost => Env("CUVARA_NAKAMA_HOST", "127.0.0.1");
        public static int NakamaPort => EnvInt("CUVARA_NAKAMA_PORT", 7350);
        public static string NakamaServerKey => Env("CUVARA_NAKAMA_SERVER_KEY", "defaultkey");
        public static string MapId => Env("CUVARA_MAP_ID", "map_01");

        /// <summary>
        /// Fallback tick rate, used only when the server advertises none in its join
        /// response. Not the rate the measurement predicts at, and NOT the rate it sends at.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This constant used to be the rate, and it was wrong: the server moved to a
        /// 60 Hz base tick and this still read 15, so the harness predicted a step four
        /// times too long and reported the result as a measurement. Every configuration
        /// now builds its <c>PredictionSettings</c> from <c>client.TickRate</c> after
        /// connecting, and prints whether it fell back to this. Leave it alone unless
        /// testing the fallback path itself.
        /// </para>
        /// <para>
        /// <b>It was also, separately, the harness's send cadence</b> — one property serving
        /// a prediction rate and a wire cadence, which is the same conflation that put 15 Hz
        /// on the wire against a 15 Hz snapshot stream and made the acknowledgement floor
        /// unmeasurable. The two are now <see cref="InputSendHz"/> and this. The environment
        /// variable keeps its name and its meaning: it overrides the fallback tick rate,
        /// which is what its documentation always said it did.
        /// </para>
        /// </remarks>
        public static int FallbackTickRate => EnvInt("CUVARA_TICK_RATE", 15);

        /// <summary>
        /// The rate snapshots are expected to arrive at — the server's <c>WorldHz</c>, not
        /// the <c>tick_rate</c> it advertises (which is <c>MovementHz</c>, 60 by default).
        /// </summary>
        public static int SnapshotRateHz => EnvInt("CUVARA_SNAPSHOT_HZ", 15);

        /// <summary>
        /// How often the harness sends input. <b>Deliberately offset from
        /// <see cref="SnapshotRateHz"/>.</b>
        /// </summary>
        /// <remarks>
        /// A client sending at exactly the snapshot rate locks the two cadences in phase, so
        /// every acknowledgement observation carries the same fixed wait and
        /// <c>AckLatencyEstimator</c> correctly refuses to offer a floor — measured on this
        /// very harness at <c>median 61.9 ms, p90 62.7, min 23.7</c>, a p10-to-median span of
        /// 0.28 base ticks. The default here comes from
        /// <c>InputCadence.RecommendedSendHz</c> so it stays offset if the snapshot rate is
        /// reconfigured. See <c>InputCadence</c> for why 13 and not 12 or 14.
        /// </remarks>
        public static int InputSendHz =>
            EnvInt("CUVARA_INPUT_SEND_HZ", InputCadence.RecommendedSendHz(SnapshotRateHz));

        /// <summary>
        /// The server's <c>ServerDefaults.DefaultPlayerSpeed</c>. Only the fallback since
        /// the wire carries speed (field 9); the measurement asserts the value actually
        /// used matches the server, so a drift here is caught rather than absorbed.
        /// </summary>
        public static float PlayerSpeed => EnvFloat("CUVARA_PLAYER_SPEED", 5f);

        public static string Describe() =>
            $"gateway {GatewayHost}:{GatewayPort}, nakama {NakamaScheme}://{NakamaHost}:{NakamaPort}, " +
            $"map {MapId}, inputSend {InputSendHz}Hz vs snapshots {SnapshotRateHz}Hz, " +
            $"fallbackTickRate {FallbackTickRate}, fallbackSpeed {PlayerSpeed}";

        private static string Env(string key, string fallback)
        {
            var v = Environment.GetEnvironmentVariable(key);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static int EnvInt(string key, int fallback) =>
            int.TryParse(Env(key, null), out var v) ? v : fallback;

        private static float EnvFloat(string key, float fallback) =>
            float.TryParse(Env(key, null), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>
    /// Minimal Nakama device authentication — enough to obtain a gateway JWT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Duplicated from <c>Samples~/DOTSSample/SampleNakamaAuth.cs</c>, unavoidably.</b>
    /// A <c>Samples~</c> folder is excluded from Unity's import, so its code cannot be
    /// referenced from an assembly — there is no way to share this without promoting it
    /// into <c>Runtime/</c>, which would put a test-and-sample convenience into the
    /// shipped package. Kept deliberately small so the duplication stays cheap: if the
    /// auth flow changes, both copies fail the same way, loudly, on the next run.
    /// </para>
    /// </remarks>
    public sealed class NakamaDeviceAuth
    {
        private readonly string _baseUrl;
        private readonly string _basicAuth;

        public NakamaDeviceAuth()
        {
            _baseUrl = $"{LiveBackendConfig.NakamaScheme}://{LiveBackendConfig.NakamaHost}:{LiveBackendConfig.NakamaPort}";
            _basicAuth = "Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(LiveBackendConfig.NakamaServerKey + ":"));
        }

        public string UserId { get; private set; } = string.Empty;

        public async UniTask<string> GetGatewayTokenAsync(string deviceId, CancellationToken ct)
        {
            var authJson = await PostAsync(
                "/v2/account/authenticate/device?create=true",
                _basicAuth,
                "{\"id\":\"" + deviceId + "\"}",
                ct);

            var sessionToken = JsonParser.Parse(authJson).GetString("token");
            if (string.IsNullOrEmpty(sessionToken))
            {
                throw new InvalidOperationException(
                    "Nakama device auth returned no session token. Response: " + authJson);
            }

            // Body is the JSON string "{}" — an empty string is rejected by the RPC.
            // Copied from the sample rather than reconstructed; this is the one part of
            // the flow whose shape cannot be checked at compile time.
            var rpcJson = await PostAsync(
                "/v2/rpc/gateway_token", "Bearer " + sessionToken, "\"{}\"", ct);

            // The RPC payload is a JSON string containing JSON.
            var payload = JsonParser.Parse(rpcJson).GetString("payload");
            if (string.IsNullOrEmpty(payload))
            {
                throw new InvalidOperationException(
                    "gateway_token RPC returned no payload. Response: " + rpcJson);
            }

            var inner = JsonParser.Parse(payload);
            UserId = inner.GetString("user_id");

            var jwt = inner.GetString("token");
            if (string.IsNullOrEmpty(jwt))
            {
                throw new InvalidOperationException(
                    "gateway_token RPC returned no 'token' field. Payload: " + payload);
            }

            return jwt;
        }

        private async UniTask<string> PostAsync(string path, string auth, string body, CancellationToken ct)
        {
            using (var request = new UnityWebRequest(_baseUrl + path, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 10;
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", auth);

                await request.SendWebRequest().WithCancellation(ct);

                if (request.result != UnityWebRequest.Result.Success)
                {
                    // Named rather than generic: "the backend is down" and "the backend
                    // rejected us" are different problems and this is the only place that
                    // can tell them apart.
                    throw new InvalidOperationException(
                        $"POST {path} failed: {request.result} {request.responseCode} {request.error}. " +
                        "Is the backend up? " + LiveBackendConfig.Describe());
                }

                return request.downloadHandler.text ?? string.Empty;
            }
        }
    }

    /// <summary>Records what a view was told, so a measurement can watch it.</summary>
    public sealed class ProbeView : Cuvara.Netcode.View.IEntityView
    {
        private readonly System.Collections.Generic.Dictionary<string, Vector2> _positions =
            new System.Collections.Generic.Dictionary<string, Vector2>();

        public int SetStateCalls { get; private set; }

        /// <summary>
        /// Every distinct position this view was told for the tracked id, in order.
        /// </summary>
        /// <remarks>
        /// The evidence that distinguishes "the server never moved the entity" from "the
        /// harness never noticed it moving". Those two produce an identical report — no
        /// usable samples, 100% still frames — and no counter already present separates
        /// them, which is why a run that measured nothing could not say why.
        /// </remarks>
        public readonly System.Collections.Generic.List<Vector2> TrackedPositions =
            new System.Collections.Generic.List<Vector2>();

        /// <summary>Id whose raw positions are recorded into <see cref="TrackedPositions"/>.</summary>
        public string TrackedId { get; set; } = string.Empty;

        /// <summary>Distinct positions seen for the tracked id. One means it never moved.</summary>
        public int DistinctTrackedPositions => TrackedPositions.Count;

        public void Spawn(string id, bool isLocal, string type) { }

        public void Despawn(string id) => _positions.Remove(id);

        public void SetState(string id, float x, float y, int hp, int maxHp)
        {
            SetStateCalls++;
            var position = new Vector2(x, y);

            if (!string.IsNullOrEmpty(TrackedId) && id == TrackedId &&
                (TrackedPositions.Count == 0 || TrackedPositions[TrackedPositions.Count - 1] != position))
            {
                TrackedPositions.Add(position);
            }

            _positions[id] = position;
        }

        /// <summary>
        /// The position this view would render for <paramref name="id"/> — the number a
        /// player actually sees, after prediction and smoothing, not the raw snapshot.
        /// </summary>
        public bool TryGet(string id, out Vector2 position) => _positions.TryGetValue(id, out position);
    }
}

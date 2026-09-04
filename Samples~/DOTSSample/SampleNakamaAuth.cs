using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace DOTSSample
{
    /// <summary>
    /// Obtains a gateway JWT from Nakama over plain HTTP, with no dependency on the
    /// Nakama SDK and nothing outside this package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately duplicated per sample.</b> Package Manager imports each sample
    /// independently, so a single shared copy outside the sample folders would simply not
    /// be imported — and two copies of the same class in the SAME namespace would collide
    /// for anyone importing both samples. Each sample therefore carries its own copy in
    /// its own namespace. Keep them in sync by hand; they are ~100 lines.
    /// </para>
    /// <para>
    /// A real application should not do this. It should implement
    /// <c>Cuvara.Netcode.Auth.IAuthProvider</c> and let the container supply it — see
    /// `Documentation~/NETCODE.md`. This is a sample's shortcut, not a pattern.
    /// </para>
    /// </remarks>
    public sealed class SampleNakamaAuth
    {
        private readonly string _baseUrl;
        private readonly string _basicAuth;

        public SampleNakamaAuth(
            string scheme = "http", string host = "127.0.0.1", int port = 7350,
            string serverKey = "defaultkey")
        {
            _baseUrl = $"{scheme}://{host}:{port}";
            _basicAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(serverKey + ":"));
        }

        public string UserId { get; private set; } = string.Empty;

        public string SessionToken { get; private set; } = string.Empty;

        /// <summary>
        /// The refresh token from the same response as <see cref="SessionToken"/>, if the
        /// server sent one.
        /// </summary>
        public string RefreshToken { get; private set; } = string.Empty;

        /// <summary>
        /// When <see cref="SessionToken"/> stops being accepted, from its own <c>exp</c>
        /// claim, or <see cref="DateTime.MinValue"/> if it could not be read.
        /// </summary>
        public DateTime SessionExpiresUtc { get; private set; } = DateTime.MinValue;

        /// <summary>Times the session was refreshed rather than re-authenticated.</summary>
        public int Refreshes { get; private set; }

        /// <summary>Times the refresh failed and a full device re-authentication was needed.</summary>
        public int Reauthentications { get; private set; }

        private string _deviceId = string.Empty;

        /// <summary>
        /// How long before expiry the session is renewed.
        /// </summary>
        /// <remarks>
        /// Nakama's default <c>session.token_expiry_sec</c> is <b>60</b>, so a client that
        /// authenticates once and never speaks again has a dead token a minute later. That is
        /// every session. Renewing ten seconds early costs one request a minute and removes
        /// the window where a user-visible request is the thing that discovers the expiry.
        /// </remarks>
        private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Renew the session if it is close to expiry, so the caller's next request carries a
        /// token the server will still accept.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Call this before any Nakama request. It is cheap when there is nothing to do: it
        /// compares two <see cref="DateTime"/>s and returns.
        /// </para>
        /// <para>
        /// The refresh token expires too — <c>session.refresh_token_expiry_sec</c>, an hour by
        /// default — so a refresh that fails falls back to a full device authentication rather
        /// than leaving the client dead. Both paths are counted, because "refreshing every
        /// minute" and "re-authenticating every minute" look the same from the outside and
        /// mean different things.
        /// </para>
        /// </remarks>
        public async UniTask EnsureSessionAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(SessionToken) || string.IsNullOrEmpty(_deviceId))
            {
                return;   // nothing authenticated yet; GetGatewayTokenAsync does that
            }

            if (SessionExpiresUtc != DateTime.MinValue &&
                DateTime.UtcNow + RenewBefore < SessionExpiresUtc)
            {
                return;
            }

            if (!string.IsNullOrEmpty(RefreshToken))
            {
                try
                {
                    var body = "{\"token\":\"" + Escape(RefreshToken) + "\"}";
                    var json = await PostAsync("/v2/account/session/refresh", _basicAuth, body, ct);
                    ApplySession(json);
                    Refreshes++;
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        "[NakamaAuth] Session refresh failed, re-authenticating from the device " +
                        "id: " + ex.Message);
                }
            }

            var authJson = await PostAsync(
                "/v2/account/authenticate/device?create=true",
                _basicAuth, "{\"id\":\"" + Escape(_deviceId) + "\"}", ct);
            ApplySession(authJson);
            Reauthentications++;
        }

        /// <summary>
        /// Take the session out of an authenticate or refresh response.
        /// </summary>
        private void ApplySession(string json)
        {
            var root = JsonParser.Parse(json);
            var token = root.GetString("token");
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException(
                    "Nakama returned no session token. Response: " + json);

            SessionToken = token;
            RefreshToken = root.GetString("refresh_token");
            SessionExpiresUtc = ReadExpiry(token);
        }

        /// <summary>
        /// The <c>exp</c> claim of a JWT, as UTC, or <see cref="DateTime.MinValue"/> if it
        /// cannot be read.
        /// </summary>
        /// <remarks>
        /// Read from the token rather than assumed, because the expiry is a server setting and
        /// a client that hardcodes 60 seconds is wrong on every deployment that changed it.
        /// Unreadable is not fatal: the caller then renews on the schedule its own polling
        /// gives it, which is worse than knowing and better than nothing.
        /// </remarks>
        private static DateTime ReadExpiry(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return DateTime.MinValue;

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                var exp = JsonParser.Parse(json).GetLong("exp");
                return exp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime
                    : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>The gateway JWT from the last successful call, reused while valid.</summary>
        public string GatewayToken { get; private set; }

        /// <summary>Expiry of <see cref="GatewayToken"/> (its <c>exp</c> claim), UTC.</summary>
        public DateTime GatewayTokenExpiresUtc { get; private set; } = DateTime.MinValue;

        /// <summary>How many calls were answered from the cached gateway token.</summary>
        public int GatewayTokenReuses { get; private set; }

        public async UniTask<string> GetGatewayTokenAsync(string deviceId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(deviceId))
                throw new ArgumentException("device id is required", nameof(deviceId));

            // The cached token first. The gateway JWT lives an hour; before this
            // check every reconnect was a COLD login — device auth (Postgres) +
            // profile hook + the rate-limited gateway_token RPC (burst 5) — so a
            // server restart with N clients produced ~2N Nakama HTTP calls for
            // credentials almost all of them already held, and more than five
            // network flaps in an hour turned into a login failure (#54).
            if (deviceId == _deviceId &&
                !string.IsNullOrEmpty(GatewayToken) &&
                GatewayTokenExpiresUtc != DateTime.MinValue &&
                DateTime.UtcNow + TimeSpan.FromSeconds(30) < GatewayTokenExpiresUtc)
            {
                GatewayTokenReuses++;
                return GatewayToken;
            }

            _deviceId = deviceId;

            // Reuse the Nakama session too when it is still fresh: EnsureSessionAsync
            // refreshes or re-auths only when needed, so the common reconnect costs
            // one RPC, not a device auth plus an RPC.
            string sessionToken;
            if (!string.IsNullOrEmpty(SessionToken))
            {
                await EnsureSessionAsync(ct);
                sessionToken = SessionToken;
            }
            else
            {
                var authBody = "{\"id\":\"" + Escape(deviceId) + "\"}";
                var authJson = await PostAsync(
                    "/v2/account/authenticate/device?create=true", _basicAuth, authBody, ct);
                ApplySession(authJson);
                sessionToken = SessionToken;
            }

            var rpcJson = await PostAsync(
                "/v2/rpc/gateway_token", "Bearer " + sessionToken, "\"{}\"", ct);

            var payload = JsonParser.Parse(rpcJson).GetString("payload");
            if (string.IsNullOrEmpty(payload))
                throw new InvalidOperationException(
                    "gateway_token RPC returned no payload. Response: " + rpcJson);

            var inner = JsonParser.Parse(payload);
            var gatewayToken = inner.GetString("token");
            UserId = inner.GetString("user_id");

            if (string.IsNullOrEmpty(gatewayToken))
                throw new InvalidOperationException(
                    "gateway_token RPC returned no 'token' field. Payload: " + payload);

            GatewayToken = gatewayToken;
            GatewayTokenExpiresUtc = ReadExpiry(gatewayToken);
            return gatewayToken;
        }

        private async UniTask<string> PostAsync(
            string path, string authorization, string body, CancellationToken ct)
        {
            using (var request = new UnityWebRequest(_baseUrl + path, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", authorization);

                await request.SendWebRequest().WithCancellation(ct);

                if (request.result != UnityWebRequest.Result.Success)
                    throw new InvalidOperationException(
                        $"POST {path} failed: {request.result} {request.responseCode} {request.error}");

                return request.downloadHandler.text ?? string.Empty;
            }
        }

        private static string Escape(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

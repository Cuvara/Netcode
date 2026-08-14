using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Samples.WorldView
{
    /// <summary>
    /// Obtains a gateway JWT from Nakama over plain HTTP, with no dependency on the
    /// Nakama SDK and nothing outside this package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the earlier version of these samples used
    /// <c>Scripts.Nakama.NakamaSessionService</c> from the host project's
    /// <c>Assets/</c>, which meant the samples shipped in <c>@cuvara/netcode</c> could
    /// not compile for anyone outside this repository. `UnityWebRequest` plus the
    /// package's own <see cref="JsonParser"/> covers both calls, so a consumer can import
    /// the sample and run it.
    /// </para>
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

            // Nakama's server key goes in HTTP Basic as "<key>:" — key as the username,
            // EMPTY password, so the trailing colon is load-bearing.
            _basicAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(serverKey + ":"));
        }

        /// <summary>User id Nakama resolved for the device, available after the call.</summary>
        public string UserId { get; private set; } = string.Empty;

        /// <summary>
        /// The Nakama SESSION token. Exposed only so a sample can demonstrate that
        /// presenting it to the gateway is wrong — it may be accepted while resolving an
        /// empty user id. Never send this to the gateway in real code.
        /// </summary>
        public string SessionToken { get; private set; } = string.Empty;

        /// <summary>
        /// Authenticates a device id and exchanges the session for a gateway-signed JWT.
        /// </summary>
        /// <remarks>
        /// The gateway token is a DIFFERENT credential from the Nakama session token: the
        /// session authenticates to Nakama, while the gateway token carries the user in
        /// the <c>sub</c> claim the gateway reads. Presenting the session token to the
        /// gateway is not a clean failure — it can be accepted while resolving an EMPTY
        /// user id.
        /// </remarks>
        public async UniTask<string> GetGatewayTokenAsync(string deviceId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                throw new ArgumentException("device id is required", nameof(deviceId));
            }

            var authBody = "{\"id\":\"" + Escape(deviceId) + "\"}";
            var authJson = await PostAsync(
                "/v2/account/authenticate/device?create=true", _basicAuth, authBody, ct);

            var sessionToken = JsonParser.Parse(authJson).GetString("token");
            SessionToken = sessionToken;
            if (string.IsNullOrEmpty(sessionToken))
            {
                throw new InvalidOperationException(
                    "Nakama device authentication returned no session token. Response: " + authJson);
            }

            // Body is a JSON *string literal* — Nakama wraps RPC payloads, so "{}" is the
            // empty-object argument encoded as a string, not a bare object.
            var rpcJson = await PostAsync(
                "/v2/rpc/gateway_token", "Bearer " + sessionToken, "\"{}\"", ct);

            // TWO parses here, and this is the opposite of the Unity SDK case. Over raw
            // HTTP the response is an envelope whose `payload` is a JSON-ENCODED STRING:
            //     {"payload":"{\"token\":\"...\",\"user_id\":\"...\"}"}
            // so the envelope is parsed, then `payload` is parsed again as JSON. The SDK's
            // IApiRpc.Payload has already unwrapped the envelope and needs only ONE parse.
            // Getting this backwards yields an empty token either way, which is why it is
            // spelled out.
            var payload = JsonParser.Parse(rpcJson).GetString("payload");
            if (string.IsNullOrEmpty(payload))
            {
                throw new InvalidOperationException(
                    "gateway_token RPC returned no payload. Response: " + rpcJson);
            }

            var inner = JsonParser.Parse(payload);
            var gatewayToken = inner.GetString("token");
            UserId = inner.GetString("user_id");

            if (string.IsNullOrEmpty(gatewayToken))
            {
                throw new InvalidOperationException(
                    "gateway_token RPC returned no 'token' field. Payload: " + payload);
            }

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
                {
                    throw new InvalidOperationException(
                        $"POST {path} failed: {request.result} {request.responseCode} {request.error}");
                }

                return request.downloadHandler.text ?? string.Empty;
            }
        }

        private static string Escape(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

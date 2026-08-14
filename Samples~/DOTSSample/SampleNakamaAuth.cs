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

        public async UniTask<string> GetGatewayTokenAsync(string deviceId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(deviceId))
                throw new ArgumentException("device id is required", nameof(deviceId));

            var authBody = "{\"id\":\"" + Escape(deviceId) + "\"}";
            var authJson = await PostAsync(
                "/v2/account/authenticate/device?create=true", _basicAuth, authBody, ct);

            var sessionToken = JsonParser.Parse(authJson).GetString("token");
            SessionToken = sessionToken;
            if (string.IsNullOrEmpty(sessionToken))
                throw new InvalidOperationException(
                    "Nakama device authentication returned no session token. Response: " + authJson);

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

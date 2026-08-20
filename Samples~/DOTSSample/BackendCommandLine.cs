using System;
using UnityEngine;

namespace DOTSSample
{
    /// <summary>
    /// Reads the backend address out of the player's command line (env vars as a
    /// fallback), so one built player can be pointed at any gateway / Nakama / map
    /// without a rebuild.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Everything the built player knew about the backend was a
    /// field initializer on <see cref="DOTSNetworkBridge"/> — and not even an
    /// inspector-authored one: the scene carries only <see cref="DOTSSceneSetup"/>,
    /// which adds the bridge at runtime, so the component can never hold anything but
    /// its own defaults (127.0.0.1:8000, Nakama 127.0.0.1:7350). Pointing a player at a
    /// different backend therefore meant editing source and rebuilding. The game server
    /// is an Agones pod whose port is assigned at scheduling time, so the address is not
    /// a constant and a baked one is wrong by construction.
    /// </para>
    /// <para>
    /// <b>Read once, at startup, before the first connection.</b> Nothing here runs per
    /// frame and nothing here changes netcode behaviour; with no arguments every value
    /// is left exactly as it was.
    /// </para>
    /// <para>
    /// Env-var names match the ones the Editor live-backend tests already use
    /// (<c>CUVARA_GATEWAY_HOST</c> and friends, see
    /// <c>Tests/Runtime/LiveBackend.cs</c>) so one exported environment drives both.
    /// A command-line flag always wins over the environment.
    /// </para>
    /// </remarks>
    public static class BackendCommandLine
    {
        /// <summary>Resolved backend settings. Fields are left untouched when nothing overrides them.</summary>
        public struct Settings
        {
            public string GatewayHost;
            public int GatewayPort;
            public string MapId;
            public bool MapExplicit;
            public string NakamaScheme;
            public string NakamaHost;
            public int NakamaPort;
            public string NakamaServerKey;
            public bool NakamaExplicit;
            public string StatusUrl;
            public bool StatusUrlExplicit;
            public string DeviceId;
            public string InstanceLabel;

            public string NakamaBaseUrl => $"{NakamaScheme}://{NakamaHost}:{NakamaPort}";

            public string NakamaHealthUrl => NakamaBaseUrl + "/healthcheck";
        }

        /// <summary>
        /// Resolves settings from the command line, then the environment, then the
        /// defaults handed in by the caller.
        /// </summary>
        public static Settings Resolve(
            string defaultGatewayHost,
            int defaultGatewayPort,
            string defaultMapId,
            string defaultStatusUrl)
        {
            var args = SafeArgs();

            var s = new Settings
            {
                GatewayHost = Str(args, "-cuvara-gateway-host", "CUVARA_GATEWAY_HOST", defaultGatewayHost),
                GatewayPort = Int(args, "-cuvara-gateway-port", "CUVARA_GATEWAY_PORT", defaultGatewayPort),
                NakamaScheme = Str(args, "-cuvara-nakama-scheme", "CUVARA_NAKAMA_SCHEME", "http"),
                NakamaHost = Str(args, "-cuvara-nakama-host", "CUVARA_NAKAMA_HOST", "127.0.0.1"),
                NakamaPort = Int(args, "-cuvara-nakama-port", "CUVARA_NAKAMA_PORT", 7350),
                NakamaServerKey = Str(args, "-cuvara-nakama-key", "CUVARA_NAKAMA_SERVER_KEY", "defaultkey"),
                NakamaExplicit =
                    Str(args, "-cuvara-nakama-scheme", "CUVARA_NAKAMA_SCHEME", null) != null ||
                    Str(args, "-cuvara-nakama-host", "CUVARA_NAKAMA_HOST", null) != null ||
                    Str(args, "-cuvara-nakama-port", "CUVARA_NAKAMA_PORT", null) != null,
                DeviceId = Str(args, "-cuvara-device", "CUVARA_DEVICE_ID", null),
                InstanceLabel = Str(args, "-cuvara-instance", "CUVARA_INSTANCE", null),
            };

            var map = Str(args, "-cuvara-map", "CUVARA_MAP_ID", null);
            s.MapExplicit = !string.IsNullOrEmpty(map);
            s.MapId = s.MapExplicit ? map : defaultMapId;

            var status = Str(args, "-cuvara-status-url", "CUVARA_STATUS_URL", null);
            s.StatusUrlExplicit = !string.IsNullOrEmpty(status);
            s.StatusUrl = s.StatusUrlExplicit ? status : defaultStatusUrl;

            return s;
        }

        /// <summary>
        /// A device id that is unique per process, so two players on one machine can
        /// never authenticate as the same Nakama user.
        /// </summary>
        /// <remarks>
        /// Two instances sharing an identity is the failure mode that reads as success:
        /// the server evicts the first login, and what is left is one player looking at
        /// an empty world — the same picture a broken area-of-interest produces. An
        /// explicit <c>-cuvara-device</c> is honoured so a launcher can give each window
        /// a name that is greppable in the server logs; without one, the process id and
        /// the clock keep them apart.
        /// </remarks>
        public static string ResolveDeviceId(in Settings settings, string fallbackPrefix)
        {
            if (!string.IsNullOrEmpty(settings.DeviceId))
            {
                return settings.DeviceId;
            }

            var label = string.IsNullOrEmpty(settings.InstanceLabel) ? "1" : settings.InstanceLabel;
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            return $"{fallbackPrefix}-{label}-{pid}-{DateTime.UtcNow.Ticks}";
        }

        private static string[] SafeArgs()
        {
            try
            {
                return Environment.GetCommandLineArgs() ?? Array.Empty<string>();
            }
            catch (Exception)
            {
                // Some platforms (WebGL) deny the command line outright. The environment
                // fallback still works, so this must not be fatal.
                return Array.Empty<string>();
            }
        }

        private static string Str(string[] args, string flag, string envName, string fallback)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.Ordinal))
                {
                    var v = args[i + 1];
                    if (!string.IsNullOrEmpty(v))
                    {
                        return v;
                    }
                }
            }

            var fromEnv = SafeEnv(envName);
            return string.IsNullOrEmpty(fromEnv) ? fallback : fromEnv;
        }

        private static int Int(string[] args, string flag, string envName, int fallback)
        {
            var raw = Str(args, flag, envName, null);
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            if (int.TryParse(raw, out var parsed) && parsed > 0 && parsed <= 65535)
            {
                return parsed;
            }

            Debug.LogWarning($"[backend-args] {flag}='{raw}' is not a usable port — keeping {fallback}.");
            return fallback;
        }

        private static string SafeEnv(string name)
        {
            try
            {
                return Environment.GetEnvironmentVariable(name);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

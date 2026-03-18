using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("EditModeTests")]

namespace UnityInputSyncerUTPServer
{
    public class DedicatedServerBootstrap : MonoBehaviour
    {
        [Header("Server Configuration")]
        [SerializeField] private ushort port = 7777;
        [SerializeField] private int maxPlayers = 2;
        [SerializeField] private bool autoStartWhenFull = true;
        [SerializeField] private float stepIntervalSeconds = 0.1f;
        [SerializeField] private bool allowLateJoin = false;
        [SerializeField] private bool sendStepHistoryOnLateJoin = true;
        [SerializeField] private float heartbeatTimeout = 15f;

        private InputSyncerServer server;

        public InputSyncerServer Server => server;

        // Internal for tests: read config after ApplyEnvironmentOverrides (e.g. after Awake).
        internal ushort ConfigPort => port;
        internal int ConfigMaxPlayers => maxPlayers;
        internal bool ConfigAutoStartWhenFull => autoStartWhenFull;
        internal float ConfigStepIntervalSeconds => stepIntervalSeconds;
        internal bool ConfigAllowLateJoin => allowLateJoin;
        internal bool ConfigSendStepHistoryOnLateJoin => sendStepHistoryOnLateJoin;
        internal float ConfigHeartbeatTimeout => heartbeatTimeout;

        void Awake()
        {
            ApplyEnvironmentOverrides();

            Debug.Log($"[DedicatedServer] Configuration: " +
                $"Port={port}, MaxPlayers={maxPlayers}, AutoStartWhenFull={autoStartWhenFull}, " +
                $"StepInterval={stepIntervalSeconds}s, AllowLateJoin={allowLateJoin}, " +
                $"SendHistoryOnLateJoin={sendStepHistoryOnLateJoin}, HeartbeatTimeout={heartbeatTimeout}s");
        }

        void Start()
        {
            var options = new InputSyncerServerOptions
            {
                Port = port,
                MaxPlayers = maxPlayers,
                AutoStartWhenFull = autoStartWhenFull,
                StepIntervalSeconds = stepIntervalSeconds,
                AllowLateJoin = allowLateJoin,
                SendStepHistoryOnLateJoin = sendStepHistoryOnLateJoin,
                HeartbeatTimeout = heartbeatTimeout,
            };

            server = new InputSyncerServer(options);
            server.Start();
        }

        void OnDestroy()
        {
            server?.Dispose();
        }

        internal void ApplyEnvironmentOverrides()
        {
            if (TryGetEnvUShort("INPUT_SYNCER_PORT", out var envPort))
                port = envPort;

            if (TryGetEnvInt("INPUT_SYNCER_MAX_PLAYERS", out var envMaxPlayers))
                maxPlayers = envMaxPlayers;

            if (TryGetEnvBool("INPUT_SYNCER_AUTO_START_WHEN_FULL", out var envAutoStart))
                autoStartWhenFull = envAutoStart;

            if (TryGetEnvFloat("INPUT_SYNCER_STEP_INTERVAL", out var envStepInterval))
                stepIntervalSeconds = envStepInterval;

            if (TryGetEnvBool("INPUT_SYNCER_ALLOW_LATE_JOIN", out var envLateJoin))
                allowLateJoin = envLateJoin;

            if (TryGetEnvBool("INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN", out var envSendHistory))
                sendStepHistoryOnLateJoin = envSendHistory;

            if (TryGetEnvFloat("INPUT_SYNCER_HEARTBEAT_TIMEOUT", out var envHeartbeat))
                heartbeatTimeout = envHeartbeat;
        }

        internal static bool TryGetEnvUShort(string name, out ushort value)
        {
            value = 0;
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw)) return false;
            return ushort.TryParse(raw, out value);
        }

        internal static bool TryGetEnvInt(string name, out int value)
        {
            value = 0;
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw)) return false;
            return int.TryParse(raw, out value);
        }

        internal static bool TryGetEnvFloat(string name, out float value)
        {
            value = 0f;
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw)) return false;
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        internal static bool TryGetEnvString(string name, out string value)
        {
            value = null;
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw)) return false;
            value = raw;
            return true;
        }

        internal static bool TryGetEnvBool(string name, out bool value)
        {
            value = false;
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw)) return false;

            switch (raw.ToLowerInvariant())
            {
                case "true":
                case "1":
                    value = true;
                    return true;
                case "false":
                case "0":
                    value = false;
                    return true;
                default:
                    return false;
            }
        }
    }
}

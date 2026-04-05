using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityInputSyncerCore;

[assembly: InternalsVisibleTo("EditModeTests")]

namespace UnityInputSyncerUTPServer
{
    public class DedicatedServerBootstrap : MonoBehaviour
    {
        [Header("Server Configuration")]
        [SerializeField] private ushort port = 7777;
        [SerializeField] private int maxPlayers = 2;
        [SerializeField] private bool autoStartWhenFull = true;
        [SerializeField] private bool autoJoinOnConnect = true;
        [SerializeField] private float stepIntervalSeconds = 0.1f;
        [SerializeField] private bool allowLateJoin = false;
        [SerializeField] private bool sendStepHistoryOnLateJoin = true;
        [SerializeField] private float heartbeatTimeout = 15f;
        [SerializeField] private float abandonMatchTimeoutSeconds;
        [SerializeField] private bool quorumUserFinishEndsMatch = true;
        [SerializeField] private int sessionFinishMaxPayloadBytes = 4096;
        [SerializeField] private bool sessionFinishBroadcast = true;
        [SerializeField] private bool rejectInputAfterSessionFinish;
        [SerializeField] private RewardOutcomeDeliveryMode rewardOutcomeDelivery = RewardOutcomeDeliveryMode.ClientToAdmin;

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
        internal bool ConfigAutoJoinOnConnect => autoJoinOnConnect;
        internal int ConfigSessionFinishMaxPayloadBytes => sessionFinishMaxPayloadBytes;
        internal bool ConfigSessionFinishBroadcast => sessionFinishBroadcast;
        internal bool ConfigRejectInputAfterSessionFinish => rejectInputAfterSessionFinish;
        internal RewardOutcomeDeliveryMode ConfigRewardOutcomeDelivery => rewardOutcomeDelivery;

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
                AutoJoinOnConnect = autoJoinOnConnect,
                StepIntervalSeconds = stepIntervalSeconds,
                AllowLateJoin = allowLateJoin,
                SendStepHistoryOnLateJoin = sendStepHistoryOnLateJoin,
                HeartbeatTimeout = heartbeatTimeout,
                AbandonMatchTimeoutSeconds = abandonMatchTimeoutSeconds,
                QuorumUserFinishEndsMatch = quorumUserFinishEndsMatch,
                SessionFinishMaxPayloadBytes = sessionFinishMaxPayloadBytes,
                SessionFinishBroadcast = sessionFinishBroadcast,
                RejectInputAfterSessionFinish = rejectInputAfterSessionFinish,
                RewardOutcomeDelivery = rewardOutcomeDelivery,
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

            if (TryGetEnvFloat("INPUT_SYNCER_ABANDON_MATCH_TIMEOUT", out var envAbandon))
                abandonMatchTimeoutSeconds = envAbandon;

            if (TryGetEnvBool("INPUT_SYNCER_QUORUM_USER_FINISH_ENDS_MATCH", out var envQuorum))
                quorumUserFinishEndsMatch = envQuorum;

            if (TryGetEnvBool("INPUT_SYNCER_AUTO_JOIN_ON_CONNECT", out var envAutoJoin))
                autoJoinOnConnect = envAutoJoin;

            if (TryGetEnvInt("INPUT_SYNCER_SESSION_FINISH_MAX_PAYLOAD_BYTES", out var envSessMax))
                sessionFinishMaxPayloadBytes = envSessMax;

            if (TryGetEnvBool("INPUT_SYNCER_SESSION_FINISH_BROADCAST", out var envSessBc))
                sessionFinishBroadcast = envSessBc;

            if (TryGetEnvBool("INPUT_SYNCER_REJECT_INPUT_AFTER_SESSION_FINISH", out var envReject))
                rejectInputAfterSessionFinish = envReject;

            if (TryGetEnvInt("INPUT_SYNCER_REWARD_OUTCOME_DELIVERY", out var envReward))
            {
                if (envReward == 1)
                    rewardOutcomeDelivery = RewardOutcomeDeliveryMode.ServerHookPerUser;
                else if (envReward == 2)
                    rewardOutcomeDelivery = RewardOutcomeDeliveryMode.ServerHookMatchOrReferee;
                else
                    rewardOutcomeDelivery = RewardOutcomeDeliveryMode.ClientToAdmin;
            }
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

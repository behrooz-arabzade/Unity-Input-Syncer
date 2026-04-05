using UnityEngine;
using UnityInputSyncerCore;

namespace UnityInputSyncerUTPServer
{
    public class MultiInstanceServerBootstrap : MonoBehaviour
    {
        [Header("Pool Configuration")]
        [SerializeField] private ushort basePort = 7778;
        [SerializeField] private int maxInstances = 10;
        [SerializeField] private bool autoRecycleOnFinish = true;

        [Header("Admin Configuration")]
        [SerializeField] private ushort adminPort = 8080;
        [SerializeField] private string authToken = "";
        [Tooltip("Public hostname or IP (no scheme) included in admin API ServerUrl / ClientConnection for clients.")]
        [SerializeField] private string publicHost = "";
        [Tooltip("When true, POST /api/instances must include non-empty matchData and/or users.")]
        [SerializeField] private bool requireMatchUserDataOnCreate = false;

        [Header("Default Server Options")]
        [SerializeField] private int maxPlayers = 2;
        [SerializeField] private bool autoStartWhenFull = true;
        [SerializeField] private bool autoJoinOnConnect = true;
        [SerializeField] private float stepIntervalSeconds = 0.1f;
        [SerializeField] private bool allowLateJoin = false;
        [SerializeField] private bool sendStepHistoryOnLateJoin = true;
        [SerializeField] private float heartbeatTimeout = 15f;
        [SerializeField] private float idleTimeoutSeconds = 0f;
        [SerializeField] private float maxInstanceLifetimeSeconds;
        [SerializeField] private float abandonMatchTimeoutSeconds;
        [SerializeField] private bool quorumUserFinishEndsMatch = true;
        [SerializeField] private int sessionFinishMaxPayloadBytes = 4096;
        [SerializeField] private bool sessionFinishBroadcast = true;
        [SerializeField] private bool rejectInputAfterSessionFinish;
        [SerializeField] private RewardOutcomeDeliveryMode rewardOutcomeDelivery = RewardOutcomeDeliveryMode.ClientToAdmin;

        private InputSyncerServerPool pool;
        private AdminHttpServer httpServer;

        public InputSyncerServerPool Pool => pool;
        public AdminHttpServer HttpServer => httpServer;

        // Internal for tests
        internal ushort ConfigBasePort => basePort;
        internal int ConfigMaxInstances => maxInstances;
        internal bool ConfigAutoRecycleOnFinish => autoRecycleOnFinish;
        internal ushort ConfigAdminPort => adminPort;
        internal string ConfigAuthToken => authToken;
        internal int ConfigMaxPlayers => maxPlayers;
        internal bool ConfigAutoStartWhenFull => autoStartWhenFull;
        internal float ConfigStepIntervalSeconds => stepIntervalSeconds;
        internal bool ConfigAllowLateJoin => allowLateJoin;
        internal bool ConfigSendStepHistoryOnLateJoin => sendStepHistoryOnLateJoin;
        internal float ConfigHeartbeatTimeout => heartbeatTimeout;
        internal float ConfigIdleTimeoutSeconds => idleTimeoutSeconds;
        internal float ConfigMaxInstanceLifetimeSeconds => maxInstanceLifetimeSeconds;
        internal float ConfigAbandonMatchTimeoutSeconds => abandonMatchTimeoutSeconds;
        internal bool ConfigQuorumUserFinishEndsMatch => quorumUserFinishEndsMatch;
        internal bool ConfigAutoJoinOnConnect => autoJoinOnConnect;
        internal int ConfigSessionFinishMaxPayloadBytes => sessionFinishMaxPayloadBytes;
        internal bool ConfigSessionFinishBroadcast => sessionFinishBroadcast;
        internal bool ConfigRejectInputAfterSessionFinish => rejectInputAfterSessionFinish;
        internal RewardOutcomeDeliveryMode ConfigRewardOutcomeDelivery => rewardOutcomeDelivery;

        void Awake()
        {
            ApplyEnvironmentOverrides();

            Debug.Log($"[MultiInstanceServer] Configuration: " +
                $"BasePort={basePort}, MaxInstances={maxInstances}, AutoRecycle={autoRecycleOnFinish}, " +
                $"AdminPort={adminPort}, AuthToken={(string.IsNullOrEmpty(authToken) ? "(none)" : "(set)")}, " +
                $"MaxPlayers={maxPlayers}, AutoStartWhenFull={autoStartWhenFull}, " +
                $"StepInterval={stepIntervalSeconds}s, AllowLateJoin={allowLateJoin}, " +
                $"SendHistoryOnLateJoin={sendStepHistoryOnLateJoin}, HeartbeatTimeout={heartbeatTimeout}s");
        }

        void Start()
        {
            var poolOptions = new InputSyncerServerPoolOptions
            {
                BasePort = basePort,
                MaxInstances = maxInstances,
                AutoRecycleOnFinish = autoRecycleOnFinish,
                IdleTimeoutSeconds = idleTimeoutSeconds,
                MaxInstanceLifetimeSeconds = maxInstanceLifetimeSeconds,
                PublicHost = publicHost,
                RequireMatchUserDataOnCreate = requireMatchUserDataOnCreate,
                DefaultServerOptions = new InputSyncerServerOptions
                {
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
                },
            };

            pool = new InputSyncerServerPool(poolOptions);
            var poolOps = new AdminPoolOperations(pool);
            var controller = new AdminController(poolOps, authToken);
            httpServer = new AdminHttpServer(controller, new AdminHttpServerOptions { Port = adminPort });
            httpServer.Start();
        }

        void Update()
        {
            pool?.Tick();
        }

        void OnDestroy()
        {
            httpServer?.Dispose();
            pool?.Dispose();
        }

        internal void ApplyEnvironmentOverrides()
        {
            if (DedicatedServerBootstrap.TryGetEnvUShort("INPUT_SYNCER_BASE_PORT", out var envBasePort))
                basePort = envBasePort;

            if (DedicatedServerBootstrap.TryGetEnvInt("INPUT_SYNCER_MAX_INSTANCES", out var envMaxInstances))
                maxInstances = envMaxInstances;

            if (DedicatedServerBootstrap.TryGetEnvBool("INPUT_SYNCER_AUTO_RECYCLE", out var envAutoRecycle))
                autoRecycleOnFinish = envAutoRecycle;

            if (DedicatedServerBootstrap.TryGetEnvUShort("INPUT_SYNCER_ADMIN_PORT", out var envAdminPort))
                adminPort = envAdminPort;

            if (DedicatedServerBootstrap.TryGetEnvString("INPUT_SYNCER_ADMIN_AUTH_TOKEN", out var envAuthToken))
                authToken = envAuthToken;

            if (DedicatedServerBootstrap.TryGetEnvString("INPUT_SYNCER_PUBLIC_HOST", out var envPublicHost))
                publicHost = envPublicHost;

            if (DedicatedServerBootstrap.TryGetEnvBool("INPUT_SYNCER_ADMIN_REQUIRE_MATCH_USER_DATA", out var envReqMu))
                requireMatchUserDataOnCreate = envReqMu;

            if (DedicatedServerBootstrap.TryGetEnvInt("INPUT_SYNCER_MAX_PLAYERS", out var envMaxPlayers))
                maxPlayers = envMaxPlayers;

            if (DedicatedServerBootstrap.TryGetEnvBool("INPUT_SYNCER_AUTO_START_WHEN_FULL", out var envAutoStart))
                autoStartWhenFull = envAutoStart;

            if (DedicatedServerBootstrap.TryGetEnvFloat("INPUT_SYNCER_STEP_INTERVAL", out var envStepInterval))
                stepIntervalSeconds = envStepInterval;

            if (DedicatedServerBootstrap.TryGetEnvBool("INPUT_SYNCER_ALLOW_LATE_JOIN", out var envLateJoin))
                allowLateJoin = envLateJoin;

            if (DedicatedServerBootstrap.TryGetEnvBool("INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN", out var envSendHistory))
                sendStepHistoryOnLateJoin = envSendHistory;

            if (DedicatedServerBootstrap.TryGetEnvFloat("INPUT_SYNCER_HEARTBEAT_TIMEOUT", out var envHeartbeat))
                heartbeatTimeout = envHeartbeat;

            if (DedicatedServerBootstrap.TryGetEnvFloat("INPUT_SYNCER_IDLE_TIMEOUT", out var envIdleTimeout))
                idleTimeoutSeconds = envIdleTimeout;

            if (DedicatedServerBootstrap.TryGetEnvFloat("INPUT_SYNCER_MAX_INSTANCE_LIFETIME", out var envMaxLife))
                maxInstanceLifetimeSeconds = envMaxLife;

            if (DedicatedServerBootstrap.TryGetEnvFloat("INPUT_SYNCER_ABANDON_MATCH_TIMEOUT", out var envAbandon))
                abandonMatchTimeoutSeconds = envAbandon;

            if (DedicatedServerBootstrap.TryGetEnvBool("INPUT_SYNCER_QUORUM_USER_FINISH_ENDS_MATCH", out var envQuorum))
                quorumUserFinishEndsMatch = envQuorum;

            if (DedicatedServerBootstrap.TryGetEnvBool("INPUT_SYNCER_AUTO_JOIN_ON_CONNECT", out var envAutoJoin))
                autoJoinOnConnect = envAutoJoin;

            if (DedicatedServerBootstrap.TryGetEnvInt("INPUT_SYNCER_SESSION_FINISH_MAX_PAYLOAD_BYTES", out var envSessMax))
                sessionFinishMaxPayloadBytes = envSessMax;

            if (DedicatedServerBootstrap.TryGetEnvBool("INPUT_SYNCER_SESSION_FINISH_BROADCAST", out var envSessBc))
                sessionFinishBroadcast = envSessBc;

            if (DedicatedServerBootstrap.TryGetEnvBool("INPUT_SYNCER_REJECT_INPUT_AFTER_SESSION_FINISH", out var envReject))
                rejectInputAfterSessionFinish = envReject;

            if (DedicatedServerBootstrap.TryGetEnvInt("INPUT_SYNCER_REWARD_OUTCOME_DELIVERY", out var envReward))
            {
                if (envReward == 1)
                    rewardOutcomeDelivery = RewardOutcomeDeliveryMode.ServerHookPerUser;
                else if (envReward == 2)
                    rewardOutcomeDelivery = RewardOutcomeDeliveryMode.ServerHookMatchOrReferee;
                else
                    rewardOutcomeDelivery = RewardOutcomeDeliveryMode.ClientToAdmin;
            }
        }
    }
}

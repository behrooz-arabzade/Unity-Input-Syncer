using UnityEngine;

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

        [Header("Default Server Options")]
        [SerializeField] private int maxPlayers = 2;
        [SerializeField] private bool autoStartWhenFull = true;
        [SerializeField] private float stepIntervalSeconds = 0.1f;
        [SerializeField] private bool allowLateJoin = false;
        [SerializeField] private bool sendStepHistoryOnLateJoin = true;
        [SerializeField] private float heartbeatTimeout = 15f;

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
                DefaultServerOptions = new InputSyncerServerOptions
                {
                    MaxPlayers = maxPlayers,
                    AutoStartWhenFull = autoStartWhenFull,
                    StepIntervalSeconds = stepIntervalSeconds,
                    AllowLateJoin = allowLateJoin,
                    SendStepHistoryOnLateJoin = sendStepHistoryOnLateJoin,
                    HeartbeatTimeout = heartbeatTimeout,
                },
            };

            pool = new InputSyncerServerPool(poolOptions);
            var poolOps = new AdminPoolOperations(pool);
            var controller = new AdminController(poolOps, authToken);
            httpServer = new AdminHttpServer(controller, new AdminHttpServerOptions { Port = adminPort });
            httpServer.Start();
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
        }
    }
}

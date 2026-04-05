namespace UnityInputSyncerUTPServer
{
    public class InputSyncerServerPoolOptions
    {
        public ushort BasePort = 7778;
        public int MaxInstances = 10;
        public bool AutoRecycleOnFinish = true;
        public float IdleTimeoutSeconds = 0f;
        /// <summary>0 = disabled. Forces FinishMatch + destroy when instance age exceeds this (seconds).</summary>
        public float MaxInstanceLifetimeSeconds = 0f;
        public InputSyncerServerOptions DefaultServerOptions = new InputSyncerServerOptions();

        /// <summary>Public hostname or IP for clients (no scheme). Used in admin <c>ServerUrl</c> / <c>ClientConnection</c>.</summary>
        public string PublicHost = "";

        /// <summary>When true, <c>POST /api/instances</c> must include non-empty <c>matchData</c> and/or <c>users</c>.</summary>
        public bool RequireMatchUserDataOnCreate = false;
    }
}

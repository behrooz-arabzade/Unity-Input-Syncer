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
    }
}

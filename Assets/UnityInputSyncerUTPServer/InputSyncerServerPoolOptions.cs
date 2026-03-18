namespace UnityInputSyncerUTPServer
{
    public class InputSyncerServerPoolOptions
    {
        public ushort BasePort = 7778;
        public int MaxInstances = 10;
        public bool AutoRecycleOnFinish = true;
        public InputSyncerServerOptions DefaultServerOptions = new InputSyncerServerOptions();
    }
}

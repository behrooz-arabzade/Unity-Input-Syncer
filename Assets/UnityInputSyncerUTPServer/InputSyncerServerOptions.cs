namespace UnityInputSyncerUTPServer
{
    public class InputSyncerServerOptions
    {
        public ushort Port = 7777;
        public float HeartbeatTimeout = 15f;
        public int MaxPlayers = 2;
        public bool AutoStartWhenFull = false;
        public float StepIntervalSeconds = 0.1f;
        public bool AllowLateJoin = false;
        public bool SendStepHistoryOnLateJoin = true;
    }
}

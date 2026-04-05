namespace UnityInputSyncerCore
{
    /// <summary>Values for <c>on-finish</c> JSON field <c>reason</c>.</summary>
    public static class InputSyncerFinishReasons
    {
        public const string Completed = "completed";
        public const string AllDisconnected = "all_disconnected";
        public const string InsufficientPlayers = "insufficient_players";
        public const string AbandonTimeout = "abandon_timeout";
        public const string MaxInstanceLifetime = "max_instance_lifetime";
    }
}

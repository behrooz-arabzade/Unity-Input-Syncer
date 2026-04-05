namespace UnityInputSyncerCore
{
    /// <summary>
    /// How reward/outcome data leaves the match host. Mode 1 is client-only (no server hook).
    /// </summary>
    public enum RewardOutcomeDeliveryMode
    {
        ClientToAdmin = 0,
        ServerHookPerUser = 1,
        ServerHookMatchOrReferee = 2,
    }
}

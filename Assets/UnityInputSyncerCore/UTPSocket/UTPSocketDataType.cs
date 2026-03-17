namespace UnityInputSyncerCore.UTPSocket
{
    public enum UTPSocketDataType : byte
    {
        Handshake = 0,
        Json = 1,
        Binary = 2,
        HeartbeatPing = 3,
        HeartbeatPong = 4
    }
}
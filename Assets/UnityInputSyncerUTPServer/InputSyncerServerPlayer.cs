using Unity.Networking.Transport;

namespace UnityInputSyncerUTPServer
{
    public class InputSyncerServerPlayer
    {
        public string UserId;
        public NetworkConnection Connection;
        public bool Joined;
        public bool Finished;
    }
}

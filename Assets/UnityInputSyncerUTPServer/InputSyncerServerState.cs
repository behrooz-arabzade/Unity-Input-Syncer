using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Unity.Networking.Transport;
using UnityInputSyncerClient;

namespace UnityInputSyncerUTPServer
{
    public class InputSyncerServerState
    {
        public bool MatchStarted;
        public bool MatchFinished;
        public int CurrentStep;
        public float StepAccumulator;
        public Dictionary<int, StepInputs> StepHistory = new Dictionary<int, StepInputs>();
        public List<JObject> PendingInputs = new List<JObject>();
        public Dictionary<NetworkConnection, InputSyncerServerPlayer> Players = new Dictionary<NetworkConnection, InputSyncerServerPlayer>();
    }
}

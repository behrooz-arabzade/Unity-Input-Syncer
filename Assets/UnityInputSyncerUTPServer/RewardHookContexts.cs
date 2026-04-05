using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace UnityInputSyncerUTPServer
{
    public sealed class RewardPerUserHookContext
    {
        public string MatchInstanceId;
        public string UserId;
        public JToken Data;
    }

    public sealed class RewardMatchHookContext
    {
        public string MatchInstanceId;
        public string Reason;
        public List<string> JoinedUserIds;
    }
}

using Newtonsoft.Json.Linq;

namespace UnityInputSyncerClient
{
    /// <summary>Payload from server <c>on-match-context</c> (admin-defined match and per-user simulation data).</summary>
    public class InputSyncerMatchContext
    {
        public string MatchId { get; set; }
        public JToken MatchData { get; set; }
        public JObject Users { get; set; }
    }
}

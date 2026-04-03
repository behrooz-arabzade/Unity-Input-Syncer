using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityInputSyncerClient;

namespace SyncSimulation
{
    /// <summary>
    /// Deterministic-enough (within a run) hashing of local-player inputs for misprediction checks.
    /// </summary>
    public static class StepInputHash
    {
        public static ulong ComputeForLocalUser(List<object> inputs, string localUserId)
        {
            if (string.IsNullOrEmpty(localUserId) || inputs == null || inputs.Count == 0)
                return 1469598103934665603UL;

            ulong h = 1469598103934665603UL;
            foreach (var raw in OrderByIndex(inputs))
            {
                if (!TryGetUserId(raw, out var uid) || uid != localUserId)
                    continue;

                var s = Serialize(raw);
                foreach (var b in Encoding.UTF8.GetBytes(s))
                    h = (h ^ b) * 1099511628211UL;
            }

            return h;
        }

        static IEnumerable<object> OrderByIndex(List<object> inputs)
        {
            return inputs.OrderBy(i =>
            {
                if (i is JObject jObj)
                    return jObj["index"]?.Value<long>() ?? 0L;
                if (i is BaseInputData bid)
                    return bid.index;
                return 0L;
            });
        }

        static bool TryGetUserId(object raw, out string userId)
        {
            userId = null;
            if (raw is JObject jObj)
            {
                userId = jObj.Value<string>("userId");
                return userId != null;
            }

            if (raw is BaseInputData bid)
            {
                userId = bid.userId;
                return userId != null;
            }

            return false;
        }

        static string Serialize(object raw)
        {
            if (raw is JObject jo)
                return jo.ToString(Newtonsoft.Json.Formatting.None);
            return Newtonsoft.Json.JsonConvert.SerializeObject(raw);
        }
    }
}

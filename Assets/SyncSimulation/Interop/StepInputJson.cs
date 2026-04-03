using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SyncSimulation
{
    /// <summary>
    /// Serializes step inputs for <see cref="JsonInputEventElement"/> buffers (main thread).
    /// </summary>
    public static class StepInputJson
    {
        public static string ToJson(object input)
        {
            if (input is JObject jo)
                return jo.ToString(Formatting.None);
            return JsonConvert.SerializeObject(input);
        }
    }
}

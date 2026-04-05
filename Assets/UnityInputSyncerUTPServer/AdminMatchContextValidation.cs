using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityInputSyncerUTPServer
{
    internal static class AdminMatchContextValidation
    {
        internal const int DefaultMaxMatchDataUtf8Bytes = 65536;
        internal const int DefaultMaxPerUserUtf8Bytes = 16384;
        internal const int DefaultMaxUserEntries = 64;

        internal static void Validate(
            AdminCreateInstanceRequest request,
            ICollection<string> errors,
            bool requireMatchUserPayload)
        {
            if (request == null)
            {
                if (requireMatchUserPayload)
                    errors.Add("matchData or users is required when INPUT_SYNCER_ADMIN_REQUIRE_MATCH_USER_DATA is enabled");
                return;
            }

            if (requireMatchUserPayload)
            {
                bool hasMatch = request.MatchData != null && request.MatchData.Count > 0;
                bool hasUsers = request.Users != null && request.Users.Properties().Any();
                if (!hasMatch && !hasUsers)
                    errors.Add("Provide matchData and/or users when INPUT_SYNCER_ADMIN_REQUIRE_MATCH_USER_DATA is enabled");
            }

            if (request.MatchData != null)
            {
                int n = Utf8ByteCount(request.MatchData);
                if (n > DefaultMaxMatchDataUtf8Bytes)
                    errors.Add($"matchData must be at most {DefaultMaxMatchDataUtf8Bytes} UTF-8 bytes (got {n})");
            }

            if (request.Users == null)
                return;

            if (request.Users.Count > DefaultMaxUserEntries)
            {
                errors.Add($"users must have at most {DefaultMaxUserEntries} entries");
                return;
            }

            foreach (var p in request.Users.Properties())
            {
                if (string.IsNullOrWhiteSpace(p.Name))
                {
                    errors.Add("users object keys must be non-empty userIds");
                    break;
                }

                int u = Utf8ByteCount(p.Value);
                if (u > DefaultMaxPerUserUtf8Bytes)
                    errors.Add($"users['{p.Name}'] must be at most {DefaultMaxPerUserUtf8Bytes} UTF-8 bytes (got {u})");
            }
        }

        internal static void MapMatchContextToOptions(AdminCreateInstanceRequest request, InputSyncerServerOptions o)
        {
            if (request == null)
                return;

            if (request.MatchData != null)
                o.MatchData = (JObject)request.MatchData.DeepClone();

            o.UserSimulationData = null;
            if (request.Users != null && request.Users.Count > 0)
            {
                o.UserSimulationData = new Dictionary<string, JToken>();
                foreach (var p in request.Users.Properties())
                    o.UserSimulationData[p.Name] = p.Value.DeepClone();
            }
        }

        private static int Utf8ByteCount(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return 0;
            var s = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
            return s == null ? 0 : Encoding.UTF8.GetByteCount(s);
        }
    }
}

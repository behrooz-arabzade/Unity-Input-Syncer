using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityInputSyncerClient;

namespace SyncSimulation
{
    /// <summary>
    /// Deterministic-enough (within a run) hashing of step inputs for
    /// misprediction checks. Two flavors:
    /// <list type="bullet">
    /// <item><see cref="ComputeForLocalUser"/> — only hashes the local user's
    ///   inputs. Useful when the host knows the opponent's predicted state
    ///   matches authoritative by construction (single-player, server-replay).</item>
    /// <item><see cref="ComputeForAllUsers"/> — hashes every user's inputs.
    ///   Required for PvP rollback: when the opponent submits a discrete
    ///   input at a predicted step, the local prediction (using carried-
    ///   forward opponent input) diverges from authoritative; only an
    ///   all-users hash flags it. Both flavors strip the per-step `index`
    ///   field so two semantically-identical inputs at different positions
    ///   in the buffer hash the same.</item>
    /// </list>
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

                var s = SerializeWithoutIndex(raw);
                foreach (var b in Encoding.UTF8.GetBytes(s))
                    h = (h ^ b) * 1099511628211UL;
            }

            return h;
        }

        /// <summary>
        /// Hash the full input set for the step, covering every user. Used
        /// by <see cref="InputTimeline.TryIngestAuthoritativeAndDetectMisprediction"/>
        /// to detect any divergence between local prediction and the
        /// authoritative state — including changes the opponent made that
        /// the local prediction couldn't anticipate.
        ///
        /// The `index` field is stripped before hashing because it's a
        /// per-step ordering aid the server assigns sequentially, not a
        /// semantic property of the input. Two identical inputs sitting
        /// at different positions in the buffer must hash the same.
        /// Inputs are sorted by `(userId, payload)` to make the hash
        /// commutative across users — order of submission doesn't change
        /// the meaning.
        /// </summary>
        public static ulong ComputeForAllUsers(List<object> inputs)
        {
            if (inputs == null || inputs.Count == 0)
                return 1469598103934665603UL;

            // Stable, content-based ordering: by userId then by serialized
            // payload (without `index`). Ensures both clients hash the same
            // set in the same order regardless of submission timing.
            var ordered = inputs
                .Select(i => new { Raw = i, UserId = TryGetUserId(i, out var u) ? u : "", Body = SerializeWithoutIndex(i) })
                .OrderBy(x => x.UserId, System.StringComparer.Ordinal)
                .ThenBy(x => x.Body, System.StringComparer.Ordinal);

            ulong h = 1469598103934665603UL;
            foreach (var item in ordered)
            {
                foreach (var b in Encoding.UTF8.GetBytes(item.UserId))
                    h = (h ^ b) * 1099511628211UL;
                // Domain separator between userId and body so two users
                // with adjacent strings (e.g. "ab"+"" vs "a"+"b") cannot
                // collide.
                h = (h ^ 0x1F) * 1099511628211UL;
                foreach (var b in Encoding.UTF8.GetBytes(item.Body))
                    h = (h ^ b) * 1099511628211UL;
                h = (h ^ 0x1E) * 1099511628211UL;
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

        /// <summary>
        /// Same as <see cref="Serialize"/> but with the `index` field
        /// stripped. Index is server-assigned per-step; carrying a
        /// step-local index into a hash makes semantically-identical
        /// inputs hash differently when their position in the buffer
        /// shifts (e.g., the same opponent action carried forward into
        /// a new step might land at a different index).
        /// </summary>
        static string SerializeWithoutIndex(object raw)
        {
            if (raw is JObject jo)
            {
                // Clone to avoid mutating the caller's object.
                var clone = (JObject)jo.DeepClone();
                clone.Remove("index");
                return clone.ToString(Newtonsoft.Json.Formatting.None);
            }
            if (raw is BaseInputData bid)
            {
                // BaseInputData is a class with public `index` and `userId`.
                // We can't snapshot it cheaply without reflection, so use
                // Newtonsoft with an ignore-property contract. The simpler
                // (and faster) path: serialize to JObject, strip, re-emit.
                var jo2 = JObject.FromObject(bid);
                jo2.Remove("index");
                return jo2.ToString(Newtonsoft.Json.Formatting.None);
            }
            return Newtonsoft.Json.JsonConvert.SerializeObject(raw);
        }
    }
}

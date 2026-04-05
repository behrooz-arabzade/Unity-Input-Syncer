using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityInputSyncerClient;

namespace SyncSimulation
{
    /// <summary>
    /// Merges authoritative lockstep steps from <see cref="InputSyncerState"/> with local prediction
    /// (continuous carry + discrete events). Detects local misprediction when authoritative data arrives.
    /// </summary>
    public sealed class InputTimeline
    {
        readonly InputSyncerState _state;
        readonly string _localUserId;
        readonly int _maxPredictionSteps;

        int _authoritativeMaxStep = -1;
        readonly Queue<(int step, BaseInputData data)> _discreteLocal = new();
        BaseInputData _continuousSample;
        bool _hasContinuous;

        readonly Dictionary<int, ulong> _localHashAfterSimStep = new();

        public InputTimeline(InputSyncerState state, string localUserId, int maxPredictionSteps)
        {
            _state = state;
            _localUserId = localUserId ?? "";
            _maxPredictionSteps = maxPredictionSteps;
        }

        public int AuthoritativeMaxStep => _authoritativeMaxStep;

        /// <summary>
        /// Highest simulation step index allowed this frame (inclusive). -1 until at least step 0 is authoritative.
        /// </summary>
        public int MaxAllowedSimStep =>
            _authoritativeMaxStep < 0 ? -1 : _authoritativeMaxStep + _maxPredictionSteps;

        /// <summary>Queue a discrete local input for an exact future simulation step.</summary>
        public void EnqueueDiscreteLocalForStep(int step, BaseInputData data)
        {
            _discreteLocal.Enqueue((step, data));
        }

        /// <summary>Last sampled continuous local input; repeated for predicted steps until replaced.</summary>
        public void SetContinuousLocalSample(BaseInputData sample)
        {
            _continuousSample = sample;
            _hasContinuous = sample != null;
        }

        public void ClearContinuousLocalSample()
        {
            _continuousSample = null;
            _hasContinuous = false;
        }

        /// <summary>
        /// Advances authoritative coverage from <see cref="InputSyncerState"/>.
        /// Returns the first divergent step if local prediction for that step does not match server inputs.
        /// </summary>
        public bool TryIngestAuthoritativeAndDetectMisprediction(out int divergentStep)
        {
            divergentStep = -1;
            while (true)
            {
                int next = _authoritativeMaxStep + 1;
                if (!_state.HasStep(next))
                    break;

                if (_localHashAfterSimStep.TryGetValue(next, out var predictedLocalHash))
                {
                    var auth = _state.GetInputsForStep(next);
                    var authLocalHash = StepInputHash.ComputeForLocalUser(auth, _localUserId);
                    if (authLocalHash != predictedLocalHash)
                    {
                        _authoritativeMaxStep = next;
                        divergentStep = next;
                        return true;
                    }

                    _localHashAfterSimStep.Remove(next);
                }

                _authoritativeMaxStep = next;
            }

            return false;
        }

        public void ClearPredictionTrackingFromStep(int stepInclusive)
        {
            var keys = _localHashAfterSimStep.Keys.Where(k => k >= stepInclusive).ToList();
            foreach (var k in keys)
                _localHashAfterSimStep.Remove(k);
        }

        public void RegisterSimulatedPredictionHash(int step, ulong localHash)
        {
            _localHashAfterSimStep[step] = localHash;
        }

        public void ClearSimulatedPredictionHash(int step)
        {
            _localHashAfterSimStep.Remove(step);
        }

        public void ClearAllPredictionHashes()
        {
            _localHashAfterSimStep.Clear();
        }

        /// <summary>
        /// Recomputes <see cref="AuthoritativeMaxStep"/> after <see cref="InputSyncerState.AddAllStepInputs"/> (full resync).
        /// </summary>
        public void RebuildAuthoritativeMaxFromState()
        {
            _authoritativeMaxStep = -1;
            while (_state.HasStep(_authoritativeMaxStep + 1))
                _authoritativeMaxStep++;
        }

        /// <summary>
        /// Builds ordered inputs for simulation step <paramref name="step"/> into <paramref name="output"/>.
        /// </summary>
        public void BuildMergedInputs(int step, List<object> output)
        {
            output.Clear();
            if (step <= _authoritativeMaxStep)
            {
                output.AddRange(_state.GetInputsForStep(step));
                return;
            }

            if (_authoritativeMaxStep < 0)
                return;

            var carry = _state.GetInputsForStep(_authoritativeMaxStep);
            foreach (var o in carry)
            {
                if (TryGetUserId(o, out var uid) && uid == _localUserId)
                    continue;
                output.Add(CloneInput(o));
            }

            FlushDiscreteForStep(step, output);

            if (_hasContinuous && _continuousSample != null)
            {
                var c = CloneBaseInput(_continuousSample);
                c.index = NextIndex(output);
                c.userId = _localUserId;
                output.Add(c);
            }

            SortByIndex(output);
        }

        public bool StepUsesPrediction(int step) => step > _authoritativeMaxStep;

        public ulong ComputeLocalHashForBuiltInputs(List<object> output) =>
            StepInputHash.ComputeForLocalUser(output, _localUserId);

        static void SortByIndex(List<object> list)
        {
            list.Sort((a, b) =>
            {
                long ia = 0, ib = 0;
                if (a is JObject ja) ia = ja["index"]?.Value<long>() ?? 0L;
                else if (a is BaseInputData ba) ia = ba.index;
                if (b is JObject jb) ib = jb["index"]?.Value<long>() ?? 0L;
                else if (b is BaseInputData bb) ib = bb.index;
                return ia.CompareTo(ib);
            });
        }

        static long NextIndex(List<object> list)
        {
            long max = -1;
            foreach (var o in list)
            {
                if (o is JObject j) max = System.Math.Max(max, j["index"]?.Value<long>() ?? 0L);
                else if (o is BaseInputData b) max = System.Math.Max(max, b.index);
            }

            return max + 1;
        }

        void FlushDiscreteForStep(int step, List<object> output)
        {
            var pending = new Queue<(int step, BaseInputData data)>(_discreteLocal);
            _discreteLocal.Clear();
            while (pending.Count > 0)
            {
                var item = pending.Dequeue();
                if (item.step < step)
                    continue;
                if (item.step > step)
                {
                    _discreteLocal.Enqueue(item);
                    continue;
                }

                var d = CloneBaseInput(item.data);
                d.index = NextIndex(output);
                d.userId = _localUserId;
                output.Add(d);
            }
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

        static object CloneInput(object o)
        {
            if (o is JObject jo)
                return JObject.Parse(jo.ToString());
            var json = JsonConvert.SerializeObject(o);
            return JsonConvert.DeserializeObject(json, o.GetType());
        }

        static BaseInputData CloneBaseInput(BaseInputData src)
        {
            var json = JsonConvert.SerializeObject(src);
            return (BaseInputData)JsonConvert.DeserializeObject(json, src.GetType());
        }
    }
}

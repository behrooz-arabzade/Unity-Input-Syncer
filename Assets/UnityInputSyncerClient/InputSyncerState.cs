using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnityInputSyncerClient
{
    public class InputSyncerState
    {
        public int LastReceivedStep = -1;
        private Dictionary<int, StepInputs> ReceivedSteps = new Dictionary<int, StepInputs>();
        private Dictionary<int, StepInputs> TemporarySteps = new Dictionary<int, StepInputs>();
        public Action OnStepMissed = null;
        private bool AllStepRequested = false;

        public void AddStepInputs(List<StepInputs> stepInputs)
        {
            stepInputs.Sort((a, b) => a.step.CompareTo(b.step));

            foreach (var stepData in stepInputs)
            {
                if (AllStepRequested)
                {
                    TemporarySteps[stepData.step] = stepData;
                    continue;
                }

                if (LastReceivedStep + 1 != stepData.step)
                {
                    OnStepMissed?.Invoke();
                    AllStepRequested = true;
                    TemporarySteps[stepData.step] = stepData;
                    continue;
                }

                LastReceivedStep = stepData.step;

                ReceivedSteps[stepData.step] = stepData;
            }
        }

        public void AddAllStepInputs(List<StepInputs> stepInputs, int serverLastSentStep)
        {
            stepInputs.Sort((a, b) => a.step.CompareTo(b.step));
            var stepInputsDict = stepInputs.ToDictionary(s => s.step, s => s);

            ReceivedSteps.Clear();

            for (int step = 0; step <= serverLastSentStep; step++)
            {
                ReceivedSteps[step] = null;

                if (stepInputsDict.ContainsKey(step))
                {
                    ReceivedSteps[step] = stepInputsDict[step];
                }
                else
                {
                    ReceivedSteps[step] = new StepInputs
                    {
                        step = step,
                        inputs = new List<object>()
                    };
                }

                LastReceivedStep = step;
            }

            foreach (var tempStep in TemporarySteps.OrderBy(kvp => kvp.Key))
            {
                if (tempStep.Key <= LastReceivedStep) continue;

                if (LastReceivedStep + 1 != tempStep.Key)
                {
                    OnStepMissed?.Invoke();
                    AllStepRequested = true;
                    return;
                }

                ReceivedSteps[tempStep.Key] = tempStep.Value;
                LastReceivedStep = tempStep.Key;
            }

            TemporarySteps.Clear();
            AllStepRequested = false;
        }

        public List<object> GetInputsForStep(int step)
        {
            if (ReceivedSteps.TryGetValue(step, out var stepInputs))
            {
                return stepInputs.inputs.OrderBy(i =>
                {
                    if (i is JObject jObj)
                        return jObj["index"]?.Value<long>() ?? 0L;
                    if (i is BaseInputData bid)
                        return bid.index;
                    return 0L;
                }).ToList();
            }

            return new List<object>();
        }

        public bool HasStep(int step)
        {
            return ReceivedSteps.ContainsKey(step);
        }
    }
}
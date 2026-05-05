using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityInputSyncerClient;

namespace Tests.RollbackIntegration
{
    public static class InputScenarioBuilder
    {
        public static InputSyncerState BuildAuthoritative(int totalSteps, string localUserId = "player1")
        {
            var state = new InputSyncerState();
            var steps = new List<StepInputs>();

            for (int i = 0; i < totalSteps; i++)
            {
                steps.Add(new StepInputs
                {
                    step = i,
                    inputs = new List<object>
                    {
                        CreateInput(localUserId, i, "correct_" + i)
                    }
                });
            }

            state.AddStepInputs(steps);
            return state;
        }

        public static (InputSyncerState state, Action injectCorrectAuth) BuildWithDivergence(
            int totalSteps, int divergeAt, string localUserId = "player1")
        {
            var state = new InputSyncerState();

            var initialSteps = new List<StepInputs>();
            for (int i = 0; i < divergeAt; i++)
            {
                initialSteps.Add(new StepInputs
                {
                    step = i,
                    inputs = new List<object>
                    {
                        CreateInput(localUserId, i, "correct_" + i)
                    }
                });
            }

            if (initialSteps.Count > 0)
                state.AddStepInputs(initialSteps);

            void InjectCorrectAuth()
            {
                var correctSteps = new List<StepInputs>();
                for (int i = divergeAt; i < totalSteps; i++)
                {
                    correctSteps.Add(new StepInputs
                    {
                        step = i,
                        inputs = new List<object>
                        {
                            CreateInput(localUserId, i, "correct_" + i)
                        }
                    });
                }

                state.AddStepInputs(correctSteps);
            }

            return (state, InjectCorrectAuth);
        }

        public static JObject CreateInput(string userId, long index, string value)
        {
            return new JObject
            {
                ["type"] = "action",
                ["userId"] = userId,
                ["index"] = index,
                ["value"] = value
            };
        }

        public static JObject CreateWrongInput(string userId, long index, int step)
        {
            return new JObject
            {
                ["type"] = "action",
                ["userId"] = userId,
                ["index"] = index,
                ["value"] = "wrong_" + step
            };
        }
    }
}

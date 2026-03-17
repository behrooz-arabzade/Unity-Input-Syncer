using System.Collections.Generic;
using NUnit.Framework;
using UnityInputSyncerClient;

namespace Tests.EditMode
{
    public class InputSyncerStateTests
    {
        [Test]
        public void AddStepInputs_AddsStepsInOrder()
        {
            var state = new InputSyncerState();

            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "a" } },
                new StepInputs { step = 1, inputs = new List<object> { "b" } },
                new StepInputs { step = 2, inputs = new List<object> { "c" } }
            });

            Assert.IsTrue(state.HasStep(0));
            Assert.IsTrue(state.HasStep(1));
            Assert.IsTrue(state.HasStep(2));
            Assert.AreEqual(2, state.LastReceivedStep);

            var inputs0 = state.GetInputsForStep(0);
            Assert.IsNotNull(inputs0);
            Assert.AreEqual(1, inputs0.Count);
            Assert.AreEqual("a", inputs0[0]);
        }

        [Test]
        public void AddStepInputs_DetectsMissedStep()
        {
            var state = new InputSyncerState();
            bool stepMissedFired = false;
            state.OnStepMissed = () => stepMissedFired = true;

            // Add step 0
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() }
            });

            Assert.IsFalse(stepMissedFired);

            // Skip step 1, add step 2 — should trigger OnStepMissed
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object>() }
            });

            Assert.IsTrue(stepMissedFired);
        }

        [Test]
        public void HasStep_ReturnsFalse_ForMissingStep()
        {
            var state = new InputSyncerState();

            Assert.IsFalse(state.HasStep(0));
            Assert.IsFalse(state.HasStep(99));
        }
    }
}

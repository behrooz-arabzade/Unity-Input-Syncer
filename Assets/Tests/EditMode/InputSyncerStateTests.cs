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

        // ---- New tests below ----

        [Test]
        public void AddStepInputs_InResyncMode_BuffersToTemporarySteps()
        {
            var state = new InputSyncerState();
            state.OnStepMissed = () => { };

            // Add step 0
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() }
            });

            // Skip step 1 → triggers resync mode
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object> { "buffered" } }
            });

            // Step 2 should NOT be in received steps (it's in temp buffer)
            Assert.IsFalse(state.HasStep(2));
            // LastReceivedStep should still be 0 (didn't advance)
            Assert.AreEqual(0, state.LastReceivedStep);

            // Add step 3 — also goes to temp buffer
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 3, inputs = new List<object> { "also buffered" } }
            });

            Assert.IsFalse(state.HasStep(3));
            Assert.AreEqual(0, state.LastReceivedStep);
        }

        [Test]
        public void AddAllStepInputs_ClearsStateAndRebuilds()
        {
            var state = new InputSyncerState();
            state.OnStepMissed = () => { };

            // Add initial steps
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "old" } }
            });

            // Resync with full history
            var allSteps = new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "a" } },
                new StepInputs { step = 1, inputs = new List<object> { "b" } },
                new StepInputs { step = 2, inputs = new List<object> { "c" } },
                new StepInputs { step = 3, inputs = new List<object> { "d" } },
                new StepInputs { step = 4, inputs = new List<object> { "e" } },
            };

            state.AddAllStepInputs(allSteps, 4);

            Assert.AreEqual(4, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(0));
            Assert.IsTrue(state.HasStep(1));
            Assert.IsTrue(state.HasStep(2));
            Assert.IsTrue(state.HasStep(3));
            Assert.IsTrue(state.HasStep(4));

            // Verify rebuilt data
            var inputs0 = state.GetInputsForStep(0);
            Assert.AreEqual(1, inputs0.Count);
            Assert.AreEqual("a", inputs0[0]);
        }

        [Test]
        public void AddAllStepInputs_FillsMissingStepsWithEmptyInputs()
        {
            var state = new InputSyncerState();
            state.OnStepMissed = () => { };

            // Provide steps 0 and 3, but serverLastSentStep is 3
            // Steps 1 and 2 are missing in the list
            var allSteps = new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "a" } },
                new StepInputs { step = 3, inputs = new List<object> { "d" } },
            };

            state.AddAllStepInputs(allSteps, 3);

            Assert.AreEqual(3, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(1));
            Assert.IsTrue(state.HasStep(2));

            // Missing steps should have empty input lists
            var inputs1 = state.GetInputsForStep(1);
            Assert.IsNotNull(inputs1);
            Assert.AreEqual(0, inputs1.Count);

            var inputs2 = state.GetInputsForStep(2);
            Assert.IsNotNull(inputs2);
            Assert.AreEqual(0, inputs2.Count);
        }

        [Test]
        public void AddAllStepInputs_ProcessesTemporarySteps()
        {
            var state = new InputSyncerState();
            state.OnStepMissed = () => { };

            // Add step 0
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() }
            });

            // Skip step 1 → triggers resync, step 2 goes to temp
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object> { "from-temp" } }
            });

            // Now resync with steps 0-1, serverLastSentStep=1
            // Temp step 2 should be merged after resync
            var allSteps = new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "a" } },
                new StepInputs { step = 1, inputs = new List<object> { "b" } },
            };

            state.AddAllStepInputs(allSteps, 1);

            // Step 2 from temp should now be in received steps
            Assert.AreEqual(2, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(2));
            var inputs2 = state.GetInputsForStep(2);
            Assert.AreEqual(1, inputs2.Count);
            Assert.AreEqual("from-temp", inputs2[0]);
        }

        [Test]
        public void AddAllStepInputs_DetectsGapInTemporarySteps()
        {
            var state = new InputSyncerState();
            int stepMissedCount = 0;
            state.OnStepMissed = () => stepMissedCount++;

            // Add step 0
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() }
            });

            // Skip step 1 → triggers first resync
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object>() }
            });
            Assert.AreEqual(1, stepMissedCount);

            // Step 4 also goes to temp (skipping 3)
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 4, inputs = new List<object>() }
            });

            // Resync with steps 0-1, serverLastSentStep=1
            // Temp has steps 2 and 4 (gap at 3) → should re-trigger OnStepMissed
            var allSteps = new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() },
                new StepInputs { step = 1, inputs = new List<object>() },
            };

            state.AddAllStepInputs(allSteps, 1);

            Assert.AreEqual(2, stepMissedCount);
        }

        [Test]
        public void GetInputsForStep_ReturnsNull_ForMissingStep()
        {
            var state = new InputSyncerState();

            Assert.IsNull(state.GetInputsForStep(0));
            Assert.IsNull(state.GetInputsForStep(99));
        }

        [Test]
        public void AddStepInputs_SortsInputsByStepBeforeProcessing()
        {
            var state = new InputSyncerState();

            // Provide steps out of order — they should be sorted and processed correctly
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object> { "c" } },
                new StepInputs { step = 0, inputs = new List<object> { "a" } },
                new StepInputs { step = 1, inputs = new List<object> { "b" } },
            });

            Assert.AreEqual(2, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(0));
            Assert.IsTrue(state.HasStep(1));
            Assert.IsTrue(state.HasStep(2));
        }

        [Test]
        public void AddStepInputs_FirstStepNotZero_TriggersGap()
        {
            var state = new InputSyncerState();
            bool stepMissedFired = false;
            state.OnStepMissed = () => stepMissedFired = true;

            // First step is 1 (not 0) — should trigger gap since LastReceivedStep starts at -1
            // -1 + 1 = 0 != 1
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 1, inputs = new List<object>() }
            });

            Assert.IsTrue(stepMissedFired);
            Assert.AreEqual(-1, state.LastReceivedStep);
        }

        [Test]
        public void OnStepMissed_FiresOnce_UntilResync()
        {
            var state = new InputSyncerState();
            int stepMissedCount = 0;
            state.OnStepMissed = () => stepMissedCount++;

            // Add step 0
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() }
            });

            // Skip step 1 → fires OnStepMissed once
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object>() }
            });
            Assert.AreEqual(1, stepMissedCount);

            // More steps arrive while in resync mode — OnStepMissed should NOT fire again
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 5, inputs = new List<object>() }
            });
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 10, inputs = new List<object>() }
            });

            Assert.AreEqual(1, stepMissedCount);
        }

        [Test]
        public void GetInputsForStep_OrdersByIndexProperty()
        {
            var state = new InputSyncerState();

            // Create inputs with index property via anonymous objects
            var input0 = new InputWithIndex { index = 2, value = "c" };
            var input1 = new InputWithIndex { index = 0, value = "a" };
            var input2 = new InputWithIndex { index = 1, value = "b" };

            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { input0, input1, input2 } }
            });

            var result = state.GetInputsForStep(0);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("a", ((InputWithIndex)result[0]).value);
            Assert.AreEqual("b", ((InputWithIndex)result[1]).value);
            Assert.AreEqual("c", ((InputWithIndex)result[2]).value);
        }
    }

    public class InputWithIndex
    {
        public long index { get; set; }
        public string value;
    }
}

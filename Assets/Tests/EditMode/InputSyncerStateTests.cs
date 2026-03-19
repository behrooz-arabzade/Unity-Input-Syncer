using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityInputSyncerClient;
using UnityInputSyncerClient.Tests;

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
        public void GetInputsForStep_ReturnsEmptyList_ForMissingStep()
        {
            var state = new InputSyncerState();

            var result0 = state.GetInputsForStep(0);
            Assert.IsNotNull(result0);
            Assert.AreEqual(0, result0.Count);

            var result99 = state.GetInputsForStep(99);
            Assert.IsNotNull(result99);
            Assert.AreEqual(0, result99.Count);
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
        public void GetInputsForStep_OrdersByIndex_JObject()
        {
            var state = new InputSyncerState();

            var input0 = new JObject { ["index"] = 2, ["value"] = "c" };
            var input1 = new JObject { ["index"] = 0, ["value"] = "a" };
            var input2 = new JObject { ["index"] = 1, ["value"] = "b" };

            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { input0, input1, input2 } }
            });

            var result = state.GetInputsForStep(0);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("a", ((JObject)result[0])["value"].ToString());
            Assert.AreEqual("b", ((JObject)result[1])["value"].ToString());
            Assert.AreEqual("c", ((JObject)result[2])["value"].ToString());
        }

        [Test]
        public void GetInputsForStep_OrdersBaseInputDataByIndex()
        {
            var state = new InputSyncerState();

            var input0 = new TestInput(new TestInputData { action = "c", value = 3 }) { index = 2 };
            var input1 = new TestInput(new TestInputData { action = "a", value = 1 }) { index = 0 };
            var input2 = new TestInput(new TestInputData { action = "b", value = 2 }) { index = 1 };

            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { input0, input1, input2 } }
            });

            var result = state.GetInputsForStep(0);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("a", ((TestInput)result[0]).GetData<TestInputData>().action);
            Assert.AreEqual("b", ((TestInput)result[1]).GetData<TestInputData>().action);
            Assert.AreEqual("c", ((TestInput)result[2]).GetData<TestInputData>().action);
        }

        // ---- Reconnection flow tests ----

        [Test]
        public void FullReconnectionLifecycle_StepsResumeNormally()
        {
            var state = new InputSyncerState();
            int stepMissedCount = 0;
            state.OnStepMissed = () => stepMissedCount++;

            // Steps 0-2 arrive normally
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "a" } },
                new StepInputs { step = 1, inputs = new List<object> { "b" } },
                new StepInputs { step = 2, inputs = new List<object> { "c" } },
            });
            Assert.AreEqual(2, state.LastReceivedStep);

            // Gap: skip step 3, receive step 4 → triggers resync
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 4, inputs = new List<object> { "e-temp" } }
            });
            Assert.AreEqual(1, stepMissedCount);
            Assert.IsFalse(state.HasStep(4)); // buffered in temp

            // Resync arrives with steps 0-3, serverLastSentStep=3
            // Temp step 4 should merge automatically
            state.AddAllStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "a" } },
                new StepInputs { step = 1, inputs = new List<object> { "b" } },
                new StepInputs { step = 2, inputs = new List<object> { "c" } },
                new StepInputs { step = 3, inputs = new List<object> { "d" } },
            }, 3);

            Assert.AreEqual(4, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(4));

            // Step 5 arrives normally after resync
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 5, inputs = new List<object> { "f" } }
            });

            Assert.AreEqual(5, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(5));
            Assert.AreEqual(1, stepMissedCount, "OnStepMissed should have fired exactly once");
        }

        [Test]
        public void AddAllStepInputs_WithEmptyHistory_SetsStateCorrectly()
        {
            var state = new InputSyncerState();

            // Server has sent step 0 but with no inputs (empty history)
            state.AddAllStepInputs(new List<StepInputs>(), 0);

            Assert.AreEqual(0, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(0));

            var inputs = state.GetInputsForStep(0);
            Assert.IsNotNull(inputs);
            Assert.AreEqual(0, inputs.Count);
        }

        [Test]
        public void AddAllStepInputs_DuplicateStepInTempAndHistory_HistoryWins()
        {
            var state = new InputSyncerState();
            state.OnStepMissed = () => { };

            // Step 0 arrives normally
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() }
            });

            // Gap: skip step 1, step 2 goes to temp with "temp" data
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object> { "temp-data" } }
            });

            // Resync includes step 2 with "server" data, serverLastSentStep=2
            // Server history should win for step 2 (temp step 2 is skipped because key <= LastReceivedStep)
            state.AddAllStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "s0" } },
                new StepInputs { step = 1, inputs = new List<object> { "s1" } },
                new StepInputs { step = 2, inputs = new List<object> { "server-data" } },
            }, 2);

            Assert.AreEqual(2, state.LastReceivedStep);
            var inputs2 = state.GetInputsForStep(2);
            Assert.AreEqual(1, inputs2.Count);
            Assert.AreEqual("server-data", inputs2[0], "Server history should override temp step data");
        }

        [Test]
        public void MultipleSequentialReconnections_AllResolve()
        {
            var state = new InputSyncerState();
            int stepMissedCount = 0;
            state.OnStepMissed = () => stepMissedCount++;

            // Step 0 arrives normally
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() }
            });

            // First gap: skip step 1, step 2 goes to temp
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object> { "first-temp" } }
            });
            Assert.AreEqual(1, stepMissedCount);

            // First resync: steps 0-1, serverLastSentStep=1 → merges temp step 2
            state.AddAllStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() },
                new StepInputs { step = 1, inputs = new List<object>() },
            }, 1);

            Assert.AreEqual(2, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(2));

            // Second gap: skip step 3, step 4 goes to temp
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 4, inputs = new List<object> { "second-temp" } }
            });
            Assert.AreEqual(2, stepMissedCount);

            // Second resync: steps 0-3, serverLastSentStep=3 → merges temp step 4
            state.AddAllStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() },
                new StepInputs { step = 1, inputs = new List<object>() },
                new StepInputs { step = 2, inputs = new List<object>() },
                new StepInputs { step = 3, inputs = new List<object>() },
            }, 3);

            Assert.AreEqual(4, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(4));
            Assert.AreEqual(2, stepMissedCount, "OnStepMissed should have fired exactly twice");
        }

        [Test]
        public void AddAllStepInputs_AfterResync_NormalStepsResumeWithoutBuffering()
        {
            var state = new InputSyncerState();
            state.OnStepMissed = () => { };

            // Step 0 arrives normally
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() }
            });

            // Gap: skip step 1, step 2 goes to temp
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object>() }
            });

            // Resync: steps 0-1, serverLastSentStep=1 → merges temp step 2
            state.AddAllStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() },
                new StepInputs { step = 1, inputs = new List<object>() },
            }, 1);

            Assert.AreEqual(2, state.LastReceivedStep);

            // Step 3 arrives normally — should go directly to ReceivedSteps, not temp
            state.AddStepInputs(new List<StepInputs>
            {
                new StepInputs { step = 3, inputs = new List<object> { "normal" } }
            });

            Assert.AreEqual(3, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(3), "Step 3 should be in ReceivedSteps, not buffered");
            var inputs = state.GetInputsForStep(3);
            Assert.AreEqual(1, inputs.Count);
            Assert.AreEqual("normal", inputs[0]);
        }
    }

}

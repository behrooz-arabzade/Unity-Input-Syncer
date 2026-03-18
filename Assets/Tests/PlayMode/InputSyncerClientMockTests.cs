using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityInputSyncerClient;
using UnityInputSyncerClient.Tests;
using UnityInputSyncerCore.Utils;

namespace Tests.PlayMode
{
    public class InputSyncerClientMockTests
    {
        [Test]
        public void MockClient_ConnectAsync_ReturnsTrue()
        {
            var options = new InputSyncerClientOptions { Mock = true };
            var client = new InputSyncerClient(null, options);

            var task = client.ConnectAsync();

            Assert.IsTrue(task.Result);

            client.Dispose();
        }

        [UnityTest]
        public IEnumerator MockClient_ProducesSteps_AfterConnect()
        {
            // Ensure the dispatcher singleton exists so Update() ticks
            _ = UnityThreadDispatcher.Instance;

            var options = new InputSyncerClientOptions
            {
                Mock = true,
                StepIntervalMs = 50
            };
            var client = new InputSyncerClient(null, options);

            var task = client.ConnectAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            Assert.IsTrue(task.Result);

            var state = client.GetState();

            // Wait until at least step 0 appears, with a timeout
            float elapsed = 0f;
            while (!state.HasStep(0) && elapsed < 3f)
            {
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            Assert.IsTrue(state.HasStep(0), "Expected at least step 0 to be present");
            Assert.IsTrue(state.LastReceivedStep >= 0, "Expected LastReceivedStep >= 0");

            client.Dispose();
        }

        // ---- New tests below ----

        [UnityTest]
        public IEnumerator MockClient_SendInput_AppearsInStepOutput()
        {
            _ = UnityThreadDispatcher.Instance;

            var options = new InputSyncerClientOptions
            {
                Mock = true,
                StepIntervalMs = 50,
                MockCurrentUserId = "test-user"
            };
            var client = new InputSyncerClient(null, options);

            // Send input before connecting so it's queued for the first step
            client.SendInput(new TestInput(new TestInputData { action = "jump", value = 1 }));

            var task = client.ConnectAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            var state = client.GetState();

            float elapsed = 0f;
            while (!state.HasStep(0) && elapsed < 3f)
            {
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            Assert.IsTrue(state.HasStep(0));
            var inputs = state.GetInputsForStep(0);
            Assert.IsNotNull(inputs);
            Assert.IsTrue(inputs.Count >= 1, "Expected at least 1 input in step 0");

            // Verify it's a BaseInputData with the correct userId
            var firstInput = inputs[0] as BaseInputData;
            Assert.IsNotNull(firstInput, "Input should be a BaseInputData");
            Assert.AreEqual("test-user", firstInput.userId);
            Assert.AreEqual(0, firstInput.index);

            client.Dispose();
        }

        [UnityTest]
        public IEnumerator MockClient_JoinMatch_ProducesJoinInputInStep()
        {
            _ = UnityThreadDispatcher.Instance;

            var options = new InputSyncerClientOptions
            {
                Mock = true,
                StepIntervalMs = 50,
                MockCurrentUserId = "test-user"
            };
            var client = new InputSyncerClient(null, options);

            client.JoinMatch("joining-player");

            var task = client.ConnectAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            var state = client.GetState();

            float elapsed = 0f;
            while (!state.HasStep(0) && elapsed < 3f)
            {
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            Assert.IsTrue(state.HasStep(0));
            var inputs = state.GetInputsForStep(0);
            Assert.IsNotNull(inputs);

            // Find the JoinInput
            var joinInput = inputs.OfType<JoinInput>().FirstOrDefault();
            Assert.IsNotNull(joinInput, "Expected a JoinInput in step 0");
            Assert.AreEqual("user-join", joinInput.type);

            client.Dispose();
        }

        [UnityTest]
        public IEnumerator MockClient_OnMatchStarted_FiresOnce()
        {
            _ = UnityThreadDispatcher.Instance;

            var options = new InputSyncerClientOptions
            {
                Mock = true,
                StepIntervalMs = 50
            };
            var client = new InputSyncerClient(null, options);

            int matchStartedCount = 0;
            client.OnMatchStarted = () => matchStartedCount++;

            var task = client.ConnectAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            var state = client.GetState();

            // Wait for multiple steps
            float elapsed = 0f;
            while (state.LastReceivedStep < 3 && elapsed < 5f)
            {
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            Assert.AreEqual(1, matchStartedCount, "OnMatchStarted should fire exactly once");

            client.Dispose();
        }

        [UnityTest]
        public IEnumerator MockClient_Dispose_StopsStepProduction()
        {
            _ = UnityThreadDispatcher.Instance;

            var options = new InputSyncerClientOptions
            {
                Mock = true,
                StepIntervalMs = 50
            };
            var client = new InputSyncerClient(null, options);

            var task = client.ConnectAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            var state = client.GetState();

            // Wait for at least step 0
            float elapsed = 0f;
            while (!state.HasStep(0) && elapsed < 3f)
            {
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            int stepsAfterDispose = state.LastReceivedStep;
            client.Dispose();

            // Wait a bit to see if more steps arrive
            yield return new WaitForSeconds(0.3f);

            // Allow at most 1 more step (in-flight at dispose time)
            Assert.LessOrEqual(state.LastReceivedStep, stepsAfterDispose + 1,
                "No significant new steps should appear after dispose");
        }

        [UnityTest]
        public IEnumerator MockClient_StepIntervalMs_ControlsTiming()
        {
            _ = UnityThreadDispatcher.Instance;

            var options = new InputSyncerClientOptions
            {
                Mock = true,
                StepIntervalMs = 200 // slow interval
            };
            var client = new InputSyncerClient(null, options);

            var task = client.ConnectAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            var state = client.GetState();

            // Wait 500ms — at 200ms intervals, expect roughly 2-3 steps
            yield return new WaitForSeconds(0.5f);

            Assert.GreaterOrEqual(state.LastReceivedStep, 1, "Should have at least 2 steps in 500ms with 200ms interval");
            Assert.LessOrEqual(state.LastReceivedStep, 5, "Should not have too many steps");

            client.Dispose();
        }

        [UnityTest]
        public IEnumerator MockClient_MultipleInputs_GetSequentialIndices()
        {
            _ = UnityThreadDispatcher.Instance;

            var options = new InputSyncerClientOptions
            {
                Mock = true,
                StepIntervalMs = 200, // long enough to queue multiple inputs
                MockCurrentUserId = "test-user"
            };
            var client = new InputSyncerClient(null, options);

            // Queue 3 inputs before connecting
            client.SendInput(new TestInput(new TestInputData { action = "a", value = 1 }));
            client.SendInput(new TestInput(new TestInputData { action = "b", value = 2 }));
            client.SendInput(new TestInput(new TestInputData { action = "c", value = 3 }));

            var task = client.ConnectAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            var state = client.GetState();

            float elapsed = 0f;
            while (!state.HasStep(0) && elapsed < 3f)
            {
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            Assert.IsTrue(state.HasStep(0));
            var inputs = state.GetInputsForStep(0);
            Assert.IsNotNull(inputs);
            Assert.AreEqual(3, inputs.Count, "Expected 3 inputs in first step");

            // Verify sequential indices
            for (int i = 0; i < 3; i++)
            {
                var input = inputs[i] as BaseInputData;
                Assert.IsNotNull(input);
                Assert.AreEqual(i, input.index, $"Input at position {i} should have index {i}");
            }

            client.Dispose();
        }
    }
}

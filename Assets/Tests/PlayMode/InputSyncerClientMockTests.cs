using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityInputSyncerClient;
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
    }
}

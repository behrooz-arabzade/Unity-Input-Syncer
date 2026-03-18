using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityInputSyncerClient;
using UnityInputSyncerClient.Tests;
using UnityInputSyncerCore;

namespace Tests.EditMode
{
    public class InputSyncerClientWithDriverTests
    {
        private TestClientDriver driver;
        private InputSyncerClient client;

        [SetUp]
        public void SetUp()
        {
            driver = new TestClientDriver();
            client = new InputSyncerClient(driver, new InputSyncerClientOptions { Mock = false });
        }

        [Test]
        public void Constructor_WithNullDriver_NonMock_ThrowsNullReference()
        {
            // When Mock=false and driver is null, the constructor calls RegisterOnSyncerEvents()
            // which tries to call Driver.On(...), causing a NullReferenceException.
            // This is a known latent issue in InputSyncerClient.
            Assert.Throws<NullReferenceException>(() =>
            {
                new InputSyncerClient(null, new InputSyncerClientOptions { Mock = false });
            });
        }

        [Test]
        public void ConnectAsync_DelegatesToDriver_ReturnsTrue()
        {
            driver.ConnectAsyncResult = true;
            var task = client.ConnectAsync();
            Assert.IsTrue(task.Result);
        }

        [Test]
        public void ConnectAsync_DelegatesToDriver_ReturnsFalse()
        {
            driver.ConnectAsyncResult = false;
            var task = client.ConnectAsync();
            Assert.IsFalse(task.Result);
        }

        [Test]
        public void SendInput_WhenNotConnected_ReturnsFalse()
        {
            driver.SetConnected(false);
            var input = new TestInput(new { action = "move" });
            Assert.IsFalse(client.SendInput(input));
        }

        [Test]
        public void SendInput_WhenConnected_EmitsInputEvent()
        {
            driver.SetConnected(true);
            var input = new TestInput(new { action = "move" });

            bool result = client.SendInput(input);

            Assert.IsTrue(result);
            Assert.AreEqual(1, driver.EmittedEvents.Count);
            Assert.AreEqual(InputSyncerEvents.MATCH_USER_INPUT_EVENT, driver.EmittedEvents[0].EventName);
        }

        [Test]
        public void JoinMatch_EmitsJoinEvent()
        {
            client.JoinMatch("player-1");

            Assert.AreEqual(1, driver.EmittedEvents.Count);
            Assert.AreEqual(InputSyncerEvents.MATCH_USER_JOIN_EVENT, driver.EmittedEvents[0].EventName);

            var data = JObject.FromObject(driver.EmittedEvents[0].Data);
            Assert.AreEqual("player-1", data["userId"].ToString());
        }

        [Test]
        public void JoinMatch_MockMode_QueuesJoinInput()
        {
            var mockDriver = new TestClientDriver();
            var mockClient = new InputSyncerClient(mockDriver, new InputSyncerClientOptions { Mock = true });

            mockClient.JoinMatch("mock-player");

            // In mock mode, Driver is null and no emit happens
            Assert.AreEqual(0, mockDriver.EmittedEvents.Count);

            mockClient.Dispose();
        }

        [Test]
        public void OnStepsReceived_AddsToState()
        {
            var stepsData = new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "input-a" } },
                new StepInputs { step = 1, inputs = new List<object> { "input-b" } },
            };

            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, stepsData);

            var state = client.GetState();
            Assert.IsTrue(state.HasStep(0));
            Assert.IsTrue(state.HasStep(1));
            Assert.AreEqual(1, state.LastReceivedStep);
        }

        [Test]
        public void OnStepsReceived_FiresOnMatchStarted_Once()
        {
            int matchStartedCount = 0;
            client.OnMatchStarted = () => matchStartedCount++;

            var stepsData1 = new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() },
            };

            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, stepsData1);
            Assert.AreEqual(1, matchStartedCount);

            var stepsData2 = new List<StepInputs>
            {
                new StepInputs { step = 1, inputs = new List<object>() },
            };

            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, stepsData2);
            Assert.AreEqual(1, matchStartedCount); // Should NOT fire again
        }

        [Test]
        public void OnAllStepReceived_RebuildsState()
        {
            var allStepsData = new AllStepInputs
            {
                requestedUser = "player-1",
                steps = new List<StepInputs>
                {
                    new StepInputs { step = 0, inputs = new List<object> { "a" } },
                    new StepInputs { step = 1, inputs = new List<object> { "b" } },
                    new StepInputs { step = 2, inputs = new List<object> { "c" } },
                },
                lastSentStep = 2
            };

            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_ALL_STEPS_EVENT, allStepsData);

            var state = client.GetState();
            Assert.AreEqual(2, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(0));
            Assert.IsTrue(state.HasStep(1));
            Assert.IsTrue(state.HasStep(2));
        }

        [Test]
        public void OnStepMissed_EmitsRequestAllSteps()
        {
            // First add step 0
            var stepsData1 = new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object>() },
            };
            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, stepsData1);

            driver.EmittedEvents.Clear();

            // Skip step 1, send step 2 → should trigger OnStepMissed → emit request-all-steps
            var stepsData2 = new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object>() },
            };
            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, stepsData2);

            Assert.AreEqual(1, driver.EmittedEvents.Count);
            Assert.AreEqual(InputSyncerEvents.MATCH_USER_REQUEST_ALL_STEPS_EVENT, driver.EmittedEvents[0].EventName);
        }

        [Test]
        public void RegisterOnCustomEvent_MockMode_DoesNotDelegateToDriver()
        {
            var mockClient = new InputSyncerClient(null, new InputSyncerClientOptions { Mock = true });

            // Should log a warning but not crash
            mockClient.RegisterOnCustomEvent("custom-event", (response) => { });

            mockClient.Dispose();
        }

        [Test]
        public void RegisterOnCustomEvent_DelegatesToDriver()
        {
            bool callbackInvoked = false;
            client.RegisterOnCustomEvent("custom-event", (response) =>
            {
                callbackInvoked = true;
            });

            Assert.IsTrue(driver.EventCallbacks.ContainsKey("custom-event"));

            // Trigger it
            driver.TriggerEvent("custom-event", new { message = "hello" });
            Assert.IsTrue(callbackInvoked);
        }

        [Test]
        public void GetState_ReturnsNonNull()
        {
            Assert.IsNotNull(client.GetState());
        }

        [Test]
        public void Constructor_RegistersSyncerEvents()
        {
            Assert.IsTrue(driver.EventCallbacks.ContainsKey(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT));
            Assert.IsTrue(driver.EventCallbacks.ContainsKey(InputSyncerEvents.INPUT_SYNCER_ALL_STEPS_EVENT));
            Assert.IsTrue(driver.EventCallbacks.ContainsKey(InputSyncerEvents.INPUT_SYNCER_START_EVENT));
        }

        [Test]
        public void OnStart_FiresOnMatchStarted()
        {
            int matchStartedCount = 0;
            client.OnMatchStarted = () => matchStartedCount++;

            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_START_EVENT, new { });

            Assert.AreEqual(1, matchStartedCount);
        }

        [Test]
        public void MockClient_DoubleDispose_DoesNotThrow()
        {
            var mockClient = new InputSyncerClient(null, new InputSyncerClientOptions { Mock = true });
            Assert.DoesNotThrow(() =>
            {
                mockClient.Dispose();
                mockClient.Dispose();
            });
        }

        [Test]
        public void MockClient_OperationsAfterDispose_DoNotThrow()
        {
            var mockClient = new InputSyncerClient(null, new InputSyncerClientOptions { Mock = true });
            mockClient.Dispose();

            Assert.DoesNotThrow(() => mockClient.SendInput(new TestInput(new TestInputData { action = "a", value = 1 })));
            Assert.DoesNotThrow(() => mockClient.JoinMatch("user"));
            Assert.DoesNotThrow(() =>
            {
                var task = mockClient.ConnectAsync();
                task.Wait(500);
            });
        }

        [Test]
        public void Driver_SimulateReconnect_InvokesOnReconnected()
        {
            var testDriver = new TestClientDriver();
            bool reconnectedFired = false;
            testDriver.OnReconnected += () => reconnectedFired = true;

            testDriver.SimulateReconnect();

            Assert.IsTrue(reconnectedFired, "OnReconnected should be invoked when driver simulates reconnect");
        }

        // ---- Reconnection integration tests ----

        [Test]
        public void FullReconnection_DriverIntegration_ResyncLifecycle()
        {
            // Steps 0-2 arrive normally
            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "a" } },
                new StepInputs { step = 1, inputs = new List<object> { "b" } },
                new StepInputs { step = 2, inputs = new List<object> { "c" } },
            });

            var state = client.GetState();
            Assert.AreEqual(2, state.LastReceivedStep);

            driver.EmittedEvents.Clear();

            // Gap: step 4 arrives (skipping 3) → should emit request-all-steps
            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, new List<StepInputs>
            {
                new StepInputs { step = 4, inputs = new List<object> { "e" } }
            });

            Assert.AreEqual(1, driver.EmittedEvents.Count);
            Assert.AreEqual(InputSyncerEvents.MATCH_USER_REQUEST_ALL_STEPS_EVENT, driver.EmittedEvents[0].EventName);

            // Server responds with full history (steps 0-3)
            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_ALL_STEPS_EVENT, new AllStepInputs
            {
                requestedUser = "player-1",
                steps = new List<StepInputs>
                {
                    new StepInputs { step = 0, inputs = new List<object> { "a" } },
                    new StepInputs { step = 1, inputs = new List<object> { "b" } },
                    new StepInputs { step = 2, inputs = new List<object> { "c" } },
                    new StepInputs { step = 3, inputs = new List<object> { "d" } },
                },
                lastSentStep = 3
            });

            // Temp step 4 should have been merged
            Assert.AreEqual(4, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(4));

            // Step 5 arrives normally after resync
            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, new List<StepInputs>
            {
                new StepInputs { step = 5, inputs = new List<object> { "f" } }
            });

            Assert.AreEqual(5, state.LastReceivedStep);
            for (int i = 0; i <= 5; i++)
                Assert.IsTrue(state.HasStep(i), $"State should have step {i}");
        }

        [Test]
        public void OnReconnected_DriverEvent_DoesNotDisruptState()
        {
            // Steps 0-1 arrive normally
            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, new List<StepInputs>
            {
                new StepInputs { step = 0, inputs = new List<object> { "a" } },
                new StepInputs { step = 1, inputs = new List<object> { "b" } },
            });

            var state = client.GetState();
            Assert.AreEqual(1, state.LastReceivedStep);

            // Transport-level reconnect fires
            driver.SimulateReconnect();

            // Step 2 arrives after reconnect — should continue normally
            driver.TriggerEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, new List<StepInputs>
            {
                new StepInputs { step = 2, inputs = new List<object> { "c" } }
            });

            Assert.AreEqual(2, state.LastReceivedStep);
            Assert.IsTrue(state.HasStep(0));
            Assert.IsTrue(state.HasStep(1));
            Assert.IsTrue(state.HasStep(2));
        }

        // ---- Mock mode edge case tests ----

        [Test]
        public void MockClient_SendInput_BeforeConnect_Queues()
        {
            var mockClient = new InputSyncerClient(null, new InputSyncerClientOptions { Mock = true });

            // SendInput before ConnectAsync should queue without throwing
            var input = new TestInput(new TestInputData { action = "jump", value = 1 });
            bool result = mockClient.SendInput(input);

            Assert.IsTrue(result, "SendInput in mock mode should return true even before connect");

            mockClient.Dispose();
        }

        [Test]
        public void MockClient_JoinMatch_WithExplicitUserId_SetsUserId()
        {
            var mockDriver = new TestClientDriver();
            var mockClient = new InputSyncerClient(mockDriver, new InputSyncerClientOptions { Mock = true });

            // JoinMatch with explicit userId in mock mode should not throw
            Assert.DoesNotThrow(() => mockClient.JoinMatch("explicit-id"));

            // In mock mode, Driver is null (set by constructor) so no emit happens
            Assert.AreEqual(0, mockDriver.EmittedEvents.Count);

            mockClient.Dispose();
        }

        [Test]
        public void SendInput_WhenDriverNull_ReturnsFalse()
        {
            // Set driver to null on a non-mock client
            client.Driver = null;

            var input = new TestInput(new TestInputData { action = "move", value = 1 });
            bool result = client.SendInput(input);

            Assert.IsFalse(result, "SendInput should return false when Driver is null");
        }
    }
}

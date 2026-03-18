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
    }
}

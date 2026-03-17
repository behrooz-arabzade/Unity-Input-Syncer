using System;
using System.Collections.Generic;
using UnityEngine;
using UnityInputSyncerClient.Drivers;

namespace UnityInputSyncerClient.Examples
{
    // Can be an example of a MatchSessionManager in your game
    public class UTPSocketDriverExample : MonoBehaviour
    {
        private InputSyncerClient inputSyncerClient;

        public void StartMatchSession()
        {
            var driverOptions = new UTPDriverOptions
            {
                Port = 7777,
                Ip = "127.0.0.1",
                Payload = new Dictionary<string, string>
                {
                    { "matchId", "example-match-id" },
                    { "userId", "example-user-id" },
                    { "custom-match-data", "example-custom-json-data" },
                },
                JwtToken = "example-match-jwt-token",

            };

            var inputSyncerOptions = new InputSyncerClientOptions
            {
                StepIntervalMs = 100,
            };

            inputSyncerClient = new InputSyncerClient(
               new UTPClientDriver(driverOptions),
               inputSyncerOptions
            );


            InjectInputSyncerToAppropriateSystems(inputSyncerClient);

            RegisterOnSyncerEvents(inputSyncerClient);

            inputSyncerClient.ConnectAsync().ContinueWith(task =>
            {
                if (task.Result)
                {
                    Debug.Log("Connected to match session successfully.");
                }
                else
                {
                    Debug.LogError("Failed to connect to match session.");
                }
            });
        }

        private void RegisterOnSyncerEvents(InputSyncerClient inputSyncerClient)
        {
            inputSyncerClient.OnMatchStarted += () =>
            {
                Debug.Log("Match started");
            };

            inputSyncerClient.RegisterOnCustomEvent("example-event", (response) =>
            {
                var eventData = inputSyncerClient.Driver.GetData<Dictionary<string, object>>(response);
                Debug.Log("Received custom event 'example-event' with data: " + JsonUtility.ToJson(eventData));
            });

            inputSyncerClient.Driver.OnConnected += () =>
            {
                Debug.Log("Socket.IO connected");
            };

            inputSyncerClient.Driver.OnDisconnected += (reason) =>
            {
                Debug.Log("Socket.IO disconnected: " + reason);
            };

            inputSyncerClient.Driver.OnError += (errorMessage) =>
            {
                Debug.LogError("Socket.IO error: " + errorMessage);
            };

            inputSyncerClient.Driver.OnReconnected += () =>
            {
                Debug.Log("Socket.IO reconnected");
            };
        }

        public Action<InputSyncerClient> OnInputSyncerReady = (inputSyncerClient) => { };

        private void InjectInputSyncerToAppropriateSystems(InputSyncerClient inputSyncerClient)
        {
            // Example: Inject the inputSyncerClient into your game's input handling systems
            // This is highly dependent on your game's architecture and is left as a placeholder

            // Or

            OnInputSyncerReady(inputSyncerClient);
        }

        public void FixedUpdate()
        {
            // Check if the step has already synced
            if (!inputSyncerClient.GetState().HasStep(1))
            {
                return;
            }

            var inputs = inputSyncerClient.GetState().GetInputsForStep(1);

            foreach (var input in inputs)
            {
                Debug.Log($"Received input for step 1: UserId={input.UserId}, InputData={JsonUtility.ToJson(input.InputData)}");
            }
        }
    }
}
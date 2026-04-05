using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityInputSyncerClient.Drivers;

namespace UnityInputSyncerClient.Examples.ServerSimulation
{
    /// <summary>
    /// Client-side example for the server simulation scenario.
    /// Sends WASD move inputs and displays the authoritative game state
    /// received from the server.
    /// </summary>
    public class ServerSimulationClientExample : MonoBehaviour
    {
        [SerializeField] private string serverIp = "127.0.0.1";
        [SerializeField] private ushort serverPort = 7777;
        [SerializeField] private string userId = "player-1";

        private InputSyncerClient client;
        private SimulationGameState latestState;

        void Start()
        {
            var driverOptions = new UTPDriverOptions
            {
                Ip = serverIp,
                Port = serverPort,
                Payload = new Dictionary<string, string>
                {
                    { "matchId", "sim-match" },
                    { "userId", userId },
                },
            };

            var clientOptions = new InputSyncerClientOptions
            {
                StepIntervalMs = 100,
            };

            client = new InputSyncerClient(
                new UTPClientDriver(driverOptions),
                clientOptions
            );

            client.RegisterOnCustomEvent("game-state", response =>
            {
                var state = client.Driver.GetData<SimulationGameState>(response);
                latestState = state;
            });

            client.ConnectAsync().ContinueWith(task =>
            {
                if (task.Result)
                {
                    Debug.Log("[SimClient] Connected");
                    client.JoinMatch(userId);
                }
                else
                {
                    Debug.LogError("[SimClient] Failed to connect");
                }
            });
        }

        void Update()
        {
            if (client == null)
                return;

            int dx = 0, dy = 0;

            if (Input.GetKeyDown(KeyCode.W)) dy = 1;
            if (Input.GetKeyDown(KeyCode.S)) dy = -1;
            if (Input.GetKeyDown(KeyCode.A)) dx = -1;
            if (Input.GetKeyDown(KeyCode.D)) dx = 1;

            if (dx != 0 || dy != 0)
            {
                client.SendInput(new MoveInput(new MoveInputData { dx = dx, dy = dy }));
            }
        }

        void OnGUI()
        {
            if (latestState == null)
            {
                GUI.Label(new Rect(10, 10, 300, 20), "Waiting for game state...");
                return;
            }

            GUI.Label(new Rect(10, 10, 300, 20), $"Step: {latestState.step}");

            int yOffset = 30;
            foreach (var kvp in latestState.players)
            {
                GUI.Label(new Rect(10, yOffset, 300, 20),
                    $"{kvp.Key}: ({kvp.Value.x}, {kvp.Value.y})");
                yOffset += 20;
            }
        }

        void OnDestroy()
        {
            client?.Dispose();
        }
    }
}

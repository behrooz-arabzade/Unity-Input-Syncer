using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityInputSyncerClient;
using UnityInputSyncerClient.Examples.ServerSimulation;

namespace UnityInputSyncerUTPServer.Examples
{
    /// <summary>
    /// Server-side simulation example. Attach this to the same GameObject as
    /// DedicatedServerBootstrap (or any object in the server scene).
    ///
    /// Each step, the server processes move inputs from all players, updates
    /// authoritative positions on a bounded grid, and broadcasts the full
    /// game state back to every client via a custom "game-state" event.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ServerSimulationExample : MonoBehaviour
    {
        [SerializeField] private DedicatedServerBootstrap bootstrap;
        [SerializeField] private int gridSize = 10;

        private Dictionary<string, PlayerPosition> playerPositions = new();

        void Start()
        {
            var server = bootstrap.Server;

            server.OnPlayerJoined += player =>
            {
                playerPositions[player.UserId] = new PlayerPosition { x = 0, y = 0 };
                Debug.Log($"[ServerSimulation] Player {player.UserId} joined — initialized at (0,0)");
            };

            server.OnPlayerDisconnected += player =>
            {
                if (player.UserId != null)
                {
                    playerPositions.Remove(player.UserId);
                    Debug.Log($"[ServerSimulation] Player {player.UserId} disconnected — removed from state");
                }
            };

            server.OnStepBroadcast += (step, stepInputs) =>
            {
                ProcessStep(step, stepInputs);

                var gameState = new SimulationGameState
                {
                    step = step,
                    players = playerPositions
                };

                string json = JsonConvert.SerializeObject(gameState);
                server.SendJsonToAll("game-state", json);
            };
        }

        private void ProcessStep(int step, StepInputs stepInputs)
        {
            foreach (var rawInput in stepInputs.inputs)
            {
                JObject input = JObject.FromObject(rawInput);

                string inputType = input.Value<string>("type");
                if (inputType != "move")
                    continue;

                string userId = input.Value<string>("userId");
                if (userId == null || !playerPositions.ContainsKey(userId))
                    continue;

                var data = input["data"] as JObject;
                if (data == null)
                    continue;

                int dx = data.Value<int>("dx");
                int dy = data.Value<int>("dy");

                var pos = playerPositions[userId];
                pos.x = Math.Clamp(pos.x + dx, 0, gridSize - 1);
                pos.y = Math.Clamp(pos.y + dy, 0, gridSize - 1);
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Networking.Transport;
using UnityEngine;
using UnityInputSyncerClient;
using UnityInputSyncerCore;
using UnityInputSyncerCore.UTPSocket;
using UnityInputSyncerCore.Utils;

namespace UnityInputSyncerUTPServer
{
    public class InputSyncerServer : IDisposable
    {
        // Events
        public event Action<InputSyncerServerPlayer> OnPlayerConnected;
        public event Action<InputSyncerServerPlayer> OnPlayerDisconnected;
        public event Action<InputSyncerServerPlayer> OnPlayerJoined;
        public event Action<InputSyncerServerPlayer> OnPlayerFinished;
        public event Action OnMatchStarted;
        public event Action OnMatchFinished;
        public event Action<int, StepInputs> OnStepBroadcast;

        private UTPSocketServer Socket;
        private InputSyncerServerOptions Options;
        private InputSyncerServerState State;
        private bool Disposed;

        private Dictionary<string, List<Action<NetworkConnection, JToken>>> customJsonCallbacks =
            new Dictionary<string, List<Action<NetworkConnection, JToken>>>();

        public InputSyncerServer(InputSyncerServerOptions options = null)
        {
            Options = options ?? new InputSyncerServerOptions();
            State = new InputSyncerServerState();

            Socket = new UTPSocketServer(new UTPSocketServerOptions
            {
                Port = Options.Port,
                HeartbeatTimeout = Options.HeartbeatTimeout,
            });

            RegisterSocketEvents();
            RegisterProtocolHandlers();
        }

        // -------------------------
        // LIFECYCLE
        // -------------------------

        public void Start()
        {
            Socket.Start();
            Debug.Log($"[InputSyncerServer] Server started on port {Options.Port}");
        }

        public void StartMatch()
        {
            if (State.MatchStarted)
                return;

            State.MatchStarted = true;
            State.CurrentStep = 0;
            State.StepAccumulator = 0f;

            PlayerLoopHook.Register(TickStep);

            SendJsonToAllJoined(InputSyncerEvents.INPUT_SYNCER_START_EVENT, "{}");
            OnMatchStarted?.Invoke();
            Debug.Log("[InputSyncerServer] Match started");
        }

        public void FinishMatch()
        {
            if (!State.MatchStarted || State.MatchFinished)
                return;

            State.MatchFinished = true;
            PlayerLoopHook.Unregister(TickStep);

            SendJsonToAllJoined(InputSyncerEvents.INPUT_SYNCER_FINISH_EVENT, "{}");
            OnMatchFinished?.Invoke();
            Debug.Log("[InputSyncerServer] Match finished");
        }

        public void Stop()
        {
            if (State.MatchStarted && !State.MatchFinished)
            {
                PlayerLoopHook.Unregister(TickStep);
            }

            Socket.Stop();
            Debug.Log("[InputSyncerServer] Server stopped");
        }

        public void Dispose()
        {
            if (Disposed)
                return;

            Disposed = true;

            if (State.MatchStarted && !State.MatchFinished)
            {
                PlayerLoopHook.Unregister(TickStep);
            }

            Socket.Dispose();
        }

        // -------------------------
        // STEP TICK
        // -------------------------

        private void TickStep()
        {
            if (!State.MatchStarted || State.MatchFinished)
                return;

            State.StepAccumulator += Time.deltaTime;

            while (State.StepAccumulator >= Options.StepIntervalSeconds)
            {
                State.StepAccumulator -= Options.StepIntervalSeconds;

                var inputs = new List<object>();
                int index = 0;
                foreach (var pendingInput in State.PendingInputs)
                {
                    pendingInput["index"] = index++;
                    inputs.Add(pendingInput);
                }
                State.PendingInputs.Clear();

                var stepInputs = new StepInputs
                {
                    step = State.CurrentStep,
                    inputs = inputs
                };

                State.StepHistory[State.CurrentStep] = stepInputs;

                var stepsArray = new List<StepInputs> { stepInputs };
                string json = JsonConvert.SerializeObject(stepsArray);
                SendJsonToAllJoined(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, json);

                OnStepBroadcast?.Invoke(State.CurrentStep, stepInputs);
                State.CurrentStep++;
            }
        }

        // -------------------------
        // PROTOCOL HANDLERS
        // -------------------------

        private void RegisterSocketEvents()
        {
            Socket.OnClientConnected += OnClientConnected;
            Socket.OnClientDisconnected += OnClientDisconnected;
        }

        private void RegisterProtocolHandlers()
        {
            // "join" event
            Socket.On(InputSyncerEvents.MATCH_USER_JOIN_EVENT, (connection, data) =>
            {
                if (!State.Players.ContainsKey(connection))
                    return;

                var player = State.Players[connection];

                if (player.Joined)
                    return;

                if (State.MatchStarted && !Options.AllowLateJoin)
                {
                    Debug.LogWarning($"[InputSyncerServer] Player tried to join after match started (AllowLateJoin=false)");
                    return;
                }

                string userId = data is JObject obj ? obj.Value<string>("userId") : null;
                player.UserId = userId ?? $"player-{connection.GetHashCode()}";
                player.Joined = true;

                OnPlayerJoined?.Invoke(player);
                Debug.Log($"[InputSyncerServer] Player joined: {player.UserId}");

                // Send step history to late joiner
                if (State.MatchStarted && Options.AllowLateJoin && Options.SendStepHistoryOnLateJoin)
                {
                    SendAllStepsToPlayer(connection);
                }

                // Auto-start if full
                if (Options.AutoStartWhenFull && !State.MatchStarted && GetJoinedPlayerCount() >= Options.MaxPlayers)
                {
                    StartMatch();
                }
            });

            // "input" event
            Socket.On(InputSyncerEvents.MATCH_USER_INPUT_EVENT, (connection, data) =>
            {
                if (!State.Players.ContainsKey(connection))
                    return;

                var player = State.Players[connection];

                if (!player.Joined || !State.MatchStarted || State.MatchFinished)
                    return;

                JObject inputPayload;
                if (data is JObject jObj)
                    inputPayload = jObj;
                else
                    return;

                // Extract the inputData field and set server-authoritative userId
                var inputData = inputPayload["inputData"] as JObject;
                if (inputData != null)
                {
                    inputData["userId"] = player.UserId;
                    State.PendingInputs.Add(inputData);
                }
                else
                {
                    // If no inputData wrapper, use the whole payload
                    inputPayload["userId"] = player.UserId;
                    State.PendingInputs.Add(inputPayload);
                }
            });

            // "user-finish" event
            Socket.On(InputSyncerEvents.MATCH_USER_FINISH_EVENT, (connection, data) =>
            {
                if (!State.Players.ContainsKey(connection))
                    return;

                var player = State.Players[connection];

                if (!player.Joined || player.Finished)
                    return;

                player.Finished = true;

                string finishJson = JsonConvert.SerializeObject(new { userId = player.UserId });
                SendJsonToAllJoined(InputSyncerEvents.INPUT_SYNCER_USER_FINISH_EVENT, finishJson);

                OnPlayerFinished?.Invoke(player);
                Debug.Log($"[InputSyncerServer] Player finished: {player.UserId}");

                // Auto-finish if all joined players are finished
                if (State.MatchStarted && !State.MatchFinished)
                {
                    bool allFinished = State.Players.Values
                        .Where(p => p.Joined)
                        .All(p => p.Finished);

                    if (allFinished)
                    {
                        FinishMatch();
                    }
                }
            });

            // "request-all-steps" event
            Socket.On(InputSyncerEvents.MATCH_USER_REQUEST_ALL_STEPS_EVENT, (connection, data) =>
            {
                if (!State.Players.ContainsKey(connection))
                    return;

                SendAllStepsToPlayer(connection);
            });
        }

        private void SendAllStepsToPlayer(NetworkConnection connection)
        {
            var allSteps = new AllStepInputs
            {
                requestedUser = State.Players.ContainsKey(connection) ? State.Players[connection].UserId : "",
                steps = State.StepHistory.Values.OrderBy(s => s.step).ToList(),
                lastSentStep = State.CurrentStep > 0 ? State.CurrentStep - 1 : 0
            };

            string json = JsonConvert.SerializeObject(allSteps);
            Socket.SendJson(connection, InputSyncerEvents.INPUT_SYNCER_ALL_STEPS_EVENT, json);
        }

        private void OnClientConnected(NetworkConnection connection)
        {
            var player = new InputSyncerServerPlayer
            {
                Connection = connection,
                Joined = false,
                Finished = false
            };

            State.Players[connection] = player;
            OnPlayerConnected?.Invoke(player);
            Debug.Log($"[InputSyncerServer] Client connected: {connection}");
        }

        private void OnClientDisconnected(NetworkConnection connection)
        {
            if (!State.Players.ContainsKey(connection))
                return;

            var player = State.Players[connection];
            State.Players.Remove(connection);

            OnPlayerDisconnected?.Invoke(player);
            Debug.Log($"[InputSyncerServer] Client disconnected: {player.UserId ?? connection.ToString()}");
        }

        // -------------------------
        // PUBLIC API - QUERIES
        // -------------------------

        public int GetPlayerCount()
        {
            return State.Players.Count;
        }

        public int GetJoinedPlayerCount()
        {
            return State.Players.Values.Count(p => p.Joined);
        }

        public IEnumerable<InputSyncerServerPlayer> GetPlayers()
        {
            return State.Players.Values;
        }

        public InputSyncerServerState GetState()
        {
            return State;
        }

        public bool IsMatchStarted => State.MatchStarted;
        public bool IsMatchFinished => State.MatchFinished;

        // -------------------------
        // PUBLIC API - CUSTOM EVENTS
        // -------------------------

        public void SendJsonToAll(string eventName, string json)
        {
            SendJsonToAllJoined(eventName, json);
        }

        public void SendJsonToPlayer(string userId, string eventName, string json)
        {
            var player = State.Players.Values.FirstOrDefault(p => p.UserId == userId);
            if (player != null)
            {
                Socket.SendJson(player.Connection, eventName, json);
            }
        }

        public void On(string eventName, Action<NetworkConnection, JToken> callback)
        {
            Socket.On(eventName, callback);
        }

        // -------------------------
        // HELPERS
        // -------------------------

        private void SendJsonToAllJoined(string eventName, string json)
        {
            foreach (var kvp in State.Players)
            {
                if (kvp.Value.Joined)
                {
                    Socket.SendJson(kvp.Key, eventName, json);
                }
            }
        }
    }
}

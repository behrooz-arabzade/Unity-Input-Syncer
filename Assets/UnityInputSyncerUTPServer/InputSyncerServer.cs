using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityInputSyncerClient;
using UnityInputSyncerCore;
using UnityInputSyncerCore.UTPSocket;
using UnityInputSyncerCore.Utils;

namespace UnityInputSyncerUTPServer
{
    public class InputSyncerServer : IDisposable
    {
        public event Action<InputSyncerServerPlayer> OnPlayerConnected;
        public event Action<InputSyncerServerPlayer> OnPlayerDisconnected;
        public event Action<InputSyncerServerPlayer> OnPlayerJoined;
        public event Action<InputSyncerServerPlayer> OnPlayerFinished;
        public event Action<InputSyncerServerPlayer> OnPlayerSessionFinished;
        public event Action OnMatchStarted;
        public event Action OnMatchFinished;
        public event Action<string> OnMatchFinishedWithReason;
        public event Action<int, StepInputs> OnStepBroadcast;

        private ISocketServer Socket;
        private InputSyncerServerOptions Options;
        private InputSyncerServerState State;
        private bool Disposed;

        private DateTime? AbandonDeadlineUtc;

        /// <summary>Optional <c>userId</c> from UTP handshake JSON, consumed on <see cref="OnClientConnected"/>.</summary>
        private readonly Dictionary<int, string> pendingHandshakeUserIds = new Dictionary<int, string>();

        private Dictionary<string, List<Action<int, JToken>>> customJsonCallbacks =
            new Dictionary<string, List<Action<int, JToken>>>();

        public InputSyncerServer(InputSyncerServerOptions options = null)
            : this(null, options)
        {
        }

        public InputSyncerServer(ISocketServer socket, InputSyncerServerOptions options = null)
        {
            Options = options ?? new InputSyncerServerOptions();
            State = new InputSyncerServerState();

            if (socket != null)
            {
                Socket = socket;
            }
            else
            {
                var utpOptions = new UTPSocketServerOptions
                {
                    Port = Options.Port,
                    HeartbeatTimeout = Options.HeartbeatTimeout,
                    OnHandshakeValidation = (connectionId, data) =>
                    {
                        if (!MatchAccessHandshake.Validate(Options, data))
                            return false;
                        if (MatchAccessHandshake.TryGetOptionalUserId(data, out var uid))
                            pendingHandshakeUserIds[connectionId] = uid;
                        else
                            pendingHandshakeUserIds.Remove(connectionId);
                        return true;
                    },
                };
                Socket = new UTPSocketServer(utpOptions);
            }

            RegisterSocketEvents();
            RegisterProtocolHandlers();
        }

        public string LastFinishReason { get; private set; }

        public InputSyncerServerOptions GetOptions() => Options;

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
            UpdateAbandonDeadline();
            Debug.Log("[InputSyncerServer] Match started");
        }

        /// <summary>Ends the match with reason <see cref="InputSyncerFinishReasons.Completed"/>.</summary>
        public void FinishMatch()
        {
            FinishMatch(InputSyncerFinishReasons.Completed);
        }

        public void FinishMatch(string reason)
        {
            if (!State.MatchStarted || State.MatchFinished)
                return;

            var joinedUserIds = State.Players.Values.Where(p => p.Joined).Select(p => p.UserId).ToList();

            State.MatchFinished = true;
            AbandonDeadlineUtc = null;
            PlayerLoopHook.Unregister(TickStep);

            LastFinishReason = reason ?? InputSyncerFinishReasons.Completed;
            string finishPayload = JsonConvert.SerializeObject(new { reason = LastFinishReason });
            SendJsonToAllJoined(InputSyncerEvents.INPUT_SYNCER_FINISH_EVENT, finishPayload);

            OnMatchFinished?.Invoke();
            OnMatchFinishedWithReason?.Invoke(LastFinishReason);

            if (Options.RewardOutcomeDelivery == RewardOutcomeDeliveryMode.ServerHookMatchOrReferee)
            {
                try
                {
                    Options.OnRewardHookMatch?.Invoke(new RewardMatchHookContext
                    {
                        MatchInstanceId = Options.MatchInstanceId ?? "",
                        Reason = LastFinishReason,
                        JoinedUserIds = joinedUserIds,
                    });
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[InputSyncerServer] OnRewardHookMatch failed: {e.Message}");
                }
            }

            Debug.Log($"[InputSyncerServer] Match finished ({LastFinishReason})");
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

        /// <summary>Emit Socket.IO–compatible <c>content-error</c> before tearing down a pooled instance.</summary>
        public void NotifyInstanceDestroyedBeforeShutdown()
        {
            const string payload =
                "{\"reason\":\"instance-destroyed\",\"message\":\"This match instance was closed by the server\"}";
            foreach (var connectionId in State.Players.Keys.ToList())
                Socket.SendJson(connectionId, InputSyncerEvents.INPUT_SYNCER_CONTENT_ERROR, payload);
        }

        internal static string DefaultAnonymousUserId(int connectionId)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(connectionId.ToString(CultureInfo.InvariantCulture)));
                var sb = new StringBuilder("player-");
                for (int i = 0; i < 4; i++)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0:x2}", hash[i]);
                return sb.ToString();
            }
        }

        private void TickStep()
        {
            CheckAbandonDeadlineExpired();

            if (!State.MatchStarted || State.MatchFinished)
                return;

            State.StepAccumulator += Time.deltaTime;

            // One step per tick max — matches Socket.IO setInterval semantics (no burst catch-up).
            if (State.StepAccumulator >= Options.StepIntervalSeconds)
            {
                State.StepAccumulator -= Options.StepIntervalSeconds;
                ProcessStep();
            }
        }

        private void CheckAbandonDeadlineExpired()
        {
            if (!State.MatchStarted || State.MatchFinished || !AbandonDeadlineUtc.HasValue)
                return;

            if (DateTime.UtcNow >= AbandonDeadlineUtc.Value)
            {
                AbandonDeadlineUtc = null;
                FinishMatch(InputSyncerFinishReasons.AbandonTimeout);
            }
        }

        private void UpdateAbandonDeadline()
        {
            if (Options.AbandonMatchTimeoutSeconds <= 0f || !State.MatchStarted || State.MatchFinished)
            {
                AbandonDeadlineUtc = null;
                return;
            }

            if (!Options.AllowLateJoin)
            {
                AbandonDeadlineUtc = null;
                return;
            }

            int joined = GetJoinedPlayerCount();
            if (joined > 0 && joined < Options.MaxPlayers)
                AbandonDeadlineUtc = DateTime.UtcNow.AddSeconds(Options.AbandonMatchTimeoutSeconds);
            else
                AbandonDeadlineUtc = null;
        }

        private void CheckMatchAbandonAfterPlayerRemoved()
        {
            if (!State.MatchStarted || State.MatchFinished)
                return;

            int joined = GetJoinedPlayerCount();
            if (joined == 0)
            {
                FinishMatch(InputSyncerFinishReasons.AllDisconnected);
                return;
            }

            if (!Options.AllowLateJoin && joined < Options.MaxPlayers)
            {
                FinishMatch(InputSyncerFinishReasons.InsufficientPlayers);
                return;
            }

            UpdateAbandonDeadline();
        }

        internal void ProcessStep()
        {
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

        private void RegisterSocketEvents()
        {
            Socket.OnClientConnected += OnClientConnected;
            Socket.OnClientDisconnected += OnClientDisconnected;
        }

        private void RegisterProtocolHandlers()
        {
            Socket.On(InputSyncerEvents.MATCH_USER_JOIN_EVENT, (connectionId, data) =>
            {
                HandleJoinPayload(connectionId, data as JObject);
            });

            Socket.On(InputSyncerEvents.MATCH_USER_INPUT_EVENT, (connectionId, data) =>
            {
                if (!State.Players.ContainsKey(connectionId))
                    return;

                var player = State.Players[connectionId];

                if (!player.Joined || !State.MatchStarted || State.MatchFinished)
                    return;

                if (Options.RejectInputAfterSessionFinish && player.SessionFinished)
                    return;

                JObject inputPayload;
                if (data is JObject jObj)
                    inputPayload = jObj;
                else
                    return;

                var inputData = inputPayload["inputData"] as JObject;
                if (inputData != null)
                {
                    inputData["userId"] = player.UserId;
                    State.PendingInputs.Add(inputData);
                }
                else
                {
                    inputPayload["userId"] = player.UserId;
                    State.PendingInputs.Add(inputPayload);
                }
            });

            Socket.On(InputSyncerEvents.MATCH_USER_FINISH_EVENT, (connectionId, data) =>
            {
                if (!State.Players.ContainsKey(connectionId))
                    return;

                var player = State.Players[connectionId];

                if (!player.Joined || player.Finished)
                    return;

                player.Finished = true;

                string finishJson = JsonConvert.SerializeObject(new { userId = player.UserId });
                SendJsonToAllJoined(InputSyncerEvents.INPUT_SYNCER_USER_FINISH_EVENT, finishJson);

                OnPlayerFinished?.Invoke(player);
                Debug.Log($"[InputSyncerServer] Player finished: {player.UserId}");

                if (Options.QuorumUserFinishEndsMatch && State.MatchStarted && !State.MatchFinished)
                {
                    bool allFinished = State.Players.Values
                        .Where(p => p.Joined)
                        .All(p => p.Finished);

                    if (allFinished)
                    {
                        FinishMatch(InputSyncerFinishReasons.Completed);
                    }
                }
            });

            Socket.On(InputSyncerEvents.MATCH_PLAYER_SESSION_FINISH_EVENT, (connectionId, data) =>
            {
                HandlePlayerSessionFinish(connectionId, data);
            });

            Socket.On(InputSyncerEvents.MATCH_USER_REQUEST_ALL_STEPS_EVENT, (connectionId, data) =>
            {
                if (!State.Players.ContainsKey(connectionId))
                    return;

                SendAllStepsToPlayer(connectionId);
            });
        }

        private void HandlePlayerSessionFinish(int connectionId, JToken data)
        {
            if (!State.Players.ContainsKey(connectionId))
                return;

            var player = State.Players[connectionId];
            if (!player.Joined || player.SessionFinished)
                return;

            JToken payloadData = ExtractSessionFinishData(data);
            if (payloadData == null || payloadData.Type == JTokenType.Null ||
                payloadData.Type == JTokenType.Undefined)
                payloadData = new JObject();

            string serialized = payloadData.ToString(Formatting.None);
            int byteCount = Encoding.UTF8.GetByteCount(serialized);
            if (byteCount > Options.SessionFinishMaxPayloadBytes)
            {
                Debug.LogWarning($"[InputSyncerServer] player-session-finish payload too large ({byteCount} bytes)");
                return;
            }

            player.SessionFinished = true;

            var outbound = new JObject
            {
                ["userId"] = player.UserId,
                ["data"] = payloadData,
            };
            string outboundJson = outbound.ToString(Formatting.None);

            if (Options.SessionFinishBroadcast)
                SendJsonToAllJoined(InputSyncerEvents.INPUT_SYNCER_PLAYER_SESSION_FINISH_EVENT, outboundJson);
            else
                Socket.SendJson(connectionId, InputSyncerEvents.INPUT_SYNCER_PLAYER_SESSION_FINISH_EVENT, outboundJson);

            OnPlayerSessionFinished?.Invoke(player);

            if (Options.RewardOutcomeDelivery == RewardOutcomeDeliveryMode.ServerHookPerUser)
            {
                try
                {
                    Options.OnRewardHookPerUser?.Invoke(new RewardPerUserHookContext
                    {
                        MatchInstanceId = Options.MatchInstanceId ?? "",
                        UserId = player.UserId,
                        Data = payloadData.DeepClone(),
                    });
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[InputSyncerServer] OnRewardHookPerUser failed: {e.Message}");
                }
            }
        }

        private static JToken ExtractSessionFinishData(JToken data)
        {
            if (data == null || data.Type == JTokenType.Null)
                return new JObject();

            if (data is not JObject obj)
                return new JObject();

            var inner = obj["data"];
            if (inner != null && inner.Type == JTokenType.Object)
                return inner;

            return obj;
        }

        void HandleJoinPayload(int connectionId, JObject body)
        {
            if (!State.Players.TryGetValue(connectionId, out var player))
                return;

            string requestedUserId = body?.Value<string>("userId");

            if (player.Joined)
            {
                if (!State.MatchStarted && !string.IsNullOrEmpty(requestedUserId))
                    player.UserId = requestedUserId.Trim();
                return;
            }

            TryCompleteJoin(connectionId, player, requestedUserId);
        }

        void TryCompleteJoin(int connectionId, InputSyncerServerPlayer player, string requestedUserId)
        {
            if (State.MatchStarted && !Options.AllowLateJoin)
            {
                Debug.LogWarning($"[InputSyncerServer] Player tried to join after match started (AllowLateJoin=false)");
                return;
            }

            if (GetJoinedPlayerCount() >= Options.MaxPlayers)
            {
                Debug.LogWarning($"[InputSyncerServer] Player tried to join but match is full ({GetJoinedPlayerCount()}/{Options.MaxPlayers})");
                string errorJson = JsonConvert.SerializeObject(new { reason = "match-full", message = $"Match is full ({Options.MaxPlayers} players max)" });
                Socket.SendJson(connectionId, InputSyncerEvents.INPUT_SYNCER_CONTENT_ERROR, errorJson);
                return;
            }

            string userId = string.IsNullOrWhiteSpace(requestedUserId)
                ? DefaultAnonymousUserId(connectionId)
                : requestedUserId.Trim();
            player.UserId = userId;
            player.Joined = true;

            OnPlayerJoined?.Invoke(player);
            Debug.Log($"[InputSyncerServer] Player joined: {player.UserId}");

            if (State.MatchStarted && Options.AllowLateJoin && Options.SendStepHistoryOnLateJoin)
                SendAllStepsToPlayer(connectionId);

            if (Options.AutoStartWhenFull && !State.MatchStarted && GetJoinedPlayerCount() >= Options.MaxPlayers)
                StartMatch();
            else
                UpdateAbandonDeadline();
        }

        private void SendAllStepsToPlayer(int connectionId)
        {
            var allSteps = new AllStepInputs
            {
                requestedUser = State.Players.ContainsKey(connectionId) ? State.Players[connectionId].UserId : "",
                steps = State.StepHistory.Values.OrderBy(s => s.step).ToList(),
                lastSentStep = State.CurrentStep > 0 ? State.CurrentStep - 1 : 0
            };

            string json = JsonConvert.SerializeObject(allSteps);
            Socket.SendJson(connectionId, InputSyncerEvents.INPUT_SYNCER_ALL_STEPS_EVENT, json);
        }

        private void OnClientConnected(int connectionId)
        {
            var player = new InputSyncerServerPlayer
            {
                ConnectionId = connectionId,
                Joined = false,
                Finished = false,
                SessionFinished = false,
            };

            State.Players[connectionId] = player;
            OnPlayerConnected?.Invoke(player);
            Debug.Log($"[InputSyncerServer] Client connected: {connectionId}");

            if (!Options.AutoJoinOnConnect)
                return;

            string handshakeUserId = null;
            if (pendingHandshakeUserIds.TryGetValue(connectionId, out var u))
            {
                handshakeUserId = u;
                pendingHandshakeUserIds.Remove(connectionId);
            }

            TryCompleteJoin(connectionId, player, handshakeUserId);
        }

        private void OnClientDisconnected(int connectionId)
        {
            if (!State.Players.ContainsKey(connectionId))
                return;

            var player = State.Players[connectionId];
            State.Players.Remove(connectionId);

            OnPlayerDisconnected?.Invoke(player);
            Debug.Log($"[InputSyncerServer] Client disconnected: {player.UserId ?? connectionId.ToString()}");

            CheckMatchAbandonAfterPlayerRemoved();
        }

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

        public void SendJsonToAll(string eventName, string json)
        {
            SendJsonToAllJoined(eventName, json);
        }

        public void SendJsonToPlayer(string userId, string eventName, string json)
        {
            var player = State.Players.Values.FirstOrDefault(p => p.UserId == userId);
            if (player != null)
            {
                Socket.SendJson(player.ConnectionId, eventName, json);
            }
        }

        public void On(string eventName, Action<int, JToken> callback)
        {
            Socket.On(eventName, callback);
        }

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

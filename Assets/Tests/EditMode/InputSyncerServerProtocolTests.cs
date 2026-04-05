using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tests.Helpers;
using UnityInputSyncerClient;
using UnityInputSyncerCore;
using UnityInputSyncerUTPServer;

namespace Tests.EditMode
{
    public class InputSyncerServerProtocolTests
    {
        private FakeSocketServer fakeSocket;
        private InputSyncerServer server;

        [SetUp]
        public void SetUp()
        {
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                MaxPlayers = 4,
                AutoStartWhenFull = false,
                AllowLateJoin = false,
                SendStepHistoryOnLateJoin = true,
                StepIntervalSeconds = 0.1f,
            });
        }

        [TearDown]
        public void TearDown()
        {
            server?.Dispose();
            server = null;
            fakeSocket = null;
        }

        // =========================================================
        // Helpers
        // =========================================================

        private int ConnectAndJoin(string userId = null)
        {
            int id = fakeSocket.SimulateClientConnect();
            var joinData = userId != null
                ? JObject.FromObject(new { userId })
                : new JObject();
            fakeSocket.SimulateJsonEvent(id, InputSyncerEvents.MATCH_USER_JOIN_EVENT, joinData);
            return id;
        }

        private void SendInput(int connectionId, JObject inputData)
        {
            var payload = new JObject { ["inputData"] = inputData };
            fakeSocket.SimulateJsonEvent(connectionId, InputSyncerEvents.MATCH_USER_INPUT_EVENT, payload);
        }

        private List<SentMessage> StepMessages()
        {
            return fakeSocket.GetMessagesByEvent(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT);
        }

        // =========================================================
        // Connection Management
        // =========================================================

        [Test]
        public void PlayerConnected_CreatesPlayerEntry()
        {
            int id = fakeSocket.SimulateClientConnect();
            Assert.AreEqual(1, server.GetPlayerCount());
            var player = server.GetPlayers().First();
            Assert.AreEqual(id, player.ConnectionId);
            Assert.IsFalse(player.Joined);
        }

        [Test]
        public void PlayerDisconnected_RemovesPlayer_FiresEvent()
        {
            int id = fakeSocket.SimulateClientConnect();
            InputSyncerServerPlayer disconnectedPlayer = null;
            server.OnPlayerDisconnected += p => disconnectedPlayer = p;

            fakeSocket.SimulateClientDisconnect(id);

            Assert.AreEqual(0, server.GetPlayerCount());
            Assert.IsNotNull(disconnectedPlayer);
            Assert.AreEqual(id, disconnectedPlayer.ConnectionId);
        }

        [Test]
        public void PlayerDisconnected_UnknownConnection_DoesNothing()
        {
            bool eventFired = false;
            server.OnPlayerDisconnected += _ => eventFired = true;

            fakeSocket.SimulateClientDisconnect(999);

            Assert.IsFalse(eventFired);
            Assert.AreEqual(0, server.GetPlayerCount());
        }

        // =========================================================
        // Join Protocol
        // =========================================================

        [Test]
        public void Join_MarksPlayerJoined_SetsUserId()
        {
            int id = ConnectAndJoin("alice");

            var player = server.GetPlayers().First();
            Assert.IsTrue(player.Joined);
            Assert.AreEqual("alice", player.UserId);
        }

        [Test]
        public void Join_DefaultsUserId_WhenNotProvided()
        {
            int id = fakeSocket.SimulateClientConnect();
            fakeSocket.SimulateJsonEvent(id, InputSyncerEvents.MATCH_USER_JOIN_EVENT, new JObject());

            var player = server.GetPlayers().First();
            Assert.IsTrue(player.Joined);
            Assert.IsTrue(player.UserId.StartsWith("player-"));
        }

        [Test]
        public void Join_AlreadyJoined_DoesNothing()
        {
            int joinCount = 0;
            server.OnPlayerJoined += _ => joinCount++;

            int id = ConnectAndJoin("alice");
            // Second join should be ignored
            fakeSocket.SimulateJsonEvent(id, InputSyncerEvents.MATCH_USER_JOIN_EVENT,
                JObject.FromObject(new { userId = "alice" }));

            Assert.AreEqual(1, joinCount);
        }

        [Test]
        public void Join_FiresOnPlayerJoined()
        {
            InputSyncerServerPlayer joinedPlayer = null;
            server.OnPlayerJoined += p => joinedPlayer = p;

            int id = ConnectAndJoin("bob");

            Assert.IsNotNull(joinedPlayer);
            Assert.AreEqual("bob", joinedPlayer.UserId);
        }

        [Test]
        public void Join_AfterMatchStarted_Rejected_WhenLateJoinDisabled()
        {
            // Options.AllowLateJoin = false (default in SetUp)
            int id1 = ConnectAndJoin("alice");
            server.StartMatch();

            int id2 = fakeSocket.SimulateClientConnect();
            fakeSocket.SimulateJsonEvent(id2, InputSyncerEvents.MATCH_USER_JOIN_EVENT,
                JObject.FromObject(new { userId = "bob" }));

            var player2 = server.GetPlayers().First(p => p.ConnectionId == id2);
            Assert.IsFalse(player2.Joined);
        }

        [Test]
        public void Join_AfterMatchStarted_Accepted_WhenLateJoinEnabled()
        {
            server?.Dispose();
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                AllowLateJoin = true,
                SendStepHistoryOnLateJoin = false,
            });

            int id1 = ConnectAndJoin("alice");
            server.StartMatch();

            int id2 = ConnectAndJoin("bob");

            var player2 = server.GetPlayers().First(p => p.ConnectionId == id2);
            Assert.IsTrue(player2.Joined);
            Assert.AreEqual("bob", player2.UserId);
        }

        [Test]
        public void Join_UnknownConnection_DoesNothing()
        {
            int joinCount = 0;
            server.OnPlayerJoined += _ => joinCount++;

            // Send join for a connection that never connected
            fakeSocket.SimulateJsonEvent(999, InputSyncerEvents.MATCH_USER_JOIN_EVENT,
                JObject.FromObject(new { userId = "ghost" }));

            Assert.AreEqual(0, joinCount);
            Assert.AreEqual(0, server.GetJoinedPlayerCount());
        }

        // =========================================================
        // Max Player Enforcement
        // =========================================================

        [Test]
        public void Join_WhenMatchFull_Rejected_SendsContentError()
        {
            server?.Dispose();
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                MaxPlayers = 2,
                AutoStartWhenFull = false,
            });

            ConnectAndJoin("alice");
            ConnectAndJoin("bob");
            Assert.AreEqual(2, server.GetJoinedPlayerCount());

            fakeSocket.ClearSentMessages();

            // Third player tries to join
            int id3 = fakeSocket.SimulateClientConnect();
            fakeSocket.SimulateJsonEvent(id3, InputSyncerEvents.MATCH_USER_JOIN_EVENT,
                JObject.FromObject(new { userId = "charlie" }));

            // Should be rejected
            Assert.AreEqual(2, server.GetJoinedPlayerCount());
            var errorMsgs = fakeSocket.GetMessagesByEvent(InputSyncerEvents.INPUT_SYNCER_CONTENT_ERROR);
            Assert.AreEqual(1, errorMsgs.Count);
            Assert.AreEqual(id3, errorMsgs[0].ConnectionId);

            var errorData = JsonConvert.DeserializeObject<JObject>(errorMsgs[0].Json);
            Assert.AreEqual("match-full", errorData["reason"].ToString());
        }

        [Test]
        public void Join_WhenMatchFull_DoesNotDisconnectClient()
        {
            server?.Dispose();
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                MaxPlayers = 2,
                AutoStartWhenFull = false,
            });

            ConnectAndJoin("alice");
            ConnectAndJoin("bob");

            int id3 = fakeSocket.SimulateClientConnect();
            fakeSocket.SimulateJsonEvent(id3, InputSyncerEvents.MATCH_USER_JOIN_EVENT,
                JObject.FromObject(new { userId = "charlie" }));

            // Client should still be in Players (connected but not joined)
            Assert.AreEqual(3, server.GetPlayerCount());
            Assert.AreEqual(2, server.GetJoinedPlayerCount());
        }

        [Test]
        public void Join_AfterPlayerDisconnect_AllowsNewJoin()
        {
            server?.Dispose();
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                MaxPlayers = 2,
                AutoStartWhenFull = false,
            });

            int id1 = ConnectAndJoin("alice");
            ConnectAndJoin("bob");
            Assert.AreEqual(2, server.GetJoinedPlayerCount());

            // Disconnect alice — frees a slot
            fakeSocket.SimulateClientDisconnect(id1);
            Assert.AreEqual(1, server.GetJoinedPlayerCount());

            // Charlie should now be able to join
            ConnectAndJoin("charlie");
            Assert.AreEqual(2, server.GetJoinedPlayerCount());
        }

        [Test]
        public void Join_MaxPlayersEnforced_WithLateJoin()
        {
            server?.Dispose();
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                MaxPlayers = 2,
                AutoStartWhenFull = false,
                AllowLateJoin = true,
            });

            ConnectAndJoin("alice");
            ConnectAndJoin("bob");
            server.StartMatch();

            // Third player tries to late-join — should still be rejected by MaxPlayers
            int id3 = fakeSocket.SimulateClientConnect();
            fakeSocket.SimulateJsonEvent(id3, InputSyncerEvents.MATCH_USER_JOIN_EVENT,
                JObject.FromObject(new { userId = "charlie" }));

            Assert.AreEqual(2, server.GetJoinedPlayerCount());
            var player3 = server.GetPlayers().First(p => p.ConnectionId == id3);
            Assert.IsFalse(player3.Joined);
        }

        // =========================================================
        // Auto-Start
        // =========================================================

        [Test]
        public void AutoStart_StartsMatch_WhenMaxPlayersJoined()
        {
            server?.Dispose();
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                MaxPlayers = 2,
                AutoStartWhenFull = true,
            });

            ConnectAndJoin("alice");
            Assert.IsFalse(server.IsMatchStarted);

            ConnectAndJoin("bob");
            Assert.IsTrue(server.IsMatchStarted);
        }

        [Test]
        public void AutoStart_DoesNotStart_WhenNotFull()
        {
            server?.Dispose();
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                MaxPlayers = 3,
                AutoStartWhenFull = true,
            });

            ConnectAndJoin("alice");
            ConnectAndJoin("bob");

            Assert.IsFalse(server.IsMatchStarted);
        }

        [Test]
        public void AutoStart_Disabled_DoesNotStart()
        {
            // AutoStartWhenFull = false (default in SetUp), MaxPlayers = 4
            ConnectAndJoin("alice");
            ConnectAndJoin("bob");
            ConnectAndJoin("charlie");
            ConnectAndJoin("dave");

            Assert.IsFalse(server.IsMatchStarted);
        }

        // =========================================================
        // Late Join
        // =========================================================

        [Test]
        public void LateJoin_SendsStepHistory_WhenEnabled()
        {
            server?.Dispose();
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                AllowLateJoin = true,
                SendStepHistoryOnLateJoin = true,
            });

            int id1 = ConnectAndJoin("alice");
            server.StartMatch();

            // Produce some steps
            server.ProcessStep();
            server.ProcessStep();

            fakeSocket.ClearSentMessages();

            // Late joiner
            int id2 = ConnectAndJoin("bob");

            var allStepsMessages = fakeSocket.GetMessagesSentTo(id2)
                .FindAll(m => m.EventName == InputSyncerEvents.INPUT_SYNCER_ALL_STEPS_EVENT);

            Assert.AreEqual(1, allStepsMessages.Count);
            var allSteps = JsonConvert.DeserializeObject<AllStepInputs>(allStepsMessages[0].Json);
            Assert.AreEqual(2, allSteps.steps.Count);
        }

        [Test]
        public void LateJoin_DoesNotSendHistory_WhenDisabled()
        {
            server?.Dispose();
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                AllowLateJoin = true,
                SendStepHistoryOnLateJoin = false,
            });

            int id1 = ConnectAndJoin("alice");
            server.StartMatch();
            server.ProcessStep();

            fakeSocket.ClearSentMessages();

            int id2 = ConnectAndJoin("bob");

            var allStepsMessages = fakeSocket.GetMessagesSentTo(id2)
                .FindAll(m => m.EventName == InputSyncerEvents.INPUT_SYNCER_ALL_STEPS_EVENT);

            Assert.AreEqual(0, allStepsMessages.Count);
        }

        // =========================================================
        // Input Handling
        // =========================================================

        [Test]
        public void Input_AddsToPendingInputs_SetsUserId()
        {
            int id = ConnectAndJoin("alice");
            server.StartMatch();

            SendInput(id, new JObject { ["action"] = "move" });

            var state = server.GetState();
            Assert.AreEqual(1, state.PendingInputs.Count);
            Assert.AreEqual("alice", state.PendingInputs[0]["userId"].ToString());
        }

        [Test]
        public void Input_ExtractsInputDataField()
        {
            int id = ConnectAndJoin("alice");
            server.StartMatch();

            var inputData = new JObject { ["action"] = "jump", ["force"] = 5 };
            SendInput(id, inputData);

            var state = server.GetState();
            Assert.AreEqual(1, state.PendingInputs.Count);
            Assert.AreEqual("jump", state.PendingInputs[0]["action"].ToString());
            Assert.AreEqual(5, state.PendingInputs[0]["force"].Value<int>());
        }

        [Test]
        public void Input_UsesWholePayload_WhenNoInputDataField()
        {
            int id = ConnectAndJoin("alice");
            server.StartMatch();

            // Send raw payload without inputData wrapper
            var rawPayload = new JObject { ["action"] = "fire" };
            fakeSocket.SimulateJsonEvent(id, InputSyncerEvents.MATCH_USER_INPUT_EVENT, rawPayload);

            var state = server.GetState();
            Assert.AreEqual(1, state.PendingInputs.Count);
            Assert.AreEqual("alice", state.PendingInputs[0]["userId"].ToString());
            Assert.AreEqual("fire", state.PendingInputs[0]["action"].ToString());
        }

        [Test]
        public void Input_IgnoredBeforeJoin()
        {
            int id = fakeSocket.SimulateClientConnect();
            server.StartMatch();

            SendInput(id, new JObject { ["action"] = "move" });

            Assert.AreEqual(0, server.GetState().PendingInputs.Count);
        }

        [Test]
        public void Input_IgnoredAfterMatchFinish()
        {
            int id = ConnectAndJoin("alice");
            server.StartMatch();
            server.FinishMatch();

            SendInput(id, new JObject { ["action"] = "move" });

            Assert.AreEqual(0, server.GetState().PendingInputs.Count);
        }

        // =========================================================
        // User Finish
        // =========================================================

        [Test]
        public void UserFinish_MarksPlayerFinished()
        {
            int id = ConnectAndJoin("alice");
            server.StartMatch();

            fakeSocket.SimulateJsonEvent(id, InputSyncerEvents.MATCH_USER_FINISH_EVENT, new JObject());

            var player = server.GetPlayers().First();
            Assert.IsTrue(player.Finished);
        }

        [Test]
        public void UserFinish_BroadcastsToAllJoined()
        {
            int id1 = ConnectAndJoin("alice");
            int id2 = ConnectAndJoin("bob");
            server.StartMatch();
            fakeSocket.ClearSentMessages();

            fakeSocket.SimulateJsonEvent(id1, InputSyncerEvents.MATCH_USER_FINISH_EVENT, new JObject());

            var finishMessages = fakeSocket.GetMessagesByEvent(InputSyncerEvents.INPUT_SYNCER_USER_FINISH_EVENT);
            Assert.AreEqual(2, finishMessages.Count);

            // Both joined players should receive the broadcast
            var recipients = finishMessages.Select(m => m.ConnectionId).ToList();
            Assert.Contains(id1, recipients);
            Assert.Contains(id2, recipients);
        }

        [Test]
        public void UserFinish_AllFinished_AutoFinishesMatch()
        {
            int id1 = ConnectAndJoin("alice");
            int id2 = ConnectAndJoin("bob");
            server.StartMatch();

            fakeSocket.SimulateJsonEvent(id1, InputSyncerEvents.MATCH_USER_FINISH_EVENT, new JObject());
            Assert.IsFalse(server.IsMatchFinished);

            fakeSocket.SimulateJsonEvent(id2, InputSyncerEvents.MATCH_USER_FINISH_EVENT, new JObject());
            Assert.IsTrue(server.IsMatchFinished);
        }

        [Test]
        public void UserFinish_IgnoredIfNotJoined()
        {
            int id = fakeSocket.SimulateClientConnect();
            server.StartMatch();

            bool finishFired = false;
            server.OnPlayerFinished += _ => finishFired = true;

            fakeSocket.SimulateJsonEvent(id, InputSyncerEvents.MATCH_USER_FINISH_EVENT, new JObject());

            Assert.IsFalse(finishFired);
        }

        [Test]
        public void UserFinish_IgnoredIfAlreadyFinished()
        {
            int id = ConnectAndJoin("alice");
            server.StartMatch();

            int finishCount = 0;
            server.OnPlayerFinished += _ => finishCount++;

            fakeSocket.SimulateJsonEvent(id, InputSyncerEvents.MATCH_USER_FINISH_EVENT, new JObject());
            fakeSocket.SimulateJsonEvent(id, InputSyncerEvents.MATCH_USER_FINISH_EVENT, new JObject());

            Assert.AreEqual(1, finishCount);
        }

        // =========================================================
        // Request All Steps
        // =========================================================

        [Test]
        public void RequestAllSteps_SendsFullHistory()
        {
            int id = ConnectAndJoin("alice");
            server.StartMatch();

            server.ProcessStep();
            server.ProcessStep();
            server.ProcessStep();

            fakeSocket.ClearSentMessages();

            fakeSocket.SimulateJsonEvent(id, InputSyncerEvents.MATCH_USER_REQUEST_ALL_STEPS_EVENT, new JObject());

            var msgs = fakeSocket.GetMessagesSentTo(id)
                .FindAll(m => m.EventName == InputSyncerEvents.INPUT_SYNCER_ALL_STEPS_EVENT);

            Assert.AreEqual(1, msgs.Count);
            var allSteps = JsonConvert.DeserializeObject<AllStepInputs>(msgs[0].Json);
            Assert.AreEqual(3, allSteps.steps.Count);
            Assert.AreEqual("alice", allSteps.requestedUser);
            Assert.AreEqual(2, allSteps.lastSentStep);
        }

        [Test]
        public void RequestAllSteps_UnknownConnection_DoesNothing()
        {
            fakeSocket.ClearSentMessages();

            fakeSocket.SimulateJsonEvent(999, InputSyncerEvents.MATCH_USER_REQUEST_ALL_STEPS_EVENT, new JObject());

            Assert.AreEqual(0, fakeSocket.SentMessages.Count);
        }

        // =========================================================
        // Step Broadcast via ProcessStep
        // =========================================================

        [Test]
        public void ProcessStep_SendsToAllJoinedPlayers()
        {
            int id1 = ConnectAndJoin("alice");
            int id2 = ConnectAndJoin("bob");
            int id3 = fakeSocket.SimulateClientConnect(); // connected but not joined
            server.StartMatch();
            fakeSocket.ClearSentMessages();

            server.ProcessStep();

            var stepMsgs = StepMessages();
            Assert.AreEqual(2, stepMsgs.Count);
            var recipients = stepMsgs.Select(m => m.ConnectionId).ToList();
            Assert.Contains(id1, recipients);
            Assert.Contains(id2, recipients);
            Assert.IsFalse(recipients.Contains(id3));
        }

        [Test]
        public void ProcessStep_IncludesPendingInputs_WithIndex()
        {
            int id = ConnectAndJoin("alice");
            server.StartMatch();

            SendInput(id, new JObject { ["action"] = "move" });
            SendInput(id, new JObject { ["action"] = "jump" });

            fakeSocket.ClearSentMessages();
            server.ProcessStep();

            var stepMsgs = StepMessages();
            Assert.AreEqual(1, stepMsgs.Count);

            var steps = JsonConvert.DeserializeObject<List<StepInputs>>(stepMsgs[0].Json);
            Assert.AreEqual(1, steps.Count);
            Assert.AreEqual(2, steps[0].inputs.Count);

            var input0 = JObject.FromObject(steps[0].inputs[0]);
            var input1 = JObject.FromObject(steps[0].inputs[1]);
            Assert.AreEqual(0, input0["index"].Value<int>());
            Assert.AreEqual(1, input1["index"].Value<int>());
            Assert.AreEqual("move", input0["action"].ToString());
            Assert.AreEqual("jump", input1["action"].ToString());
        }

        [Test]
        public void ProcessStep_ClearsPendingInputs()
        {
            int id = ConnectAndJoin("alice");
            server.StartMatch();

            SendInput(id, new JObject { ["action"] = "move" });
            server.ProcessStep();

            Assert.AreEqual(0, server.GetState().PendingInputs.Count);
        }

        [Test]
        public void ProcessStep_IncrementsStep()
        {
            ConnectAndJoin("alice");
            server.StartMatch();

            Assert.AreEqual(0, server.GetState().CurrentStep);
            server.ProcessStep();
            Assert.AreEqual(1, server.GetState().CurrentStep);
            server.ProcessStep();
            Assert.AreEqual(2, server.GetState().CurrentStep);
        }

        [Test]
        public void ProcessStep_StoresInHistory()
        {
            ConnectAndJoin("alice");
            server.StartMatch();

            server.ProcessStep();
            server.ProcessStep();

            var state = server.GetState();
            Assert.AreEqual(2, state.StepHistory.Count);
            Assert.IsTrue(state.StepHistory.ContainsKey(0));
            Assert.IsTrue(state.StepHistory.ContainsKey(1));
            Assert.AreEqual(0, state.StepHistory[0].step);
            Assert.AreEqual(1, state.StepHistory[1].step);
        }

        [Test]
        public void ProcessStep_FiresOnStepBroadcast()
        {
            ConnectAndJoin("alice");
            server.StartMatch();

            int broadcastStep = -1;
            StepInputs broadcastData = null;
            server.OnStepBroadcast += (step, data) =>
            {
                broadcastStep = step;
                broadcastData = data;
            };

            server.ProcessStep();

            Assert.AreEqual(0, broadcastStep);
            Assert.IsNotNull(broadcastData);
            Assert.AreEqual(0, broadcastData.step);
        }

        // =========================================================
        // Match Lifecycle
        // =========================================================

        [Test]
        public void StartMatch_SendsStartEvent_ToAllJoined()
        {
            int id1 = ConnectAndJoin("alice");
            int id2 = ConnectAndJoin("bob");
            fakeSocket.ClearSentMessages();

            server.StartMatch();

            var startMsgs = fakeSocket.GetMessagesByEvent(InputSyncerEvents.INPUT_SYNCER_START_EVENT);
            Assert.AreEqual(2, startMsgs.Count);
            var recipients = startMsgs.Select(m => m.ConnectionId).ToList();
            Assert.Contains(id1, recipients);
            Assert.Contains(id2, recipients);
        }

        [Test]
        public void FinishMatch_SendsFinishEvent_ToAllJoined()
        {
            int id1 = ConnectAndJoin("alice");
            int id2 = ConnectAndJoin("bob");
            server.StartMatch();
            fakeSocket.ClearSentMessages();

            server.FinishMatch();

            var finishMsgs = fakeSocket.GetMessagesByEvent(InputSyncerEvents.INPUT_SYNCER_FINISH_EVENT);
            Assert.AreEqual(2, finishMsgs.Count);
            var jo = JObject.Parse(finishMsgs[0].Json);
            Assert.AreEqual(InputSyncerFinishReasons.Completed, jo["reason"]?.ToString());
        }

        [Test]
        public void PlayerSessionFinish_BroadcastsUserIdAndData()
        {
            int id1 = ConnectAndJoin("alice");
            int id2 = ConnectAndJoin("bob");
            server.StartMatch();
            fakeSocket.ClearSentMessages();

            fakeSocket.SimulateJsonEvent(id1, InputSyncerEvents.MATCH_PLAYER_SESSION_FINISH_EVENT,
                JObject.FromObject(new { data = new { result = "win" } }));

            var msgs = fakeSocket.GetMessagesByEvent(InputSyncerEvents.INPUT_SYNCER_PLAYER_SESSION_FINISH_EVENT);
            Assert.AreEqual(2, msgs.Count);
            var body = JObject.Parse(msgs[0].Json);
            Assert.AreEqual("alice", body["userId"]?.ToString());
            Assert.AreEqual("win", body["data"]?["result"]?.ToString());
        }

        [Test]
        public void PlayerSessionFinish_DoesNotFinishMatch()
        {
            int id1 = ConnectAndJoin("alice");
            ConnectAndJoin("bob");
            server.StartMatch();
            fakeSocket.SimulateJsonEvent(id1, InputSyncerEvents.MATCH_PLAYER_SESSION_FINISH_EVENT, new JObject());
            Assert.IsFalse(server.IsMatchFinished);
        }

        [Test]
        public void QuorumUserFinishEndsMatch_False_DoesNotAutoFinish()
        {
            server.Dispose();
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket, new InputSyncerServerOptions
            {
                MaxPlayers = 4,
                AutoStartWhenFull = false,
                QuorumUserFinishEndsMatch = false,
            });
            int id1 = ConnectAndJoin("alice");
            int id2 = ConnectAndJoin("bob");
            server.StartMatch();
            fakeSocket.SimulateJsonEvent(id1, InputSyncerEvents.MATCH_USER_FINISH_EVENT, new JObject());
            fakeSocket.SimulateJsonEvent(id2, InputSyncerEvents.MATCH_USER_FINISH_EVENT, new JObject());
            Assert.IsFalse(server.IsMatchFinished);
        }

        [Test]
        public void Disconnect_LastJoinedPlayerMidMatch_FinishesAllDisconnected()
        {
            int id1 = ConnectAndJoin("alice");
            server.StartMatch();
            fakeSocket.ClearSentMessages();
            fakeSocket.SimulateClientDisconnect(id1);
            Assert.IsTrue(server.IsMatchFinished);
            var finishMsgs = fakeSocket.GetMessagesByEvent(InputSyncerEvents.INPUT_SYNCER_FINISH_EVENT);
            Assert.GreaterOrEqual(finishMsgs.Count, 1);
            var jo = JObject.Parse(finishMsgs[0].Json);
            Assert.AreEqual(InputSyncerFinishReasons.AllDisconnected, jo["reason"]?.ToString());
        }

        [Test]
        public void Disconnect_OnePlayerMidMatch_InsufficientPlayers()
        {
            int id1 = ConnectAndJoin("alice");
            int id2 = ConnectAndJoin("bob");
            server.StartMatch();
            fakeSocket.ClearSentMessages();
            fakeSocket.SimulateClientDisconnect(id1);
            Assert.IsTrue(server.IsMatchFinished);
            var finishMsgs = fakeSocket.GetMessagesByEvent(InputSyncerEvents.INPUT_SYNCER_FINISH_EVENT);
            var jo = JObject.Parse(finishMsgs[0].Json);
            Assert.AreEqual(InputSyncerFinishReasons.InsufficientPlayers, jo["reason"]?.ToString());
        }

        // =========================================================
        // Custom Events & API
        // =========================================================

        [Test]
        public void SendJsonToAll_SendsToAllJoinedPlayers()
        {
            int id1 = ConnectAndJoin("alice");
            int id2 = ConnectAndJoin("bob");
            int id3 = fakeSocket.SimulateClientConnect(); // not joined
            fakeSocket.ClearSentMessages();

            server.SendJsonToAll("custom-event", "{\"key\":\"value\"}");

            Assert.AreEqual(2, fakeSocket.SentMessages.Count);
            var recipients = fakeSocket.SentMessages.Select(m => m.ConnectionId).ToList();
            Assert.Contains(id1, recipients);
            Assert.Contains(id2, recipients);
        }

        [Test]
        public void SendJsonToPlayer_SendsToCorrectPlayer()
        {
            ConnectAndJoin("alice");
            ConnectAndJoin("bob");
            fakeSocket.ClearSentMessages();

            server.SendJsonToPlayer("bob", "personal-msg", "{\"hello\":true}");

            Assert.AreEqual(1, fakeSocket.SentMessages.Count);
            Assert.AreEqual("personal-msg", fakeSocket.SentMessages[0].EventName);
        }

        [Test]
        public void On_DelegatesToSocket()
        {
            bool called = false;
            server.On("custom-event", (connId, data) => called = true);

            int id = fakeSocket.SimulateClientConnect();
            fakeSocket.SimulateJsonEvent(id, "custom-event", new JObject());

            Assert.IsTrue(called);
        }
    }

    // =========================================================
    // Migrated InputSyncerServerStateTests (from PlayMode)
    // =========================================================

    public class InputSyncerServerStateTests
    {
        private FakeSocketServer fakeSocket;
        private InputSyncerServer server;

        [SetUp]
        public void SetUp()
        {
            fakeSocket = new FakeSocketServer();
            server = new InputSyncerServer(fakeSocket);
        }

        [TearDown]
        public void TearDown()
        {
            server?.Dispose();
            server = null;
            fakeSocket = null;
        }

        [Test]
        public void Server_Dispose_Idempotent()
        {
            server.Dispose();
            Assert.DoesNotThrow(() => server.Dispose());
            server = null;
        }

        [Test]
        public void Server_StartMatch_SetsMatchStarted()
        {
            Assert.IsFalse(server.IsMatchStarted);
            server.StartMatch();
            Assert.IsTrue(server.IsMatchStarted);
        }

        [Test]
        public void Server_StartMatch_Idempotent()
        {
            int matchStartedCount = 0;
            server.OnMatchStarted += () => matchStartedCount++;

            server.StartMatch();
            server.StartMatch();

            Assert.AreEqual(1, matchStartedCount, "OnMatchStarted should fire only once");
        }

        [Test]
        public void Server_FinishMatch_SetsMatchFinished()
        {
            server.StartMatch();
            server.FinishMatch();
            Assert.IsTrue(server.IsMatchFinished);
        }

        [Test]
        public void Server_FinishMatch_BeforeStart_DoesNothing()
        {
            server.FinishMatch();
            Assert.IsFalse(server.IsMatchFinished);
            Assert.IsFalse(server.IsMatchStarted);
        }

        [Test]
        public void Server_GetPlayerCount_InitiallyZero()
        {
            Assert.AreEqual(0, server.GetPlayerCount());
            Assert.AreEqual(0, server.GetJoinedPlayerCount());
        }

        [Test]
        public void Server_IsMatchStarted_DefaultFalse()
        {
            Assert.IsFalse(server.IsMatchStarted);
            Assert.IsFalse(server.IsMatchFinished);
        }

        [Test]
        public void Server_FinishMatch_Idempotent()
        {
            int finishCount = 0;
            server.OnMatchFinished += () => finishCount++;

            server.StartMatch();
            server.FinishMatch();
            server.FinishMatch();

            Assert.AreEqual(1, finishCount, "OnMatchFinished should fire only once");
        }

        [Test]
        public void Server_GetState_ReturnsNonNull()
        {
            Assert.IsNotNull(server.GetState());
        }

        [Test]
        public void Server_GetPlayers_InitiallyEmpty()
        {
            var players = server.GetPlayers();
            Assert.IsNotNull(players);
            Assert.AreEqual(0, System.Linq.Enumerable.Count(players));
        }
    }
}

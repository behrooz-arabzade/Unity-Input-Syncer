using NUnit.Framework;
using UnityInputSyncerUTPServer;

namespace Tests.PlayMode
{
    /// <summary>
    /// Tests for InputSyncerServer state management (no networking required).
    /// UTP integration tests are excluded from batch test runs due to Unity Transport
    /// timing issues in -batchmode -nographics. Run those manually in the Editor's Test Runner.
    /// </summary>
    public class InputSyncerServerStateTests
    {
        private InputSyncerServer server;

        [TearDown]
        public void TearDown()
        {
            server?.Dispose();
            server = null;
        }

        [Test]
        public void Server_Dispose_Idempotent()
        {
            server = new InputSyncerServer();
            server.Dispose();
            Assert.DoesNotThrow(() => server.Dispose());
            server = null;
        }

        [Test]
        public void Server_StartMatch_SetsMatchStarted()
        {
            server = new InputSyncerServer();
            Assert.IsFalse(server.IsMatchStarted);
            server.StartMatch();
            Assert.IsTrue(server.IsMatchStarted);
        }

        [Test]
        public void Server_StartMatch_Idempotent()
        {
            server = new InputSyncerServer();
            int matchStartedCount = 0;
            server.OnMatchStarted += () => matchStartedCount++;

            server.StartMatch();
            server.StartMatch();

            Assert.AreEqual(1, matchStartedCount, "OnMatchStarted should fire only once");
        }

        [Test]
        public void Server_FinishMatch_SetsMatchFinished()
        {
            server = new InputSyncerServer();
            server.StartMatch();
            server.FinishMatch();
            Assert.IsTrue(server.IsMatchFinished);
        }

        [Test]
        public void Server_FinishMatch_BeforeStart_DoesNothing()
        {
            server = new InputSyncerServer();
            server.FinishMatch();
            Assert.IsFalse(server.IsMatchFinished);
            Assert.IsFalse(server.IsMatchStarted);
        }

        [Test]
        public void Server_GetPlayerCount_InitiallyZero()
        {
            server = new InputSyncerServer();
            Assert.AreEqual(0, server.GetPlayerCount());
            Assert.AreEqual(0, server.GetJoinedPlayerCount());
        }

        [Test]
        public void Server_IsMatchStarted_DefaultFalse()
        {
            server = new InputSyncerServer();
            Assert.IsFalse(server.IsMatchStarted);
            Assert.IsFalse(server.IsMatchFinished);
        }

        [Test]
        public void Server_FinishMatch_Idempotent()
        {
            server = new InputSyncerServer();
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
            server = new InputSyncerServer();
            Assert.IsNotNull(server.GetState());
        }

        [Test]
        public void Server_GetPlayers_InitiallyEmpty()
        {
            server = new InputSyncerServer();
            var players = server.GetPlayers();
            Assert.IsNotNull(players);
            Assert.AreEqual(0, System.Linq.Enumerable.Count(players));
        }
    }
}

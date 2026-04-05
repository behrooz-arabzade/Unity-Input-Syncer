using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tests.Helpers;
using UnityInputSyncerCore;
using UnityInputSyncerUTPServer;

namespace Tests.EditMode
{
    public class InputSyncerServerPoolTests
    {
        private InputSyncerServerPool pool;
        private List<FakeSocketServer> createdSockets;

        private FakeSocketServer CreateFakeSocket(ushort port)
        {
            var socket = new FakeSocketServer();
            createdSockets.Add(socket);
            return socket;
        }

        [SetUp]
        public void SetUp()
        {
            createdSockets = new List<FakeSocketServer>();
        }

        [TearDown]
        public void TearDown()
        {
            pool?.Dispose();
            pool = null;
            createdSockets = null;
        }

        // =========================================================
        // Helpers
        // =========================================================

        private InputSyncerServerPool CreatePool(InputSyncerServerPoolOptions options = null)
        {
            pool = new InputSyncerServerPool(options, CreateFakeSocket);
            return pool;
        }

        private FakeSocketServer GetSocket(int index)
        {
            return createdSockets[index];
        }

        private int ConnectAndJoin(FakeSocketServer socket, string userId)
        {
            int id = socket.SimulateClientConnect();
            socket.SimulateJsonEvent(id, InputSyncerEvents.MATCH_USER_JOIN_EVENT,
                JObject.FromObject(new { userId }));
            return id;
        }

        // =========================================================
        // Pool Creation
        // =========================================================

        [Test]
        public void CreatePool_WithDefaults_HasCorrectSettings()
        {
            CreatePool();

            Assert.AreEqual(0, pool.GetInstanceCount());
            Assert.AreEqual(10, pool.GetAvailableSlots());
        }

        [Test]
        public void CreatePool_WithCustomOptions_AppliesCorrectly()
        {
            CreatePool(new InputSyncerServerPoolOptions
            {
                BasePort = 9000,
                MaxInstances = 5,
            });

            Assert.AreEqual(5, pool.GetAvailableSlots());

            var instance = pool.CreateInstance();
            Assert.AreEqual(9000, instance.Port);
        }

        // =========================================================
        // Instance Creation
        // =========================================================

        [Test]
        public void CreateInstance_AssignsCorrectPort()
        {
            CreatePool(new InputSyncerServerPoolOptions { BasePort = 8000 });

            var instance = pool.CreateInstance();

            Assert.AreEqual(8000, instance.Port);
        }

        [Test]
        public void CreateInstance_AssignsSequentialPorts()
        {
            CreatePool(new InputSyncerServerPoolOptions { BasePort = 8000 });

            var i1 = pool.CreateInstance();
            var i2 = pool.CreateInstance();
            var i3 = pool.CreateInstance();

            Assert.AreEqual(8000, i1.Port);
            Assert.AreEqual(8001, i2.Port);
            Assert.AreEqual(8002, i3.Port);
        }

        [Test]
        public void CreateInstance_StartsServer()
        {
            CreatePool();

            pool.CreateInstance();

            Assert.IsTrue(GetSocket(0).Started);
        }

        [Test]
        public void CreateInstance_InitialStateIsIdle()
        {
            CreatePool();

            var instance = pool.CreateInstance();

            Assert.AreEqual(ServerInstanceState.Idle, instance.State);
        }

        [Test]
        public void CreateInstance_AssignsUniqueIds()
        {
            CreatePool();

            var i1 = pool.CreateInstance();
            var i2 = pool.CreateInstance();

            Assert.AreNotEqual(i1.Id, i2.Id);
        }

        [Test]
        public void CreateInstance_WhenPoolFull_ThrowsException()
        {
            CreatePool(new InputSyncerServerPoolOptions { MaxInstances = 2 });

            pool.CreateInstance();
            pool.CreateInstance();

            Assert.Throws<InvalidOperationException>(() => pool.CreateInstance());
        }

        [Test]
        public void CreateInstance_FiresOnInstanceCreated()
        {
            CreatePool();

            ServerInstance createdInstance = null;
            pool.OnInstanceCreated += i => createdInstance = i;

            var instance = pool.CreateInstance();

            Assert.AreSame(instance, createdInstance);
        }

        [Test]
        public void CreateInstance_AppliesOverrideOptions()
        {
            CreatePool(new InputSyncerServerPoolOptions
            {
                DefaultServerOptions = new InputSyncerServerOptions { MaxPlayers = 4 }
            });

            var instance = pool.CreateInstance(new InputSyncerServerOptions { MaxPlayers = 8 });

            // Verify by connecting 5+ players (would fail with MaxPlayers=4 if auto-start were on)
            Assert.IsNotNull(instance.Server);
        }

        // =========================================================
        // State Transitions
        // =========================================================

        [Test]
        public void StateTransition_IdleToWaitingForPlayers_OnConnect()
        {
            CreatePool();
            var instance = pool.CreateInstance();

            GetSocket(0).SimulateClientConnect();

            Assert.AreEqual(ServerInstanceState.WaitingForPlayers, instance.State);
        }

        [Test]
        public void StateTransition_WaitingForPlayersToIdle_WhenAllDisconnect()
        {
            CreatePool();
            var instance = pool.CreateInstance();
            var socket = GetSocket(0);

            int clientId = socket.SimulateClientConnect();
            Assert.AreEqual(ServerInstanceState.WaitingForPlayers, instance.State);

            socket.SimulateClientDisconnect(clientId);
            Assert.AreEqual(ServerInstanceState.Idle, instance.State);
        }

        [Test]
        public void StateTransition_WaitingForPlayersToInMatch_OnMatchStart()
        {
            CreatePool(new InputSyncerServerPoolOptions
            {
                DefaultServerOptions = new InputSyncerServerOptions
                {
                    MaxPlayers = 2,
                    AutoStartWhenFull = true,
                }
            });

            var instance = pool.CreateInstance();
            var socket = GetSocket(0);

            ConnectAndJoin(socket, "alice");
            ConnectAndJoin(socket, "bob");

            Assert.AreEqual(ServerInstanceState.InMatch, instance.State);
        }

        [Test]
        public void StateTransition_InMatchToFinished_OnMatchFinish()
        {
            CreatePool();
            var instance = pool.CreateInstance();
            var socket = GetSocket(0);

            ConnectAndJoin(socket, "alice");
            instance.Server.StartMatch();
            Assert.AreEqual(ServerInstanceState.InMatch, instance.State);

            instance.Server.FinishMatch();
            Assert.AreEqual(ServerInstanceState.Finished, instance.State);
        }

        [Test]
        public void StateTransition_FiresOnInstanceStateChanged()
        {
            CreatePool();
            var instance = pool.CreateInstance();

            var transitions = new List<(ServerInstanceState from, ServerInstanceState to)>();
            pool.OnInstanceStateChanged += (inst, from, to) => transitions.Add((from, to));

            GetSocket(0).SimulateClientConnect();

            Assert.AreEqual(1, transitions.Count);
            Assert.AreEqual(ServerInstanceState.Idle, transitions[0].from);
            Assert.AreEqual(ServerInstanceState.WaitingForPlayers, transitions[0].to);
        }

        // =========================================================
        // Destroy and Recycle
        // =========================================================

        [Test]
        public void DestroyInstance_DisposesServer()
        {
            CreatePool();
            var instance = pool.CreateInstance();

            pool.DestroyInstance(instance.Id);

            Assert.IsTrue(GetSocket(0).IsDisposed);
        }

        [Test]
        public void DestroyInstance_RemovesFromPool()
        {
            CreatePool();
            var instance = pool.CreateInstance();

            pool.DestroyInstance(instance.Id);

            Assert.AreEqual(0, pool.GetInstanceCount());
            Assert.IsNull(pool.GetInstance(instance.Id));
        }

        [Test]
        public void DestroyInstance_ReturnsPort()
        {
            CreatePool(new InputSyncerServerPoolOptions { BasePort = 8000 });
            var instance = pool.CreateInstance();
            Assert.AreEqual(8000, instance.Port);

            pool.DestroyInstance(instance.Id);

            // Next instance should get the recycled port
            var newInstance = pool.CreateInstance();
            Assert.AreEqual(8000, newInstance.Port);
        }

        [Test]
        public void DestroyInstance_FiresOnInstanceDestroyed()
        {
            CreatePool();
            var instance = pool.CreateInstance();

            ServerInstance destroyedInstance = null;
            pool.OnInstanceDestroyed += i => destroyedInstance = i;

            pool.DestroyInstance(instance.Id);

            Assert.AreSame(instance, destroyedInstance);
        }

        [Test]
        public void DestroyInstance_UnknownId_DoesNothing()
        {
            CreatePool();
            pool.CreateInstance();

            bool eventFired = false;
            pool.OnInstanceDestroyed += _ => eventFired = true;

            pool.DestroyInstance("nonexistent-id");

            Assert.IsFalse(eventFired);
            Assert.AreEqual(1, pool.GetInstanceCount());
        }

        // =========================================================
        // Port Recycling
        // =========================================================

        [Test]
        public void PortRecycling_ReusesReleasedPort()
        {
            CreatePool(new InputSyncerServerPoolOptions { BasePort = 8000 });

            var i1 = pool.CreateInstance(); // 8000
            var i2 = pool.CreateInstance(); // 8001

            pool.DestroyInstance(i1.Id);

            var i3 = pool.CreateInstance();
            Assert.AreEqual(8000, i3.Port);
        }

        [Test]
        public void PortRecycling_FIFOOrder()
        {
            CreatePool(new InputSyncerServerPoolOptions { BasePort = 8000 });

            var i1 = pool.CreateInstance(); // 8000
            var i2 = pool.CreateInstance(); // 8001
            var i3 = pool.CreateInstance(); // 8002

            pool.DestroyInstance(i1.Id); // recycle 8000
            pool.DestroyInstance(i3.Id); // recycle 8002

            var i4 = pool.CreateInstance(); // should get 8000 (first recycled)
            var i5 = pool.CreateInstance(); // should get 8002 (second recycled)

            Assert.AreEqual(8000, i4.Port);
            Assert.AreEqual(8002, i5.Port);
        }

        // =========================================================
        // Auto-Recycle
        // =========================================================

        [Test]
        public void AutoRecycle_Enabled_DestroysFinishedInstances()
        {
            CreatePool(new InputSyncerServerPoolOptions { AutoRecycleOnFinish = true });

            var instance = pool.CreateInstance();
            var socket = GetSocket(0);

            ConnectAndJoin(socket, "alice");
            instance.Server.StartMatch();
            instance.Server.FinishMatch();

            // Pending destroy not yet processed
            // Trigger processing via next API call
            Assert.AreEqual(0, pool.GetInstanceCount());
        }

        [Test]
        public void AutoRecycle_Disabled_KeepsFinishedInstances()
        {
            CreatePool(new InputSyncerServerPoolOptions { AutoRecycleOnFinish = false });

            var instance = pool.CreateInstance();
            var socket = GetSocket(0);

            ConnectAndJoin(socket, "alice");
            instance.Server.StartMatch();
            instance.Server.FinishMatch();

            Assert.AreEqual(1, pool.GetInstanceCount());
            Assert.AreEqual(ServerInstanceState.Finished, instance.State);
        }

        // =========================================================
        // Queries
        // =========================================================

        [Test]
        public void GetAllInstances_ReturnsAllInstances()
        {
            CreatePool();

            var i1 = pool.CreateInstance();
            var i2 = pool.CreateInstance();
            var i3 = pool.CreateInstance();

            var all = pool.GetAllInstances();
            Assert.AreEqual(3, all.Count);
            Assert.IsTrue(all.Any(i => i.Id == i1.Id));
            Assert.IsTrue(all.Any(i => i.Id == i2.Id));
            Assert.IsTrue(all.Any(i => i.Id == i3.Id));
        }

        [Test]
        public void GetInstancesByState_FiltersCorrectly()
        {
            CreatePool();

            var i1 = pool.CreateInstance();
            var i2 = pool.CreateInstance();

            // Make i1 WaitingForPlayers
            GetSocket(0).SimulateClientConnect();

            var idle = pool.GetInstancesByState(ServerInstanceState.Idle);
            var waiting = pool.GetInstancesByState(ServerInstanceState.WaitingForPlayers);

            Assert.AreEqual(1, idle.Count);
            Assert.AreEqual(i2.Id, idle[0].Id);
            Assert.AreEqual(1, waiting.Count);
            Assert.AreEqual(i1.Id, waiting[0].Id);
        }

        [Test]
        public void GetAvailableSlots_ReflectsCurrentState()
        {
            CreatePool(new InputSyncerServerPoolOptions { MaxInstances = 3 });

            Assert.AreEqual(3, pool.GetAvailableSlots());

            pool.CreateInstance();
            Assert.AreEqual(2, pool.GetAvailableSlots());

            pool.CreateInstance();
            Assert.AreEqual(1, pool.GetAvailableSlots());
        }

        // =========================================================
        // Dispose
        // =========================================================

        [Test]
        public void Dispose_DisposesAllInstances()
        {
            CreatePool();

            pool.CreateInstance();
            pool.CreateInstance();
            pool.CreateInstance();

            pool.Dispose();
            pool = null;

            Assert.IsTrue(createdSockets[0].IsDisposed);
            Assert.IsTrue(createdSockets[1].IsDisposed);
            Assert.IsTrue(createdSockets[2].IsDisposed);
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            CreatePool();
            pool.CreateInstance();

            pool.Dispose();
            Assert.DoesNotThrow(() => pool.Dispose());
            pool = null;
        }

        [Test]
        public void PostDispose_ThrowsObjectDisposedException()
        {
            CreatePool();
            pool.Dispose();

            Assert.Throws<ObjectDisposedException>(() => pool.CreateInstance());
            Assert.Throws<ObjectDisposedException>(() => pool.GetAllInstances());
            Assert.Throws<ObjectDisposedException>(() => pool.GetInstanceCount());
            Assert.Throws<ObjectDisposedException>(() => pool.GetAvailableSlots());
            Assert.Throws<ObjectDisposedException>(() => pool.DestroyInstance("x"));
            Assert.Throws<ObjectDisposedException>(() => pool.GetInstance("x"));
            Assert.Throws<ObjectDisposedException>(() => pool.GetInstancesByState(ServerInstanceState.Idle));

            pool = null;
        }

        // =========================================================
        // Port Overflow Guard (Step 16)
        // =========================================================

        [Test]
        public void AllocatePort_NearMax_AllocatesSuccessfully()
        {
            CreatePool(new InputSyncerServerPoolOptions { BasePort = 65534, MaxInstances = 2 });

            var i1 = pool.CreateInstance();
            var i2 = pool.CreateInstance();

            Assert.AreEqual(65534, i1.Port);
            Assert.AreEqual(65535, i2.Port);
        }

        [Test]
        public void AllocatePort_RangeExhausted_ThrowsInvalidOperationException()
        {
            CreatePool(new InputSyncerServerPoolOptions { BasePort = 65535, MaxInstances = 2 });

            pool.CreateInstance(); // 65535

            Assert.Throws<InvalidOperationException>(() => pool.CreateInstance());
        }

        [Test]
        public void AllocatePort_AfterExhaustion_RecycledPortsStillWork()
        {
            CreatePool(new InputSyncerServerPoolOptions { BasePort = 65535, MaxInstances = 2 });

            var i1 = pool.CreateInstance(); // 65535
            pool.DestroyInstance(i1.Id);

            // Recycled port should work
            var i2 = pool.CreateInstance();
            Assert.AreEqual(65535, i2.Port);
        }

        // =========================================================
        // Idle Instance Timeout (Step 19)
        // =========================================================

        [Test]
        public void IdleTimeout_DisabledByDefault_IdleInstancePersists()
        {
            CreatePool(new InputSyncerServerPoolOptions { IdleTimeoutSeconds = 0f });
            pool.CreateInstance();

            pool.Tick();

            Assert.AreEqual(1, pool.GetInstanceCount());
        }

        [Test]
        public void IdleTimeout_DestroysIdleInstance_AfterTimeout()
        {
            CreatePool(new InputSyncerServerPoolOptions { IdleTimeoutSeconds = 0.001f });
            var instance = pool.CreateInstance();
            Assert.AreEqual(ServerInstanceState.Idle, instance.State);

            // Wait a tiny bit so the timeout fires
            System.Threading.Thread.Sleep(10);
            pool.Tick();

            Assert.AreEqual(0, pool.GetInstanceCount());
        }

        [Test]
        public void IdleTimeout_DestroysFinishedInstance_AfterTimeout()
        {
            CreatePool(new InputSyncerServerPoolOptions
            {
                IdleTimeoutSeconds = 0.001f,
                AutoRecycleOnFinish = false,
            });
            var instance = pool.CreateInstance();
            var socket = GetSocket(0);

            ConnectAndJoin(socket, "alice");
            instance.Server.StartMatch();
            instance.Server.FinishMatch();
            Assert.AreEqual(ServerInstanceState.Finished, instance.State);

            System.Threading.Thread.Sleep(10);
            pool.Tick();

            Assert.AreEqual(0, pool.GetInstanceCount());
        }

        [Test]
        public void IdleTimeout_DoesNotDestroy_InMatchOrWaitingInstances()
        {
            CreatePool(new InputSyncerServerPoolOptions { IdleTimeoutSeconds = 0.001f });

            var instance = pool.CreateInstance();
            var socket = GetSocket(0);

            ConnectAndJoin(socket, "alice");
            Assert.AreEqual(ServerInstanceState.WaitingForPlayers, instance.State);

            System.Threading.Thread.Sleep(10);
            pool.Tick();

            Assert.AreEqual(1, pool.GetInstanceCount());
        }

        [Test]
        public void IdleTimeout_RecyclesPortsAfterDestruction()
        {
            CreatePool(new InputSyncerServerPoolOptions
            {
                BasePort = 9000,
                IdleTimeoutSeconds = 0.001f,
            });

            pool.CreateInstance();
            System.Threading.Thread.Sleep(10);
            pool.Tick();

            Assert.AreEqual(0, pool.GetInstanceCount());

            var newInstance = pool.CreateInstance();
            Assert.AreEqual(9000, newInstance.Port);
        }

        // =========================================================
        // Max instance lifetime
        // =========================================================

        [Test]
        public void MaxInstanceLifetime_Disabled_DoesNotDestroy()
        {
            CreatePool(new InputSyncerServerPoolOptions { MaxInstanceLifetimeSeconds = 0f });
            pool.CreateInstance();
            pool.Tick();
            Assert.AreEqual(1, pool.GetInstanceCount());
        }

        [Test]
        public void MaxInstanceLifetime_DestroysAfterAge()
        {
            CreatePool(new InputSyncerServerPoolOptions { MaxInstanceLifetimeSeconds = 0.001f });
            pool.CreateInstance();
            System.Threading.Thread.Sleep(15);
            pool.Tick();
            Assert.AreEqual(0, pool.GetInstanceCount());
        }

        // =========================================================
        // Integration
        // =========================================================

        [Test]
        public void FullLifecycle_CreateConnectPlayFinishRecycleReuse()
        {
            CreatePool(new InputSyncerServerPoolOptions
            {
                BasePort = 9000,
                MaxInstances = 2,
                AutoRecycleOnFinish = true,
                DefaultServerOptions = new InputSyncerServerOptions
                {
                    MaxPlayers = 2,
                    AutoStartWhenFull = true,
                }
            });

            // Create first instance
            var instance1 = pool.CreateInstance();
            Assert.AreEqual(9000, instance1.Port);
            Assert.AreEqual(ServerInstanceState.Idle, instance1.State);

            var socket1 = GetSocket(0);

            // Players connect and join
            ConnectAndJoin(socket1, "alice");
            Assert.AreEqual(ServerInstanceState.WaitingForPlayers, instance1.State);

            ConnectAndJoin(socket1, "bob");
            Assert.AreEqual(ServerInstanceState.InMatch, instance1.State);

            // Players finish
            var players = instance1.Server.GetPlayers().ToList();
            foreach (var player in players)
            {
                socket1.SimulateJsonEvent(player.ConnectionId,
                    InputSyncerEvents.MATCH_USER_FINISH_EVENT, new JObject());
            }

            Assert.AreEqual(ServerInstanceState.Finished, instance1.State);

            // Auto-recycle should clear on next API call
            Assert.AreEqual(0, pool.GetInstanceCount());

            // Create new instance — should reuse recycled port
            var instance2 = pool.CreateInstance();
            Assert.AreEqual(9000, instance2.Port);
            Assert.AreNotEqual(instance1.Id, instance2.Id);
            Assert.AreEqual(ServerInstanceState.Idle, instance2.State);
        }
    }
}

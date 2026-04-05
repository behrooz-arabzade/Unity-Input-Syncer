using System;
using System.Collections.Generic;
using System.Linq;
using UnityInputSyncerCore;
using UnityInputSyncerCore.UTPSocket;

namespace UnityInputSyncerUTPServer
{
    public class InputSyncerServerPool : IDisposable
    {
        public delegate ISocketServer SocketServerFactory(ushort port);

        public event Action<ServerInstance> OnInstanceCreated;
        public event Action<ServerInstance, ServerInstanceState, ServerInstanceState> OnInstanceStateChanged;
        public event Action<ServerInstance> OnInstanceDestroyed;

        private readonly InputSyncerServerPoolOptions options;
        private readonly SocketServerFactory socketFactory;
        private readonly Dictionary<string, ServerInstance> instances = new Dictionary<string, ServerInstance>();
        private readonly HashSet<ushort> usedPorts = new HashSet<ushort>();
        private readonly Queue<ushort> availablePorts = new Queue<ushort>();
        private readonly List<string> pendingDestroys = new List<string>();
        private ushort nextSequentialPort;
        private bool disposed;

        public InputSyncerServerPool(InputSyncerServerPoolOptions options = null, SocketServerFactory socketFactory = null)
        {
            this.options = options ?? new InputSyncerServerPoolOptions();
            this.socketFactory = socketFactory;
            nextSequentialPort = this.options.BasePort;
        }

        public ServerInstance CreateInstance(InputSyncerServerOptions overrideOptions = null)
        {
            ThrowIfDisposed();
            ProcessPendingDestroys();

            if (instances.Count >= options.MaxInstances)
                throw new InvalidOperationException(
                    $"Cannot create instance: pool is full ({options.MaxInstances}/{options.MaxInstances}).");

            ushort port = AllocatePort();
            string id = Guid.NewGuid().ToString();

            var d = options.DefaultServerOptions;
            var o = overrideOptions;
            var serverOptions = new InputSyncerServerOptions
            {
                Port = port,
                HeartbeatTimeout = o?.HeartbeatTimeout ?? d.HeartbeatTimeout,
                MaxPlayers = o?.MaxPlayers ?? d.MaxPlayers,
                AutoStartWhenFull = o?.AutoStartWhenFull ?? d.AutoStartWhenFull,
                AutoJoinOnConnect = o?.AutoJoinOnConnect ?? d.AutoJoinOnConnect,
                StepIntervalSeconds = o?.StepIntervalSeconds ?? d.StepIntervalSeconds,
                AllowLateJoin = o?.AllowLateJoin ?? d.AllowLateJoin,
                SendStepHistoryOnLateJoin = o?.SendStepHistoryOnLateJoin ?? d.SendStepHistoryOnLateJoin,
                QuorumUserFinishEndsMatch = o?.QuorumUserFinishEndsMatch ?? d.QuorumUserFinishEndsMatch,
                SessionFinishMaxPayloadBytes = o?.SessionFinishMaxPayloadBytes ?? d.SessionFinishMaxPayloadBytes,
                SessionFinishBroadcast = o?.SessionFinishBroadcast ?? d.SessionFinishBroadcast,
                RejectInputAfterSessionFinish = o?.RejectInputAfterSessionFinish ?? d.RejectInputAfterSessionFinish,
                AbandonMatchTimeoutSeconds = o?.AbandonMatchTimeoutSeconds ?? d.AbandonMatchTimeoutSeconds,
                RewardOutcomeDelivery = o?.RewardOutcomeDelivery ?? d.RewardOutcomeDelivery,
                OnRewardHookPerUser = o?.OnRewardHookPerUser ?? d.OnRewardHookPerUser,
                OnRewardHookMatch = o?.OnRewardHookMatch ?? d.OnRewardHookMatch,
                MatchInstanceId = id,
                MatchAccess = o?.MatchAccess ?? d.MatchAccess,
                MatchPassword = o != null ? o.MatchPassword : d.MatchPassword,
                AllowedMatchTokens = MergeAllowedMatchTokens(o, d),
            };

            ISocketServer socket = socketFactory?.Invoke(port);
            var server = socket != null
                ? new InputSyncerServer(socket, serverOptions)
                : new InputSyncerServer(serverOptions);

            var instance = new ServerInstance(id, port, server);

            instance.OnStateChanged += HandleInstanceStateChanged;
            instances[id] = instance;

            server.Start();

            OnInstanceCreated?.Invoke(instance);
            return instance;
        }

        public void DestroyInstance(string instanceId)
        {
            ThrowIfDisposed();
            ProcessPendingDestroys();

            if (!instances.TryGetValue(instanceId, out var instance))
                return;

            DestroyInstanceInternal(instance);
        }

        public ServerInstance GetInstance(string instanceId)
        {
            ThrowIfDisposed();
            ProcessPendingDestroys();

            instances.TryGetValue(instanceId, out var instance);
            return instance;
        }

        public IReadOnlyList<ServerInstance> GetAllInstances()
        {
            ThrowIfDisposed();
            ProcessPendingDestroys();

            return instances.Values.ToList();
        }

        public IReadOnlyList<ServerInstance> GetInstancesByState(ServerInstanceState state)
        {
            ThrowIfDisposed();
            ProcessPendingDestroys();

            return instances.Values.Where(i => i.State == state).ToList();
        }

        public int GetInstanceCount()
        {
            ThrowIfDisposed();
            ProcessPendingDestroys();

            return instances.Count;
        }

        public int GetAvailableSlots()
        {
            ThrowIfDisposed();
            ProcessPendingDestroys();

            return options.MaxInstances - instances.Count;
        }

        public void Tick()
        {
            ThrowIfDisposed();
            ProcessMaxInstanceLifetime();
            ProcessIdleTimeouts();
            ProcessPendingDestroys();
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            foreach (var instance in instances.Values.ToList())
            {
                instance.OnStateChanged -= HandleInstanceStateChanged;
                instance.Server.Dispose();
            }

            instances.Clear();
            usedPorts.Clear();
            availablePorts.Clear();
            pendingDestroys.Clear();
        }

        private ushort AllocatePort()
        {
            if (availablePorts.Count > 0)
            {
                var port = availablePorts.Dequeue();
                usedPorts.Add(port);
                return port;
            }

            if (nextSequentialPort == 0 && usedPorts.Count > 0)
                throw new InvalidOperationException(
                    "Cannot allocate port: sequential port range exhausted. Destroy existing instances to recycle ports.");

            ushort allocated = nextSequentialPort;
            nextSequentialPort++;
            usedPorts.Add(allocated);
            return allocated;
        }

        private void ReleasePort(ushort port)
        {
            usedPorts.Remove(port);
            availablePorts.Enqueue(port);
        }

        private void HandleInstanceStateChanged(ServerInstance instance, ServerInstanceState oldState, ServerInstanceState newState)
        {
            OnInstanceStateChanged?.Invoke(instance, oldState, newState);

            if (newState == ServerInstanceState.Finished && options.AutoRecycleOnFinish)
            {
                pendingDestroys.Add(instance.Id);
            }
        }

        private void ProcessMaxInstanceLifetime()
        {
            if (options.MaxInstanceLifetimeSeconds <= 0f)
                return;

            var now = DateTime.UtcNow;
            foreach (var instance in instances.Values.ToList())
            {
                var age = (now - instance.CreatedAt).TotalSeconds;
                if (age < options.MaxInstanceLifetimeSeconds)
                    continue;

                if (instance.Server.IsMatchStarted && !instance.Server.IsMatchFinished)
                    instance.Server.FinishMatch(InputSyncerFinishReasons.MaxInstanceLifetime);

                pendingDestroys.Add(instance.Id);
            }
        }

        private void ProcessIdleTimeouts()
        {
            if (options.IdleTimeoutSeconds <= 0f)
                return;

            var now = DateTime.UtcNow;
            foreach (var instance in instances.Values)
            {
                if (instance.State != ServerInstanceState.Idle && instance.State != ServerInstanceState.Finished)
                    continue;

                var elapsed = (now - instance.LastStateChangeTime).TotalSeconds;
                if (elapsed >= options.IdleTimeoutSeconds)
                {
                    pendingDestroys.Add(instance.Id);
                }
            }
        }

        private void ProcessPendingDestroys()
        {
            if (pendingDestroys.Count == 0)
                return;

            var toDestroy = new List<string>(pendingDestroys);
            pendingDestroys.Clear();

            foreach (var id in toDestroy)
            {
                if (instances.TryGetValue(id, out var instance))
                {
                    DestroyInstanceInternal(instance);
                }
            }
        }

        private void DestroyInstanceInternal(ServerInstance instance)
        {
            instance.OnStateChanged -= HandleInstanceStateChanged;
            instances.Remove(instance.Id);
            ReleasePort(instance.Port);
            instance.Server.NotifyInstanceDestroyedBeforeShutdown();
            instance.Server.Dispose();
            OnInstanceDestroyed?.Invoke(instance);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(InputSyncerServerPool));
        }

        private static HashSet<string> MergeAllowedMatchTokens(
            InputSyncerServerOptions o,
            InputSyncerServerOptions d)
        {
            if (o != null && o.AllowedMatchTokens != null && o.AllowedMatchTokens.Count > 0)
                return new HashSet<string>(o.AllowedMatchTokens, StringComparer.Ordinal);
            if (o != null)
                return null;
            if (d.AllowedMatchTokens != null && d.AllowedMatchTokens.Count > 0)
                return new HashSet<string>(d.AllowedMatchTokens, StringComparer.Ordinal);
            return null;
        }
    }
}

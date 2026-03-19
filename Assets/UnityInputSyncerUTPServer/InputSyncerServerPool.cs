using System;
using System.Collections.Generic;
using System.Linq;
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

            var serverOptions = new InputSyncerServerOptions
            {
                Port = port,
                HeartbeatTimeout = overrideOptions?.HeartbeatTimeout ?? options.DefaultServerOptions.HeartbeatTimeout,
                MaxPlayers = overrideOptions?.MaxPlayers ?? options.DefaultServerOptions.MaxPlayers,
                AutoStartWhenFull = overrideOptions?.AutoStartWhenFull ?? options.DefaultServerOptions.AutoStartWhenFull,
                StepIntervalSeconds = overrideOptions?.StepIntervalSeconds ?? options.DefaultServerOptions.StepIntervalSeconds,
                AllowLateJoin = overrideOptions?.AllowLateJoin ?? options.DefaultServerOptions.AllowLateJoin,
                SendStepHistoryOnLateJoin = overrideOptions?.SendStepHistoryOnLateJoin ?? options.DefaultServerOptions.SendStepHistoryOnLateJoin,
            };

            ISocketServer socket = socketFactory?.Invoke(port);
            var server = socket != null
                ? new InputSyncerServer(socket, serverOptions)
                : new InputSyncerServer(serverOptions);

            string id = Guid.NewGuid().ToString();
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
            instance.Server.Dispose();
            OnInstanceDestroyed?.Invoke(instance);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(InputSyncerServerPool));
        }
    }
}

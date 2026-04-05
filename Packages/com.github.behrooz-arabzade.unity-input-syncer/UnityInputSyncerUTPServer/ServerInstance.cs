using System;

namespace UnityInputSyncerUTPServer
{
    public class ServerInstance
    {
        public string Id { get; }
        public ushort Port { get; }
        public ServerInstanceState State { get; private set; }
        public InputSyncerServer Server { get; }
        public DateTime CreatedAt { get; }
        public DateTime LastStateChangeTime { get; private set; }

        public event Action<ServerInstance, ServerInstanceState, ServerInstanceState> OnStateChanged;

        public ServerInstance(string id, ushort port, InputSyncerServer server)
        {
            Id = id;
            Port = port;
            Server = server;
            State = ServerInstanceState.Idle;
            CreatedAt = DateTime.UtcNow;
            LastStateChangeTime = DateTime.UtcNow;

            Server.OnPlayerConnected += HandlePlayerConnected;
            Server.OnPlayerDisconnected += HandlePlayerDisconnected;
            Server.OnMatchStarted += HandleMatchStarted;
            Server.OnMatchFinished += HandleMatchFinished;
        }

        private void SetState(ServerInstanceState newState)
        {
            if (State == newState)
                return;

            var oldState = State;
            State = newState;
            LastStateChangeTime = DateTime.UtcNow;
            OnStateChanged?.Invoke(this, oldState, newState);
        }

        private void HandlePlayerConnected(InputSyncerServerPlayer player)
        {
            if (State == ServerInstanceState.Idle)
            {
                SetState(ServerInstanceState.WaitingForPlayers);
            }
        }

        private void HandlePlayerDisconnected(InputSyncerServerPlayer player)
        {
            if (State == ServerInstanceState.WaitingForPlayers && Server.GetPlayerCount() == 0)
            {
                SetState(ServerInstanceState.Idle);
            }
        }

        private void HandleMatchStarted()
        {
            SetState(ServerInstanceState.InMatch);
        }

        private void HandleMatchFinished()
        {
            SetState(ServerInstanceState.Finished);
        }
    }
}

using UnityEngine;

namespace UnityInputSyncerUTPServer.Examples
{
    public class InputSyncerServerExample : MonoBehaviour
    {
        [Header("Server Configuration")]
        [SerializeField] private ushort port = 7777;
        [SerializeField] private int maxPlayers = 2;
        [SerializeField] private bool autoStartWhenFull = true;
        [SerializeField] private float stepIntervalSeconds = 0.1f;
        [SerializeField] private bool allowLateJoin = false;

        private InputSyncerServer server;

        void Start()
        {
            var options = new InputSyncerServerOptions
            {
                Port = port,
                MaxPlayers = maxPlayers,
                AutoStartWhenFull = autoStartWhenFull,
                StepIntervalSeconds = stepIntervalSeconds,
                AllowLateJoin = allowLateJoin,
            };

            server = new InputSyncerServer(options);

            server.OnPlayerConnected += player =>
            {
                Debug.Log($"Player connected: {player.ConnectionId}");
            };

            server.OnPlayerJoined += player =>
            {
                Debug.Log($"Player joined: {player.UserId}");
            };

            server.OnPlayerDisconnected += player =>
            {
                Debug.Log($"Player disconnected: {player.UserId}");
            };

            server.OnMatchStarted += () =>
            {
                Debug.Log("Match started!");
            };

            server.OnMatchFinished += () =>
            {
                Debug.Log("Match finished!");
            };

            server.OnStepBroadcast += (step, data) =>
            {
                Debug.Log($"Step {step} broadcast with {data.inputs.Count} inputs");
            };

            server.Start();
        }

        void OnDestroy()
        {
            server?.Dispose();
        }
    }
}

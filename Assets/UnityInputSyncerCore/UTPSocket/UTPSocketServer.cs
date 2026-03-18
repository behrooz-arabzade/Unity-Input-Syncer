using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;
using UnityInputSyncerCore.Utils;

namespace UnityInputSyncerCore.UTPSocket
{
    public sealed class UTPSocketServer : IDisposable
    {
        // EVENTS
        public event Action<NetworkConnection> OnClientConnected;
        public event Action<NetworkConnection> OnClientDisconnected;
        public event Action<NetworkConnection, string> OnClientError;

        NetworkDriver driver;
        NetworkPipeline reliable;
        NetworkPipeline unreliable;

        private UTPSocketServerOptions Options;
        private UTPSocketServerState State;

        private Dictionary<NetworkConnection, ClientInfo> clients = new Dictionary<NetworkConnection, ClientInfo>();
        private List<NetworkConnection> pendingRemovals = new List<NetworkConnection>();

        // -------------------------
        // CONSTRUCTOR
        // -------------------------
        public UTPSocketServer(UTPSocketServerOptions options)
        {
            Options = options;
            State = new UTPSocketServerState();

            driver = NetworkDriver.Create();

            reliable = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            unreliable = driver.CreatePipeline(typeof(UnreliableSequencedPipelineStage));
        }

        // -------------------------
        // START SERVER
        // -------------------------
        public void Start()
        {
            if (State.Running)
                return;

            var endpoint = NetworkEndpoint.AnyIpv4.WithPort(Options.Port);
            if (driver.Bind(endpoint) != 0)
            {
                Debug.LogError($"Failed to bind to port {Options.Port}");
                return;
            }

            if (driver.Listen() != 0)
            {
                Debug.LogError("Failed to listen");
                return;
            }

            State.Running = true;
            PlayerLoopHook.Register(TickInternal);
            Debug.Log($"Server started on port {Options.Port}");
        }

        // -------------------------
        // STOP SERVER
        // -------------------------
        public void Stop()
        {
            if (!State.Running)
                return;

            State.Running = false;
            PlayerLoopHook.Unregister(TickInternal);

            // Disconnect all clients
            foreach (var kvp in clients)
            {
                if (kvp.Key.IsCreated)
                    kvp.Key.Disconnect(driver);
            }

            clients.Clear();
            Debug.Log("Server stopped");
        }

        // -------------------------
        // INTERNAL TICK (MAIN THREAD)
        // -------------------------
        void TickInternal()
        {
            if (State.Disposed || !State.Running)
                return;

            driver.ScheduleUpdate().Complete();

            HandleIncomingConnections();
            HandleClientEvents();
            HandleHeartbeats(Time.deltaTime);
            CleanupDisconnectedClients();
        }

        void HandleIncomingConnections()
        {
            NetworkConnection connection;
            while ((connection = driver.Accept()) != default)
            {
                clients[connection] = new ClientInfo
                {
                    Connection = connection,
                    HandshakeComplete = false,
                    HeartbeatTimer = Options.HeartbeatTimeout
                };
                Debug.Log($"Client connected: {connection}");
            }
        }

        void HandleClientEvents()
        {
            foreach (var kvp in clients)
            {
                var connection = kvp.Key;
                var clientInfo = kvp.Value;

                if (!connection.IsCreated)
                    continue;

                NetworkEvent.Type evt;
                while ((evt = driver.PopEventForConnection(connection, out var reader))
                       != NetworkEvent.Type.Empty)
                {
                    switch (evt)
                    {
                        case NetworkEvent.Type.Data:
                            HandleIncoming(connection, clientInfo, reader);
                            break;

                        case NetworkEvent.Type.Disconnect:
                            OnClientDisconnected?.Invoke(connection);
                            pendingRemovals.Add(connection);
                            Debug.Log($"Client disconnected: {connection}");
                            break;
                    }
                }
            }
        }

        void HandleHeartbeats(float delta)
        {
            foreach (var kvp in clients)
            {
                var connection = kvp.Key;
                var clientInfo = kvp.Value;

                if (!clientInfo.HandshakeComplete)
                    continue;

                clientInfo.HeartbeatTimer -= delta;
                if (clientInfo.HeartbeatTimer <= 0f)
                {
                    OnClientError?.Invoke(connection, "Heartbeat timeout");
                    if (connection.IsCreated)
                        connection.Disconnect(driver);
                    pendingRemovals.Add(connection);
                }
            }
        }

        void CleanupDisconnectedClients()
        {
            foreach (var connection in pendingRemovals)
            {
                clients.Remove(connection);
            }
            pendingRemovals.Clear();
        }

        void HandleIncoming(NetworkConnection connection, ClientInfo clientInfo, DataStreamReader reader)
        {
            UTPSocketDataType type = (UTPSocketDataType)reader.ReadByte();

            switch (type)
            {
                case UTPSocketDataType.Handshake:
                    int payloadLength = reader.ReadInt();
                    NativeArray<byte> handshakeData = new(payloadLength, Allocator.Temp);
                    reader.ReadBytes(handshakeData);
                    OnHandshake(connection, clientInfo, handshakeData);
                    handshakeData.Dispose();
                    break;

                case UTPSocketDataType.Json:
                    int eventNameLength = reader.ReadInt();
                    var eventNameBytes = new NativeArray<byte>(eventNameLength, Allocator.Temp);
                    reader.ReadBytes(eventNameBytes);

                    int jsonLength = reader.ReadInt();
                    NativeArray<byte> jsonData = new(jsonLength, Allocator.Temp);
                    reader.ReadBytes(jsonData);

                    byte[] eventNameBuffer = ArrayPool<byte>.Shared.Rent(eventNameLength);
                    byte[] jsonBuffer = ArrayPool<byte>.Shared.Rent(jsonLength);
                    try
                    {
                        eventNameBytes.CopyTo(eventNameBuffer);
                        jsonData.CopyTo(jsonBuffer);

                        string eventName = Encoding.UTF8.GetString(eventNameBuffer, 0, eventNameLength);
                        string json = Encoding.UTF8.GetString(jsonBuffer, 0, jsonLength);

                        OnJsonData(connection, eventName, json);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(eventNameBuffer);
                        ArrayPool<byte>.Shared.Return(jsonBuffer);
                        eventNameBytes.Dispose();
                        jsonData.Dispose();
                    }
                    break;

                case UTPSocketDataType.Binary:
                    int eventId = reader.ReadInt();
                    int dataLength = reader.ReadInt();
                    NativeArray<byte> data = new(dataLength, Allocator.Temp);
                    reader.ReadBytes(data);
                    OnBinaryData(connection, eventId, data);
                    data.Dispose();
                    break;

                case UTPSocketDataType.HeartbeatPing:
                    OnHeartbeatPing(connection, clientInfo);
                    break;
            }
        }

        private void OnHandshake(NetworkConnection connection, ClientInfo clientInfo, NativeArray<byte> data)
        {
            // Perform handshake validation here
            bool success = Options.OnHandshakeValidation?.Invoke(connection, data) ?? true;
            string errorMessage = "";

            if (!success)
            {
                errorMessage = "Handshake validation failed";
            }

            // Send handshake response
            SendHandshakeResponse(connection, success, errorMessage);

            if (success)
            {
                clientInfo.HandshakeComplete = true;
                clientInfo.HeartbeatTimer = Options.HeartbeatTimeout;
                OnClientConnected?.Invoke(connection);
            }
            else
            {
                connection.Disconnect(driver);
                pendingRemovals.Add(connection);
            }
        }

        private void OnJsonData(NetworkConnection connection, string eventName, string json)
        {
            if (jsonCallbacks.ContainsKey(eventName))
            {
                var jToken = JToken.Parse(json);
                foreach (var callback in jsonCallbacks[eventName])
                {
                    callback(connection, jToken);
                }
            }
        }

        private void OnBinaryData(NetworkConnection connection, int eventId, NativeArray<byte> data)
        {
            if (binaryCallbacks.ContainsKey(eventId))
            {
                foreach (var callback in binaryCallbacks[eventId])
                {
                    callback(connection, data);
                }
            }
        }

        private void OnHeartbeatPing(NetworkConnection connection, ClientInfo clientInfo)
        {
            // Reset heartbeat timer
            clientInfo.HeartbeatTimer = Options.HeartbeatTimeout;

            // Send pong back
            SendHeartbeatPong(connection);
        }

        // -------------------------
        // SEND
        // -------------------------
        public void SendJson(NetworkConnection connection, string eventName, string json, bool reliableSend = true)
        {
            if (!clients.ContainsKey(connection) || !clients[connection].HandshakeComplete)
                return;

            int eventNameByteCount = Encoding.UTF8.GetByteCount(eventName);
            int jsonByteCount = Encoding.UTF8.GetByteCount(json);

            byte[] eventNameBuffer = ArrayPool<byte>.Shared.Rent(eventNameByteCount);
            byte[] jsonBuffer = ArrayPool<byte>.Shared.Rent(jsonByteCount);

            try
            {
                Encoding.UTF8.GetBytes(eventName, 0, eventName.Length, eventNameBuffer, 0);
                Encoding.UTF8.GetBytes(json, 0, json.Length, jsonBuffer, 0);

                var eventNameBytes = new NativeArray<byte>(eventNameByteCount, Allocator.Temp);
                var jsonBytes = new NativeArray<byte>(jsonByteCount, Allocator.Temp);

                NativeArray<byte>.Copy(eventNameBuffer, eventNameBytes, eventNameByteCount);
                NativeArray<byte>.Copy(jsonBuffer, jsonBytes, jsonByteCount);

                SendInternal(connection, UTPSocketDataType.Json, eventNameBytes, jsonBytes, reliableSend);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(eventNameBuffer);
                ArrayPool<byte>.Shared.Return(jsonBuffer);
            }
        }

        public void SendBinary(NetworkConnection connection, int eventId, NativeArray<byte> data, bool reliableSend = false)
        {
            if (!clients.ContainsKey(connection) || !clients[connection].HandshakeComplete)
                return;

            var eventIdBytes = new NativeArray<byte>(4, Allocator.Temp);
            eventIdBytes[0] = (byte)eventId;
            eventIdBytes[1] = (byte)(eventId >> 8);
            eventIdBytes[2] = (byte)(eventId >> 16);
            eventIdBytes[3] = (byte)(eventId >> 24);

            SendInternal(connection, UTPSocketDataType.Binary, eventIdBytes, data, reliableSend);
        }

        public void SendJsonToConnection(NetworkConnection connection, string eventName, string json, bool reliableSend = true)
        {
            if (!clients.ContainsKey(connection) || !clients[connection].HandshakeComplete)
                return;

            SendJson(connection, eventName, json, reliableSend);
        }

        public void SendBinaryToConnection(NetworkConnection connection, int eventId, NativeArray<byte> data, bool reliableSend = false)
        {
            if (!clients.ContainsKey(connection) || !clients[connection].HandshakeComplete)
                return;

            SendBinary(connection, eventId, data, reliableSend);
        }

        private void SendHandshakeResponse(NetworkConnection connection, bool success, string errorMessage)
        {
            int status = driver.BeginSend(reliable, connection, out var writer);
            if (status != 0)
                return;

            writer.WriteByte((byte)UTPSocketDataType.Handshake);
            writer.WriteByte((byte)(success ? 1 : 0));

            if (!success && !string.IsNullOrEmpty(errorMessage))
            {
                int errorMessageByteCount = Encoding.UTF8.GetByteCount(errorMessage);
                byte[] errorBuffer = ArrayPool<byte>.Shared.Rent(errorMessageByteCount);
                try
                {
                    Encoding.UTF8.GetBytes(errorMessage, 0, errorMessage.Length, errorBuffer, 0);
                    writer.WriteInt(errorMessageByteCount);
                    writer.WriteBytes(errorBuffer);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(errorBuffer);
                }
            }

            driver.EndSend(writer);
        }

        private void SendHeartbeatPong(NetworkConnection connection)
        {
            SendInternal(connection, UTPSocketDataType.HeartbeatPong, default, default, true);
        }

        void SendInternal(NetworkConnection connection, UTPSocketDataType type, NativeArray<byte> eventNameOrId, NativeArray<byte> payload, bool reliableSend)
        {
            if (!connection.IsCreated)
                return;

            var pipe = reliableSend ? reliable : unreliable;

            int status = driver.BeginSend(pipe, connection, out var writer);
            if (status != 0)
                return;

            writer.WriteByte((byte)type);

            if (type == UTPSocketDataType.HeartbeatPong)
            {
                driver.EndSend(writer);
                return;
            }

            if (type == UTPSocketDataType.Json)
            {
                writer.WriteInt(eventNameOrId.Length);
                writer.WriteBytes(eventNameOrId);
            }
            else if (type == UTPSocketDataType.Binary)
            {
                int eventId = BitConverter.ToInt32(eventNameOrId);
                writer.WriteInt(eventId);
            }

            writer.WriteInt(payload.IsCreated ? payload.Length : 0);
            if (payload.IsCreated && payload.Length > 0)
                writer.WriteBytes(payload);
            driver.EndSend(writer);
        }

        // -------------------------
        // EVENT LISTENERS
        // -------------------------
        private Dictionary<string, List<Action<NetworkConnection, JToken>>> jsonCallbacks = new Dictionary<string, List<Action<NetworkConnection, JToken>>>();
        public void On(string eventName, Action<NetworkConnection, JToken> callback)
        {
            if (!jsonCallbacks.ContainsKey(eventName))
            {
                jsonCallbacks.Add(eventName, new List<Action<NetworkConnection, JToken>>());
            }

            jsonCallbacks[eventName].Add(callback);
        }

        private Dictionary<int, List<Action<NetworkConnection, NativeArray<byte>>>> binaryCallbacks = new Dictionary<int, List<Action<NetworkConnection, NativeArray<byte>>>>();
        public void On(int eventId, Action<NetworkConnection, NativeArray<byte>> callback)
        {
            if (!binaryCallbacks.ContainsKey(eventId))
            {
                binaryCallbacks.Add(eventId, new List<Action<NetworkConnection, NativeArray<byte>>>());
            }

            binaryCallbacks[eventId].Add(callback);
        }

        // -------------------------
        // CLIENT MANAGEMENT
        // -------------------------
        public int GetConnectedClientCount()
        {
            return clients.Count;
        }

        public IEnumerable<NetworkConnection> GetConnectedClients()
        {
            return clients.Keys;
        }

        public void DisconnectClient(NetworkConnection connection)
        {
            if (clients.ContainsKey(connection) && connection.IsCreated)
            {
                connection.Disconnect(driver);
                pendingRemovals.Add(connection);
            }
        }

        // -------------------------
        // CLEANUP
        // -------------------------
        public void Dispose()
        {
            if (State.Disposed)
                return;

            State.Disposed = true;

            Stop();

            driver.Dispose();
        }
    }

    public class UTPSocketServerOptions
    {
        public ushort Port = 7777;
        public float HeartbeatTimeout = 15f;
        public Func<NetworkConnection, NativeArray<byte>, bool> OnHandshakeValidation;
    }

    public class UTPSocketServerState
    {
        public bool Running;
        public bool Disposed;
    }

    public class ClientInfo
    {
        public NetworkConnection Connection;
        public bool HandshakeComplete;
        public float HeartbeatTimer;
    }
}
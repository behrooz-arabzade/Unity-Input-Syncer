using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;
using UnityInputSyncerCore.Utils;

namespace UnityInputSyncerCore.UTPSocket
{
    public sealed class UTPSocketClient : ISocketClient
    {
        // EVENTS
        public event Action OnConnected;
        public event Action<string> OnDisconnected;
        public event Action OnReconnected;
        public event Action<string> OnError;

        public bool IsConnected => State.Connected;
        public float LatencyMs => State.LatencyMs;

        NetworkDriver driver;
        NetworkConnection connection;

        NetworkPipeline reliable;
        NetworkPipeline unreliable;

        NetworkEndpoint endpoint;

        private Dictionary<string, string> Payload;
        public UTPSocketClientOptions Options;
        public UTPSocketClientState State;

        // -------------------------
        // CONSTRUCTOR
        // -------------------------
        public UTPSocketClient(UTPSocketClientOptions options)
        {
            Options = options;
            State = new UTPSocketClientState();

            driver = NetworkDriver.Create();

            Payload = Options.Payload ?? new Dictionary<string, string>();

            reliable = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            unreliable = driver.CreatePipeline(typeof(UnreliableSequencedPipelineStage));

            endpoint = NetworkEndpoint.Parse(Options.Host, Options.Port);
        }

        // -------------------------
        // CONNECT
        // -------------------------
        public void Connect()
        {
            if (connection.IsCreated)
                return;

            connection = driver.Connect(endpoint);
            PlayerLoopHook.Register(TickInternal);
        }

        // -------------------------
        // INTERNAL TICK (MAIN THREAD)
        // -------------------------
        void TickInternal()
        {
            if (State.Disposed)
                return;

            driver.ScheduleUpdate().Complete();

            HandleNetworkEvents();
            HandleHeartbeat(Time.deltaTime);
            HandleReconnect(Time.deltaTime);
        }

        void HandleNetworkEvents()
        {
            if (!connection.IsCreated)
                return;

            NetworkEvent.Type evt;
            while ((evt = driver.PopEventForConnection(connection, out var reader))
                   != NetworkEvent.Type.Empty)
            {
                switch (evt)
                {
                    case NetworkEvent.Type.Connect:
                        SendHandShake(default);
                        break;

                    case NetworkEvent.Type.Data:
                        HandleIncoming(reader);
                        break;

                    case NetworkEvent.Type.Disconnect:
                        OnDisconnected?.Invoke("Disconnected by server");
                        State.Connected = false;
                        BeginReconnect();
                        break;
                }
            }
        }

        void HandleHeartbeat(float delta)
        {
            if (!connection.IsCreated || !State.Connected || State.Reconnecting)
                return;

            // Send ping
            State.HeartbeatSendTimer -= delta;
            if (State.HeartbeatSendTimer <= 0f)
            {
                SendHeartbeatPing();
                State.HeartbeatSendTimer = Options.HeartbeatInterval;
                State.AwaitingPong = true;
            }

            // Timeout check
            if (State.AwaitingPong)
            {
                State.HeartbeatTimeoutTimer += delta;
                if (State.HeartbeatTimeoutTimer >= Options.HeartbeatTimeout)
                {
                    OnError?.Invoke("Heartbeat timeout");
                    HeartbeatDisconnect();
                }
            }
        }

        void HandleIncoming(DataStreamReader reader)
        {
            UTPSocketDataType type = (UTPSocketDataType)reader.ReadByte();

            switch (type)
            {
                case UTPSocketDataType.Handshake:
                    byte handshakeSuccess = reader.ReadByte();
                    string handshakeErrorMessage = "";
                    if (handshakeSuccess == 0)
                    {
                        int errorMessageLength = reader.ReadInt();
                        var errorMessageBytes = new NativeArray<byte>(errorMessageLength, Allocator.Temp);
                        reader.ReadBytes(errorMessageBytes);

                        byte[] tempBuffer = ArrayPool<byte>.Shared.Rent(errorMessageLength);
                        try
                        {
                            errorMessageBytes.CopyTo(tempBuffer);
                            handshakeErrorMessage = Encoding.UTF8.GetString(tempBuffer, 0, errorMessageLength);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(tempBuffer);
                            errorMessageBytes.Dispose();
                        }
                    }
                    OnHandshake(handshakeSuccess != 0, handshakeErrorMessage);
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

                        OnJsonData(eventName, json);
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
                    OnBinaryData(eventId, data);
                    break;
                case UTPSocketDataType.HeartbeatPong:
                    OnHeartbeatPong();
                    break;
            }
        }

        private void OnHandshake(bool successful, string errorMessage = "")
        {
            if (!successful)
            {
                OnError?.Invoke($"Handshake failed: {errorMessage}");
                BeginReconnect();
                return;
            }

            if (State.Reconnecting)
                OnReconnected?.Invoke();
            else
                OnConnected?.Invoke();

            State.Reconnecting = false;
            State.Connected = true;

            // Reset heartbeat timers
            State.HeartbeatSendTimer = Options.HeartbeatInterval;
            State.HeartbeatTimeoutTimer = 0f;
            State.AwaitingPong = false;
        }

        private void OnJsonData(string eventName, string json)
        {
            if (jsonCallbacks.ContainsKey(eventName))
            {
                var jToken = JToken.Parse(json);
                foreach (var callback in jsonCallbacks[eventName])
                {
                    callback(jToken);
                }
            }
        }

        private void OnBinaryData(int eventId, NativeArray<byte> data)
        {
            if (binaryCallbacks.ContainsKey(eventId))
            {
                foreach (var callback in binaryCallbacks[eventId])
                {
                    callback(data);
                }
            }
        }

        private void OnHeartbeatPong()
        {
            State.AwaitingPong = false;
            State.HeartbeatTimeoutTimer = 0f;

            long elapsed = Stopwatch.GetTimestamp() - State.PingSendTimestamp;
            State.LatencyMs = (float)(elapsed * 1000.0 / Stopwatch.Frequency);
        }

        void HandleReconnect(float delta)
        {
            if (!State.Reconnecting)
                return;

            State.ReconnectTimer -= delta;
            if (State.ReconnectTimer <= 0f)
            {
                State.Reconnecting = false;
                Connect();
            }
        }

        // -------------------------
        // SEND
        // -------------------------
        public void SendJson(string eventName, string json, bool reliableSend = true)
        {
            // Calculate sizes first
            int eventNameByteCount = Encoding.UTF8.GetByteCount(eventName);
            int jsonByteCount = Encoding.UTF8.GetByteCount(json);

            // Rent buffers from pool (no GC allocation)
            byte[] eventNameBuffer = ArrayPool<byte>.Shared.Rent(eventNameByteCount);
            byte[] jsonBuffer = ArrayPool<byte>.Shared.Rent(jsonByteCount);

            try
            {
                // Encode into pooled buffers
                Encoding.UTF8.GetBytes(eventName, 0, eventName.Length, eventNameBuffer, 0);
                Encoding.UTF8.GetBytes(json, 0, json.Length, jsonBuffer, 0);

                // Create NativeArrays from the filled buffers
                var eventNameBytes = new NativeArray<byte>(eventNameByteCount, Allocator.Temp);
                var jsonBytes = new NativeArray<byte>(jsonByteCount, Allocator.Temp);

                NativeArray<byte>.Copy(eventNameBuffer, eventNameBytes, eventNameByteCount);
                NativeArray<byte>.Copy(jsonBuffer, jsonBytes, jsonByteCount);

                SendInternal(UTPSocketDataType.Json, eventNameBytes, jsonBytes, reliableSend);
            }
            finally
            {
                // Return buffers to pool for reuse
                ArrayPool<byte>.Shared.Return(eventNameBuffer);
                ArrayPool<byte>.Shared.Return(jsonBuffer);
            }
        }

        public void SendBinary(int eventId, NativeArray<byte> data, bool reliableSend = false)
        {
            var eventIdBytes = new NativeArray<byte>(4, Allocator.Temp);
            eventIdBytes[0] = (byte)eventId;
            eventIdBytes[1] = (byte)(eventId >> 8);
            eventIdBytes[2] = (byte)(eventId >> 16);
            eventIdBytes[3] = (byte)(eventId >> 24);

            SendInternal(UTPSocketDataType.Binary, eventIdBytes, data, reliableSend);
        }

        public void SendHandShake(NativeArray<byte> data)
        {
            SendInternal(UTPSocketDataType.Handshake, default, data, true);
        }

        private void SendHeartbeatPing()
        {
            State.PingSendTimestamp = Stopwatch.GetTimestamp();
            SendInternal(UTPSocketDataType.HeartbeatPing, default, default, true);
        }

        void SendInternal(UTPSocketDataType type, NativeArray<byte> eventNameOrId, NativeArray<byte> payload, bool reliableSend)
        {
            if (!connection.IsCreated)
                return;

            var pipe = reliableSend ? reliable : unreliable;

            int status = driver.BeginSend(pipe, connection, out var writer);
            if (status != 0)
            {
                UnityEngine.Debug.LogWarning($"[UTPSocketClient] BeginSend failed with status {status}");
                return;
            }

            writer.WriteByte((byte)type);

            if (type == UTPSocketDataType.HeartbeatPing)
            {
                // No additional data for heartbeat ping
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
        private Dictionary<string, List<Action<JToken>>> jsonCallbacks = new Dictionary<string, List<Action<JToken>>>();
        public void On(string eventName, Action<JToken> callback)
        {
            if (!jsonCallbacks.ContainsKey(eventName))
            {
                jsonCallbacks.Add(eventName, new List<Action<JToken>>());
            }

            jsonCallbacks[eventName].Add(callback);
        }

        public Dictionary<int, List<Action<NativeArray<byte>>>> binaryCallbacks = new Dictionary<int, List<Action<NativeArray<byte>>>>();
        public void On(int eventId, Action<NativeArray<byte>> callback)
        {
            if (!binaryCallbacks.ContainsKey(eventId))
            {
                binaryCallbacks.Add(eventId, new List<Action<NativeArray<byte>>>());
            }

            binaryCallbacks[eventId].Add(callback);
        }

        // -------------------------
        // RECONNECT
        // -------------------------
        void BeginReconnect()
        {
            connection = default;
            State.Reconnecting = true;
            State.ReconnectTimer = Options.ReconnectDelay;
        }

        void HeartbeatDisconnect()
        {
            if (connection.IsCreated)
                connection.Disconnect(driver);

            OnDisconnected?.Invoke("Heartbeat timeout");

            State.Connected = false;
            BeginReconnect();
        }

        // -------------------------
        // CLEANUP
        // -------------------------
        public void Dispose()
        {
            if (State.Disposed)
                return;

            State.Disposed = true;

            PlayerLoopHook.Unregister(TickInternal);

            if (connection.IsCreated)
                connection.Disconnect(driver);

            driver.Dispose();
        }
    }

    public class UTPSocketClientOptions
    {
        public string Host;
        public ushort Port;
        public Dictionary<string, string> Payload;
        public float HeartbeatInterval = 3f;
        public float HeartbeatTimeout = 8f;
        public float ReconnectDelay = 2f;
    }

    public class UTPSocketClientState
    {
        public float HeartbeatSendTimer;
        public float HeartbeatTimeoutTimer;
        public bool AwaitingPong;
        public float ReconnectTimer;
        public bool Reconnecting;
        public bool Connected;
        public bool Disposed;
        public long PingSendTimestamp;
        public float LatencyMs = -1f;
    }
}
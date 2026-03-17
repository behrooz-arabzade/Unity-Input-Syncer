using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEngine;
using UnityInputSyncerCore;
using UnityInputSyncerCore.UTPSocket;

namespace UnityInputSyncerClient.Drivers
{
    public class UTPClientDriver : IClientDriver
    {
        private UTPSocketClient Socket;

        public override bool IsConnected => Socket != null && Socket.State.Connected;

        private UTPDriverOptions Options;

        public UTPClientDriver(UTPDriverOptions options = null)
        {
            Options = options ?? new UTPDriverOptions();
        }

        public override async Task<bool> ConnectAsync()
        {
            UTPSocketClientOptions socketClientOptions = new UTPSocketClientOptions
            {
                Host = Options.Ip,
                Port = Options.Port,
                Payload = Options.Payload,
                HeartbeatInterval = 1,
                HeartbeatTimeout = 5,
                ReconnectDelay = 0.1f,
            };

            Socket = new UTPSocketClient(socketClientOptions);

            var connectionTcs = new TaskCompletionSource<bool>();

            if (Options.FakeLatency && Options.ConnectDelayMs > 0)
            {
                await Task.Delay(Options.ConnectDelayMs);
            }

            Socket.OnConnected += () =>
            {
                OnConnected();
                connectionTcs.TrySetResult(true);
            };

            RegisterSocketCommonEvents();

            try
            {
                Socket.Connect();

                var completedTask = await Task.WhenAny(connectionTcs.Task,
                    Task.Delay(Options.ConnectionTimeoutMs));

                if (completedTask == connectionTcs.Task)
                {
                    return await connectionTcs.Task;
                }
                else
                {
                    OnError("Connection timeout - connection-complete event not received");
                    return false;
                }
            }
            catch (Exception ex)
            {
                OnError($"UTPSocketClient connection error: {ex.Message}");
                connectionTcs.TrySetResult(false);
                return false;
            }
        }

        private void RegisterSocketCommonEvents()
        {
            Socket.OnReconnected += () =>
            {
                OnReconnected();
            };

            Socket.OnError += (errorMessage) =>
            {
                OnError(errorMessage);
            };

            Socket.OnDisconnected += (reason) =>
            {
                OnDisconnected(reason);
            };
        }

        public override async Task DisconnectAsync()
        {
            Socket.Dispose();
        }

        public override bool Emit(string eventName, object data = null, ClientDriverEmitChannel channel = ClientDriverEmitChannel.Reliable)
        {
            if (!IsConnected)
            {
                OnError("Cannot emit: Not connected");
                return false;
            }

            string json = data != null ?
                JsonConvert.SerializeObject(data, Options.JsonSerializerSettings) : "{}";


            Socket.SendJson(eventName, json, channel == ClientDriverEmitChannel.Reliable);
            return true;
        }

        public override bool Emit(int eventId, INativeArraySerializable data = null, ClientDriverEmitChannel channel = ClientDriverEmitChannel.Reliable)
        {
            if (!IsConnected)
            {
                OnError("Cannot emit: Not connected");
                return false;
            }

            NativeArray<byte> nativeBytes = data != null ?
                data.ToNativeBytes(Allocator.Temp) : new NativeArray<byte>(0, Allocator.Temp);

            Socket.SendBinary(eventId, nativeBytes, channel == ClientDriverEmitChannel.Reliable);
            nativeBytes.Dispose();
            return true;
        }

        public override void On(string eventName, Action<ConnectionResponse> callback)
        {
            Socket.On(eventName, (json) =>
            {
                var response = new ConnectionResponse
                {
                    data = json
                };
                callback(response);
            });
        }

        public override void On(int eventId, Action<NativeArray<byte>> callback)
        {
            Socket.On(eventId, (bytes) =>
            {
                callback(bytes);
            });
        }

        public override T GetData<T>(ConnectionResponse response)
        {
            JToken data = (JToken)response.data;
            return data.ToObject<T>();
        }

        public override T GetData<T>(NativeArray<byte> response)
        {
            Debug.LogError("UTP driver does not support binary data deserialization.");
            throw new NotImplementedException();
        }
    }

    public class UTPDriverOptions
    {
        public string Ip;
        public ushort Port;
        public Dictionary<string, string> Payload;
        public string JwtToken = "";
        public bool FakeLatency = false;
        public int ConnectionTimeoutMs = 5000;
        public int ConnectDelayMs = 0;
        public int EmitMinDelayMs = 0;
        public int EmitMaxDelayMs = 0;
        public JsonSerializerSettings JsonSerializerSettings = new JsonSerializerSettings();
    }
}
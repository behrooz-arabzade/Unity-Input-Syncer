using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SocketIOClient;
using SocketIOClient.Newtonsoft.Json;
using Unity.Collections;
using UnityEngine;
using UnityInputSyncerCore;

namespace UnityInputSyncerClient.Drivers
{
    public class SocketIODriver : IClientDriver
    {
        private SocketIOUnity Socket;
        public override bool IsConnected => Socket?.Connected ?? false;
        public override float LatencyMs => -1f;

        private SocketIODriverOptions Options;

        public SocketIODriver(SocketIODriverOptions options = null)
        {
            Options = options ?? new SocketIODriverOptions();
        }

        public override async Task<bool> ConnectAsync()
        {
            // A second ConnectAsync on the same driver replaces the socket. The old one has
            // to go with it: it still holds this driver's OnDisconnected and OnError
            // handlers, and an old socket reporting a disconnect after a successful
            // reconnect looks to the consumer exactly like the new one failing.
            if (Socket != null)
            {
                try
                {
                    Socket.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"SocketIODriver: disposing the previous socket threw: {ex.Message}");
                }
                Socket = null;
            }

            Socket = new SocketIOUnity(Options.Url, new SocketIOOptions
            {
                ExtraHeaders = new Dictionary<string, string>
                {
                    { "Authorization", $"Bearer {Options.JwtToken}" },
                },
                Transport = SocketIOClient.Transport.TransportProtocol.WebSocket,
                Path = "/match-gateway",
                Query = Options.Payload,
            }, SocketIOUnity.UnityThreadScope.Update);

            var connectionTcs = new TaskCompletionSource<bool>();

            if (Options.FakeLatency && Options.ConnectDelayMs > 0)
            {
                await Task.Delay(Options.ConnectDelayMs);
            }

            Socket.OnConnected += (sender, e) =>
            {
                OnConnected();
                connectionTcs.TrySetResult(true);
            };

            Socket.JsonSerializer = new NewtonsoftJsonSerializer(Options.JsonSerializerSettings);

            // Every name already in eventCallbacks has a dispatch closure — registered on the
            // socket this call just replaced. Re-bind them onto the new one first. Without
            // this a second ConnectAsync produced a live socket that received no events at
            // all: On() short-circuits registration once a name is in eventCallbacks, and
            // pendingJsonCallbacks was drained by the first connect, so nothing re-registered
            // and the failure was silent. Found on the real server by football-card E07/S09.
            foreach (var eventName in eventCallbacks.Keys)
            {
                RegisterSocketDispatch(eventName);
            }

            // Then replay registrations buffered before a Socket existed at all. Order
            // matters: a name already re-bound above is a no-op here, and a name that is new
            // gets its dispatch from On() — neither path registers a name twice.
            foreach (var (eventName, callback) in pendingJsonCallbacks)
            {
                On(eventName, callback);
            }
            pendingJsonCallbacks.Clear();

            RegisterSocketCommonEvents();

            try
            {
                await Socket.ConnectAsync();

                var completedTask = await Task.WhenAny(connectionTcs.Task, Task.Delay(5000));

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
                OnError($"MatchSocket connection error: {ex.Message}");
                connectionTcs.TrySetResult(false);
                return false;
            }
        }

        private void RegisterSocketCommonEvents()
        {
            Socket.OnReconnected += (sender, e) =>
            {
                OnReconnected();
            };

            Socket.OnError += (sender, errorMessage) =>
            {
                OnError(errorMessage);
            };

            Socket.OnDisconnected += (sender, reason) =>
            {
                OnDisconnected(reason);
            };
        }

        public override async Task DisconnectAsync()
        {
            if (Socket == null)
            {
                return;
            }

            await Socket.DisconnectAsync();
        }

        public override bool Emit(string eventName, object data = null, ClientDriverEmitChannel channel = ClientDriverEmitChannel.Reliable)
        {
            if (Socket == null || !Socket.Connected)
            {
                OnError("Emit failed: Socket is not connected");
                return false;
            }

            if (Options.FakeLatency && Options.EmitMinDelayMs > 0 && Options.EmitMaxDelayMs >= Options.EmitMinDelayMs)
            {
                var random = new System.Random();
                int delay = random.Next(Options.EmitMinDelayMs, Options.EmitMaxDelayMs + 1);
                Task.Delay(delay).ContinueWith(_ =>
                {
                    Socket.EmitAsync(eventName, data);
                });
            }
            else
            {
                Socket.EmitAsync(eventName, data);
            }

            return true;
        }

        public override bool Emit(int eventId, INativeArraySerializable data = null, ClientDriverEmitChannel channel = ClientDriverEmitChannel.Reliable)
        {
            throw new NotSupportedException(
                "Socket.IO transport does not support binary events. Use the UTP driver for binary event support.");
        }

        public override T GetData<T>(ConnectionResponse response)
        {
            SocketIOResponse socketResponse = (SocketIOResponse)response.data;
            return socketResponse.GetValue<T>();
        }

        private Dictionary<string, List<Action<ConnectionResponse>>> eventCallbacks =
        new Dictionary<string, List<Action<ConnectionResponse>>>();

        private List<(string eventName, Action<ConnectionResponse> callback)> pendingJsonCallbacks = new();

        public override void On(string eventName, Action<ConnectionResponse> callback)
        {
            if (Socket == null)
            {
                pendingJsonCallbacks.Add((eventName, callback));
                return;
            }

            if (!eventCallbacks.ContainsKey(eventName))
            {
                eventCallbacks.Add(eventName, new List<Action<ConnectionResponse>>());
                RegisterSocketDispatch(eventName);
            }

            eventCallbacks[eventName].Add(callback);
        }

        /// <summary>
        /// Binds one event name on the current socket to this driver's callback list. The
        /// list is looked up on every dispatch, not captured, so re-binding after a
        /// reconnect keeps the callbacks that were registered before it.
        /// </summary>
        private void RegisterSocketDispatch(string eventName)
        {
            Socket.OnUnityThread(eventName, (response) =>
            {
                var connectionResponse = new ConnectionResponse
                {
                    data = response
                };
                foreach (var callback in eventCallbacks[eventName])
                {
                    callback(connectionResponse);
                }
            });
        }

        public override void On(int eventId, Action<NativeArray<byte>> callback)
        {
            throw new NotSupportedException(
                "Socket.IO transport does not support binary events. Use the UTP driver for binary event support.");
        }

        public override T GetData<T>(NativeArray<byte> response)
        {
            throw new NotSupportedException(
                "Socket.IO transport does not support binary events. Use the UTP driver for binary event support.");
        }
    }

    public class SocketIODriverOptions
    {
        public string Url;
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

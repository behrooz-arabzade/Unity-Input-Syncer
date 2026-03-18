using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityInputSyncerCore;

namespace UnityInputSyncerClient.Tests
{
    public class EmittedEvent
    {
        public string EventName;
        public object Data;
        public ClientDriverEmitChannel Channel;
    }

    public class TestClientDriver : IClientDriver
    {
        private bool _isConnected;
        public override bool IsConnected => _isConnected;

        public void SetConnected(bool connected) => _isConnected = connected;

        public bool ConnectAsyncResult = true;
        public List<EmittedEvent> EmittedEvents = new List<EmittedEvent>();
        public Dictionary<string, List<Action<ConnectionResponse>>> EventCallbacks = new Dictionary<string, List<Action<ConnectionResponse>>>();

        public override async Task<bool> ConnectAsync()
        {
            _isConnected = ConnectAsyncResult;
            return ConnectAsyncResult;
        }

        public override Task DisconnectAsync()
        {
            _isConnected = false;
            return Task.CompletedTask;
        }

        public override bool Emit(string eventName, object data = null, ClientDriverEmitChannel channel = ClientDriverEmitChannel.Reliable)
        {
            EmittedEvents.Add(new EmittedEvent
            {
                EventName = eventName,
                Data = data,
                Channel = channel
            });
            return true;
        }

        public override bool Emit(int eventId, INativeArraySerializable data = null, ClientDriverEmitChannel channel = ClientDriverEmitChannel.Reliable)
        {
            throw new NotImplementedException("TestClientDriver does not support binary events.");
        }

        public override void On(string eventName, Action<ConnectionResponse> callback)
        {
            if (!EventCallbacks.ContainsKey(eventName))
            {
                EventCallbacks[eventName] = new List<Action<ConnectionResponse>>();
            }
            EventCallbacks[eventName].Add(callback);
        }

        public override void On(int eventId, Action<NativeArray<byte>> callback)
        {
            throw new NotImplementedException("TestClientDriver does not support binary events.");
        }

        public override T GetData<T>(ConnectionResponse response)
        {
            if (response.data is JToken jToken)
            {
                return jToken.ToObject<T>();
            }
            return JObject.FromObject(response.data).ToObject<T>();
        }

        public override T GetData<T>(NativeArray<byte> response)
        {
            T instance = new T();
            instance.FromNativeBytes(response);
            return instance;
        }

        /// <summary>
        /// Simulates a server sending an event to the client.
        /// </summary>
        public void TriggerEvent(string eventName, object data)
        {
            if (!EventCallbacks.ContainsKey(eventName))
                return;

            var response = new ConnectionResponse { data = JToken.FromObject(data) };
            foreach (var callback in EventCallbacks[eventName])
            {
                callback(response);
            }
        }
    }
}

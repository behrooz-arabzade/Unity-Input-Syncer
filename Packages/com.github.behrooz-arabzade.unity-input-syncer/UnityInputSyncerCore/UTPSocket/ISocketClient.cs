using System;
using Newtonsoft.Json.Linq;
using Unity.Collections;

namespace UnityInputSyncerCore.UTPSocket
{
    public interface ISocketClient : IDisposable
    {
        event Action OnConnected;
        event Action<string> OnDisconnected;
        event Action OnReconnected;
        event Action<string> OnError;
        bool IsConnected { get; }
        float LatencyMs { get; }
        void Connect();
        void SendJson(string eventName, string json, bool reliable = true);
        void SendBinary(int eventId, NativeArray<byte> data, bool reliable = false);
        void On(string eventName, Action<JToken> callback);
        void On(int eventId, Action<NativeArray<byte>> callback);
    }
}

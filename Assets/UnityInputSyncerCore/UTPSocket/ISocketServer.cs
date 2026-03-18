using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Unity.Collections;

namespace UnityInputSyncerCore.UTPSocket
{
    public interface ISocketServer : IDisposable
    {
        event Action<int> OnClientConnected;
        event Action<int> OnClientDisconnected;
        event Action<int, string> OnClientError;

        void Start();
        void Stop();
        void SendJson(int connectionId, string eventName, string json, bool reliable = true);
        void SendBinary(int connectionId, int eventId, NativeArray<byte> data, bool reliable = false);
        void On(string eventName, Action<int, JToken> callback);
        void On(int eventId, Action<int, NativeArray<byte>> callback);
        int GetConnectedClientCount();
        IEnumerable<int> GetConnectedClients();
        void DisconnectClient(int connectionId);
    }
}

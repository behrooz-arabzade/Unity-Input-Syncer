using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityInputSyncerCore.UTPSocket;

namespace Tests.Helpers
{
    public class SentMessage
    {
        public int ConnectionId;
        public string EventName;
        public string Json;
        public bool Reliable;
    }

    public class SentBinaryMessage
    {
        public int ConnectionId;
        public int EventId;
        public byte[] Data;
        public bool Reliable;
    }

    public class FakeSocketServer : ISocketServer
    {
        public event Action<int> OnClientConnected;
        public event Action<int> OnClientDisconnected;
        public event Action<int, string> OnClientError;

        public List<SentMessage> SentMessages = new List<SentMessage>();
        public List<SentBinaryMessage> SentBinaryMessages = new List<SentBinaryMessage>();
        public bool Started;
        public bool Stopped;
        public bool IsDisposed;

        private Dictionary<string, List<Action<int, JToken>>> jsonCallbacks = new Dictionary<string, List<Action<int, JToken>>>();
        private Dictionary<int, List<Action<int, NativeArray<byte>>>> binaryCallbacks = new Dictionary<int, List<Action<int, NativeArray<byte>>>>();

        private HashSet<int> connectedClients = new HashSet<int>();
        private int nextConnectionId = 0;

        // -------------------------
        // ISocketServer implementation
        // -------------------------

        public void Start()
        {
            Started = true;
        }

        public void Stop()
        {
            Stopped = true;
        }

        public void SendJson(int connectionId, string eventName, string json, bool reliable = true)
        {
            SentMessages.Add(new SentMessage
            {
                ConnectionId = connectionId,
                EventName = eventName,
                Json = json,
                Reliable = reliable
            });
        }

        public void SendBinary(int connectionId, int eventId, NativeArray<byte> data, bool reliable = false)
        {
            var bytes = new byte[data.Length];
            data.CopyTo(bytes);
            SentBinaryMessages.Add(new SentBinaryMessage
            {
                ConnectionId = connectionId,
                EventId = eventId,
                Data = bytes,
                Reliable = reliable
            });
        }

        public void On(string eventName, Action<int, JToken> callback)
        {
            if (!jsonCallbacks.ContainsKey(eventName))
                jsonCallbacks[eventName] = new List<Action<int, JToken>>();

            jsonCallbacks[eventName].Add(callback);
        }

        public void On(int eventId, Action<int, NativeArray<byte>> callback)
        {
            if (!binaryCallbacks.ContainsKey(eventId))
                binaryCallbacks[eventId] = new List<Action<int, NativeArray<byte>>>();

            binaryCallbacks[eventId].Add(callback);
        }

        public int GetConnectedClientCount()
        {
            return connectedClients.Count;
        }

        public IEnumerable<int> GetConnectedClients()
        {
            return connectedClients;
        }

        public void DisconnectClient(int connectionId)
        {
            connectedClients.Remove(connectionId);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }

        // -------------------------
        // Simulation methods for tests
        // -------------------------

        public int SimulateClientConnect()
        {
            int id = nextConnectionId++;
            connectedClients.Add(id);
            OnClientConnected?.Invoke(id);
            return id;
        }

        public void SimulateClientDisconnect(int connectionId)
        {
            connectedClients.Remove(connectionId);
            OnClientDisconnected?.Invoke(connectionId);
        }

        public void SimulateJsonEvent(int connectionId, string eventName, JToken data)
        {
            if (!jsonCallbacks.ContainsKey(eventName))
                return;

            foreach (var callback in jsonCallbacks[eventName])
            {
                callback(connectionId, data);
            }
        }

        public void SimulateClientError(int connectionId, string error)
        {
            OnClientError?.Invoke(connectionId, error);
        }

        public List<SentMessage> GetMessagesSentTo(int connectionId)
        {
            return SentMessages.FindAll(m => m.ConnectionId == connectionId);
        }

        public List<SentMessage> GetMessagesByEvent(string eventName)
        {
            return SentMessages.FindAll(m => m.EventName == eventName);
        }

        public void ClearSentMessages()
        {
            SentMessages.Clear();
            SentBinaryMessages.Clear();
        }
    }
}

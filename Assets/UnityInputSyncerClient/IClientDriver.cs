using System;
using System.Threading.Tasks;
using Unity.Collections;
using UnityInputSyncerCore;

namespace UnityInputSyncerClient
{
    public abstract class IClientDriver
    {
        public abstract bool IsConnected { get; }
        public abstract Task<bool> ConnectAsync();
        public abstract Task DisconnectAsync();
        public abstract bool Emit(string eventName, object data = null, ClientDriverEmitChannel channel = ClientDriverEmitChannel.Reliable);
        public abstract bool Emit(int eventId, INativeArraySerializable data = null, ClientDriverEmitChannel channel = ClientDriverEmitChannel.Reliable);
        public abstract void On(string eventName, Action<ConnectionResponse> callback);
        public abstract void On(int eventId, Action<NativeArray<byte>> callback);
        public abstract T GetData<T>(ConnectionResponse response);
        public abstract T GetData<T>(NativeArray<byte> response) where T : INativeArraySerializable, new();

        public Action OnConnected = () => { };
        public Action OnReconnected = () => { };
        public Action<string> OnError = (errorMessage) => { };
        public Action<string> OnDisconnected = (reason) => { };
    }

    public struct ConnectionResponse
    {
        public object data;
    }

    public enum ClientDriverEmitChannel
    {
        Reliable,
        Unreliable
    }
}
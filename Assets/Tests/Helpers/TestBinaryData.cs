using Unity.Collections;
using UnityInputSyncerCore;

namespace UnityInputSyncerClient.Tests
{
    public class TestBinaryData : INativeArraySerializable
    {
        public int IntValue;
        public byte ByteValue;

        public NativeArray<byte> ToNativeBytes(Allocator allocator = Allocator.Temp)
        {
            var bytes = new NativeArray<byte>(5, allocator);
            bytes[0] = (byte)(IntValue & 0xFF);
            bytes[1] = (byte)((IntValue >> 8) & 0xFF);
            bytes[2] = (byte)((IntValue >> 16) & 0xFF);
            bytes[3] = (byte)((IntValue >> 24) & 0xFF);
            bytes[4] = ByteValue;
            return bytes;
        }

        public void FromNativeBytes(NativeArray<byte> nativeBytes)
        {
            IntValue = nativeBytes[0] | (nativeBytes[1] << 8) | (nativeBytes[2] << 16) | (nativeBytes[3] << 24);
            ByteValue = nativeBytes[4];
        }
    }
}

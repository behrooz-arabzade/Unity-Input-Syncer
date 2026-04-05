using Unity.Collections;

namespace UnityInputSyncerCore
{
    public interface INativeArraySerializable
    {
        public NativeArray<byte> ToNativeBytes(Allocator allocator = Allocator.Temp);
        public void FromNativeBytes(NativeArray<byte> nativeBytes);
    }
}
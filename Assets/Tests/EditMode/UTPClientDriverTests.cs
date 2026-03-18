using System;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using UnityInputSyncerClient;
using UnityInputSyncerClient.Drivers;
using UnityInputSyncerClient.Tests;

namespace Tests.EditMode
{
    public class UTPClientDriverTests
    {
        [Test]
        public void IsConnected_FalseBeforeConnect()
        {
            var driver = new UTPClientDriver(new UTPDriverOptions
            {
                Ip = "127.0.0.1",
                Port = 9999
            });
            Assert.IsFalse(driver.IsConnected);
        }

        [Test]
        public void BinaryGetData_DeserializesFromNativeArray()
        {
            var driver = new UTPClientDriver(new UTPDriverOptions
            {
                Ip = "127.0.0.1",
                Port = 9999
            });

            var original = new TestBinaryData { IntValue = 12345, ByteValue = 42 };
            var bytes = original.ToNativeBytes(Allocator.Temp);

            var result = driver.GetData<TestBinaryData>(bytes);

            Assert.AreEqual(12345, result.IntValue);
            Assert.AreEqual(42, result.ByteValue);
            bytes.Dispose();
        }

        [Test]
        public void BinaryGetData_RoundTrips_WithEdgeValues()
        {
            var driver = new UTPClientDriver(new UTPDriverOptions
            {
                Ip = "127.0.0.1",
                Port = 9999
            });

            var original = new TestBinaryData { IntValue = int.MaxValue, ByteValue = 255 };
            var bytes = original.ToNativeBytes(Allocator.Temp);

            var result = driver.GetData<TestBinaryData>(bytes);

            Assert.AreEqual(int.MaxValue, result.IntValue);
            Assert.AreEqual(255, result.ByteValue);
            bytes.Dispose();
        }

        /// <summary>
        /// Contract: GetData with a buffer shorter than the type expects (TestBinaryData needs 5 bytes)
        /// results in IndexOutOfRangeException. Callers must pass valid-length buffers.
        /// </summary>
        [Test]
        public void BinaryGetData_TruncatedBuffer_Throws()
        {
            var driver = new UTPClientDriver(new UTPDriverOptions
            {
                Ip = "127.0.0.1",
                Port = 9999
            });

            using (var bytes = new NativeArray<byte>(3, Allocator.Temp))
            {
                Assert.Throws<IndexOutOfRangeException>(() =>
                    driver.GetData<TestBinaryData>(bytes));
            }
        }

        [Test]
        public void BinaryGetData_EmptyBuffer_Throws()
        {
            var driver = new UTPClientDriver(new UTPDriverOptions
            {
                Ip = "127.0.0.1",
                Port = 9999
            });

            using (var bytes = new NativeArray<byte>(0, Allocator.Temp))
            {
                Assert.Throws<IndexOutOfRangeException>(() =>
                    driver.GetData<TestBinaryData>(bytes));
            }
        }

        [Test]
        public void Emit_WhenDisconnected_ReturnsFalse()
        {
            var driver = new UTPClientDriver(new UTPDriverOptions
            {
                Ip = "127.0.0.1",
                Port = 9999
            });
            Assert.IsFalse(driver.IsConnected);
            bool result = driver.Emit("test", new { });
            Assert.IsFalse(result, "Emit should return false when not connected");
        }

        [Test]
        public void BinaryGetData_OversizedBuffer_IgnoresExtraBytes()
        {
            var driver = new UTPClientDriver(new UTPDriverOptions
            {
                Ip = "127.0.0.1",
                Port = 9999
            });

            // TestBinaryData needs 5 bytes; create a 10-byte buffer with garbage after valid data
            var original = new TestBinaryData { IntValue = 42, ByteValue = 7 };
            var validBytes = original.ToNativeBytes(Allocator.Temp);

            var oversized = new NativeArray<byte>(10, Allocator.Temp);
            NativeArray<byte>.Copy(validBytes, oversized, validBytes.Length);
            for (int i = validBytes.Length; i < 10; i++)
                oversized[i] = 0xFF;
            validBytes.Dispose();

            var result = driver.GetData<TestBinaryData>(oversized);

            Assert.AreEqual(42, result.IntValue);
            Assert.AreEqual(7, result.ByteValue);
            oversized.Dispose();
        }

        [Test]
        public void BinaryGetData_NegativeIntValue_RoundTrips()
        {
            var driver = new UTPClientDriver(new UTPDriverOptions
            {
                Ip = "127.0.0.1",
                Port = 9999
            });

            var original = new TestBinaryData { IntValue = -1, ByteValue = 0 };
            var bytes = original.ToNativeBytes(Allocator.Temp);

            var result = driver.GetData<TestBinaryData>(bytes);

            Assert.AreEqual(-1, result.IntValue);
            Assert.AreEqual(0, result.ByteValue);
            bytes.Dispose();
        }

        [Test]
        public void BinaryGetData_SequentialCalls_ReturnIndependentInstances()
        {
            var driver = new UTPClientDriver(new UTPDriverOptions
            {
                Ip = "127.0.0.1",
                Port = 9999
            });

            var original = new TestBinaryData { IntValue = 100, ByteValue = 50 };
            var bytes = original.ToNativeBytes(Allocator.Temp);

            var result1 = driver.GetData<TestBinaryData>(bytes);
            var result2 = driver.GetData<TestBinaryData>(bytes);

            Assert.AreNotSame(result1, result2, "Each GetData call should return a new instance");
            Assert.AreEqual(result1.IntValue, result2.IntValue);
            Assert.AreEqual(result1.ByteValue, result2.ByteValue);
            bytes.Dispose();
        }
    }
}

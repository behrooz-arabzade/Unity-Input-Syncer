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
    }
}

using System;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using UnityInputSyncerClient;
using UnityInputSyncerClient.Drivers;

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
        public void BinaryGetData_ThrowsNotImplementedException()
        {
            var driver = new UTPClientDriver(new UTPDriverOptions
            {
                Ip = "127.0.0.1",
                Port = 9999
            });
            var data = new NativeArray<byte>(0, Allocator.Temp);
            LogAssert.Expect(LogType.Error, "UTP driver does not support binary data deserialization.");
            Assert.Throws<NotImplementedException>(() =>
            {
                driver.GetData<string>(data);
            });
            data.Dispose();
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

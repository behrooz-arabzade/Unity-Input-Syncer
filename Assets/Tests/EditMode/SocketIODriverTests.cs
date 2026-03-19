using System;
using NUnit.Framework;
using Unity.Collections;
using UnityInputSyncerClient;
using UnityInputSyncerClient.Drivers;
using UnityInputSyncerClient.Tests;

namespace Tests.EditMode
{
    public class SocketIODriverTests
    {
        [Test]
        public void IsConnected_FalseBeforeConnect()
        {
            var driver = new SocketIODriver();
            Assert.IsFalse(driver.IsConnected);
        }

        [Test]
        public void BinaryEmit_ThrowsNotSupportedException()
        {
            var driver = new SocketIODriver();
            Assert.Throws<NotSupportedException>(() =>
            {
                driver.Emit(1, null, ClientDriverEmitChannel.Reliable);
            });
        }

        [Test]
        public void BinaryOn_ThrowsNotSupportedException()
        {
            var driver = new SocketIODriver();
            Assert.Throws<NotSupportedException>(() =>
            {
                driver.On(1, (data) => { });
            });
        }

        [Test]
        public void BinaryGetData_ThrowsNotSupportedException()
        {
            var driver = new SocketIODriver();
            var data = new NativeArray<byte>(0, Allocator.Temp);
            Assert.Throws<NotSupportedException>(() =>
            {
                driver.GetData<TestBinaryData>(data);
            });
            data.Dispose();
        }

        [Test]
        public void LatencyMs_AlwaysReturnsNegativeOne()
        {
            var driver = new SocketIODriver();
            Assert.AreEqual(-1f, driver.LatencyMs);
        }

        [Test]
        public void On_BeforeConnect_DoesNotThrow()
        {
            var driver = new SocketIODriver();
            Assert.DoesNotThrow(() =>
            {
                driver.On("test-event", (response) => { });
            });
        }
    }
}

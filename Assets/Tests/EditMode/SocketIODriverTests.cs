using System;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using UnityInputSyncerClient;
using UnityInputSyncerClient.Drivers;

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
        public void BinaryEmit_ThrowsNotImplementedException()
        {
            var driver = new SocketIODriver();
            LogAssert.Expect(LogType.Error, "Socket.IO driver does not support binary data events.");
            Assert.Throws<NotImplementedException>(() =>
            {
                driver.Emit(1, null, ClientDriverEmitChannel.Reliable);
            });
        }

        [Test]
        public void BinaryOn_ThrowsNotImplementedException()
        {
            var driver = new SocketIODriver();
            LogAssert.Expect(LogType.Error, "Socket.IO driver does not support binary data events.");
            Assert.Throws<NotImplementedException>(() =>
            {
                driver.On(1, (data) => { });
            });
        }

        [Test]
        public void BinaryGetData_ThrowsNotImplementedException()
        {
            var driver = new SocketIODriver();
            var data = new NativeArray<byte>(0, Allocator.Temp);
            LogAssert.Expect(LogType.Error, "Socket.IO driver does not support binary data events.");
            Assert.Throws<NotImplementedException>(() =>
            {
                driver.GetData<string>(data);
            });
            data.Dispose();
        }
    }
}

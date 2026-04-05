using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using UnityInputSyncerCore;
using UnityInputSyncerUTPServer;

namespace Tests.EditMode
{
    public class MatchAccessHandshakeTests
    {
        private static NativeArray<byte> Utf8Bytes(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            var na = new NativeArray<byte>(b.Length, Allocator.Temp);
            na.CopyFrom(b);
            return na;
        }

        [Test]
        public void OpenMode_AcceptsEmptyPayload()
        {
            var opt = new InputSyncerServerOptions { MatchAccess = MatchAccessMode.Open };
            var data = new NativeArray<byte>(0, Allocator.Temp);
            try
            {
                Assert.IsTrue(MatchAccessHandshake.Validate(opt, data));
            }
            finally
            {
                data.Dispose();
            }
        }

        [Test]
        public void PasswordMode_AcceptsMatchingPassword()
        {
            var opt = new InputSyncerServerOptions
            {
                MatchAccess = MatchAccessMode.Password,
                MatchPassword = "secret",
            };
            var data = Utf8Bytes("{\"matchPassword\":\"secret\"}");
            try
            {
                Assert.IsTrue(MatchAccessHandshake.Validate(opt, data));
            }
            finally
            {
                data.Dispose();
            }
        }

        [Test]
        public void PasswordMode_RejectsWrongPassword()
        {
            var opt = new InputSyncerServerOptions
            {
                MatchAccess = MatchAccessMode.Password,
                MatchPassword = "secret",
            };
            var data = Utf8Bytes("{\"matchPassword\":\"other\"}");
            try
            {
                Assert.IsFalse(MatchAccessHandshake.Validate(opt, data));
            }
            finally
            {
                data.Dispose();
            }
        }

        [Test]
        public void TokenMode_AcceptsListedToken()
        {
            var opt = new InputSyncerServerOptions
            {
                MatchAccess = MatchAccessMode.Token,
                AllowedMatchTokens = new HashSet<string> { "t1", "t2" },
            };
            var data = Utf8Bytes("{\"matchToken\":\"t2\"}");
            try
            {
                Assert.IsTrue(MatchAccessHandshake.Validate(opt, data));
            }
            finally
            {
                data.Dispose();
            }
        }

        [Test]
        public void TokenMode_RejectsUnknownToken()
        {
            var opt = new InputSyncerServerOptions
            {
                MatchAccess = MatchAccessMode.Token,
                AllowedMatchTokens = new HashSet<string> { "t1" },
            };
            var data = Utf8Bytes("{\"matchToken\":\"nope\"}");
            try
            {
                Assert.IsFalse(MatchAccessHandshake.Validate(opt, data));
            }
            finally
            {
                data.Dispose();
            }
        }

        [Test]
        public void RejectsPayloadOverMaxBytes()
        {
            var opt = new InputSyncerServerOptions { MatchAccess = MatchAccessMode.Open };
            var data = new NativeArray<byte>(MatchAccessHandshake.MaxPayloadBytes + 1, Allocator.Temp);
            try
            {
                Assert.IsFalse(MatchAccessHandshake.Validate(opt, data));
            }
            finally
            {
                data.Dispose();
            }
        }

        [Test]
        public void TryGetOptionalUserId_ReturnsTrimmedValue()
        {
            var data = Utf8Bytes("{\"userId\":\"  u1  \"}");
            try
            {
                Assert.IsTrue(MatchAccessHandshake.TryGetOptionalUserId(data, out var uid));
                Assert.AreEqual("u1", uid);
            }
            finally
            {
                data.Dispose();
            }
        }

        [Test]
        public void TryGetOptionalUserId_FalseWhenMissing()
        {
            var data = Utf8Bytes("{}");
            try
            {
                Assert.IsFalse(MatchAccessHandshake.TryGetOptionalUserId(data, out _));
            }
            finally
            {
                data.Dispose();
            }
        }
    }
}

using System;
using NUnit.Framework;
using UnityEngine;
using UnityInputSyncerUTPServer;

namespace Tests.EditMode
{
    public class DedicatedServerBootstrapTests
    {
        // Key used for all env var tests — unique enough to avoid collisions
        private const string TestVar = "INPUT_SYNCER_TEST_VAR";

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(TestVar, null);
            ClearBootstrapEnvVars();
        }

        private static void ClearBootstrapEnvVars()
        {
            Environment.SetEnvironmentVariable("INPUT_SYNCER_PORT", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_MAX_PLAYERS", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_AUTO_START_WHEN_FULL", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_STEP_INTERVAL", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_ALLOW_LATE_JOIN", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_HEARTBEAT_TIMEOUT", null);
        }

        // =========================================================
        // TryGetEnvBool
        // =========================================================

        [Test]
        public void TryGetEnvBool_True_String()
        {
            Environment.SetEnvironmentVariable(TestVar, "true");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvBool(TestVar, out var value));
            Assert.IsTrue(value);
        }

        [Test]
        public void TryGetEnvBool_False_String()
        {
            Environment.SetEnvironmentVariable(TestVar, "false");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvBool(TestVar, out var value));
            Assert.IsFalse(value);
        }

        [Test]
        public void TryGetEnvBool_One()
        {
            Environment.SetEnvironmentVariable(TestVar, "1");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvBool(TestVar, out var value));
            Assert.IsTrue(value);
        }

        [Test]
        public void TryGetEnvBool_Zero()
        {
            Environment.SetEnvironmentVariable(TestVar, "0");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvBool(TestVar, out var value));
            Assert.IsFalse(value);
        }

        [Test]
        public void TryGetEnvBool_CaseInsensitive()
        {
            Environment.SetEnvironmentVariable(TestVar, "TRUE");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvBool(TestVar, out var value));
            Assert.IsTrue(value);
        }

        [Test]
        public void TryGetEnvBool_Invalid_ReturnsFalse()
        {
            Environment.SetEnvironmentVariable(TestVar, "yes");
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvBool(TestVar, out _));
        }

        [Test]
        public void TryGetEnvBool_Missing_ReturnsFalse()
        {
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvBool(TestVar, out _));
        }

        [Test]
        public void TryGetEnvBool_Empty_ReturnsFalse()
        {
            Environment.SetEnvironmentVariable(TestVar, "");
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvBool(TestVar, out _));
        }

        // =========================================================
        // TryGetEnvUShort
        // =========================================================

        [Test]
        public void TryGetEnvUShort_Valid()
        {
            Environment.SetEnvironmentVariable(TestVar, "9999");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvUShort(TestVar, out var value));
            Assert.AreEqual((ushort)9999, value);
        }

        [Test]
        public void TryGetEnvUShort_Zero()
        {
            Environment.SetEnvironmentVariable(TestVar, "0");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvUShort(TestVar, out var value));
            Assert.AreEqual((ushort)0, value);
        }

        [Test]
        public void TryGetEnvUShort_MaxValue()
        {
            Environment.SetEnvironmentVariable(TestVar, "65535");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvUShort(TestVar, out var value));
            Assert.AreEqual(ushort.MaxValue, value);
        }

        [Test]
        public void TryGetEnvUShort_Negative_ReturnsFalse()
        {
            Environment.SetEnvironmentVariable(TestVar, "-1");
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvUShort(TestVar, out _));
        }

        [Test]
        public void TryGetEnvUShort_Overflow_ReturnsFalse()
        {
            Environment.SetEnvironmentVariable(TestVar, "70000");
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvUShort(TestVar, out _));
        }

        [Test]
        public void TryGetEnvUShort_NonNumeric_ReturnsFalse()
        {
            Environment.SetEnvironmentVariable(TestVar, "abc");
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvUShort(TestVar, out _));
        }

        [Test]
        public void TryGetEnvUShort_Missing_ReturnsFalse()
        {
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvUShort(TestVar, out _));
        }

        // =========================================================
        // TryGetEnvInt
        // =========================================================

        [Test]
        public void TryGetEnvInt_Valid()
        {
            Environment.SetEnvironmentVariable(TestVar, "42");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvInt(TestVar, out var value));
            Assert.AreEqual(42, value);
        }

        [Test]
        public void TryGetEnvInt_Negative()
        {
            Environment.SetEnvironmentVariable(TestVar, "-5");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvInt(TestVar, out var value));
            Assert.AreEqual(-5, value);
        }

        [Test]
        public void TryGetEnvInt_NonNumeric_ReturnsFalse()
        {
            Environment.SetEnvironmentVariable(TestVar, "xyz");
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvInt(TestVar, out _));
        }

        [Test]
        public void TryGetEnvInt_Missing_ReturnsFalse()
        {
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvInt(TestVar, out _));
        }

        // =========================================================
        // TryGetEnvFloat
        // =========================================================

        [Test]
        public void TryGetEnvFloat_Integer()
        {
            Environment.SetEnvironmentVariable(TestVar, "10");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvFloat(TestVar, out var value));
            Assert.AreEqual(10f, value, 0.001f);
        }

        [Test]
        public void TryGetEnvFloat_Decimal()
        {
            Environment.SetEnvironmentVariable(TestVar, "0.05");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvFloat(TestVar, out var value));
            Assert.AreEqual(0.05f, value, 0.001f);
        }

        [Test]
        public void TryGetEnvFloat_UsesInvariantCulture()
        {
            // Dot as decimal separator regardless of locale
            Environment.SetEnvironmentVariable(TestVar, "3.14");
            Assert.IsTrue(DedicatedServerBootstrap.TryGetEnvFloat(TestVar, out var value));
            Assert.AreEqual(3.14f, value, 0.001f);
        }

        [Test]
        public void TryGetEnvFloat_NonNumeric_ReturnsFalse()
        {
            Environment.SetEnvironmentVariable(TestVar, "not_a_float");
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvFloat(TestVar, out _));
        }

        [Test]
        public void TryGetEnvFloat_Missing_ReturnsFalse()
        {
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvFloat(TestVar, out _));
        }

        [Test]
        public void TryGetEnvFloat_Empty_ReturnsFalse()
        {
            Environment.SetEnvironmentVariable(TestVar, "");
            Assert.IsFalse(DedicatedServerBootstrap.TryGetEnvFloat(TestVar, out _));
        }

        // =========================================================
        // ApplyEnvironmentOverrides applies env vars to config
        // =========================================================

        [Test]
        public void ApplyEnvironmentOverrides_AppliesPortAndMaxPlayers()
        {
            ClearBootstrapEnvVars();
            try
            {
                Environment.SetEnvironmentVariable("INPUT_SYNCER_PORT", "8888");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_MAX_PLAYERS", "4");

                var go = new GameObject();
                var bootstrap = go.AddComponent<DedicatedServerBootstrap>();
                bootstrap.ApplyEnvironmentOverrides();

                Assert.AreEqual((ushort)8888, bootstrap.ConfigPort);
                Assert.AreEqual(4, bootstrap.ConfigMaxPlayers);

                UnityEngine.Object.DestroyImmediate(go);
            }
            finally
            {
                ClearBootstrapEnvVars();
            }
        }

        [Test]
        public void ApplyEnvironmentOverrides_AppliesBoolAndFloatOptions()
        {
            ClearBootstrapEnvVars();
            try
            {
                Environment.SetEnvironmentVariable("INPUT_SYNCER_AUTO_START_WHEN_FULL", "false");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_STEP_INTERVAL", "0.05");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_ALLOW_LATE_JOIN", "true");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN", "false");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_HEARTBEAT_TIMEOUT", "30");

                var go = new GameObject();
                var bootstrap = go.AddComponent<DedicatedServerBootstrap>();
                bootstrap.ApplyEnvironmentOverrides();

                Assert.IsFalse(bootstrap.ConfigAutoStartWhenFull);
                Assert.AreEqual(0.05f, bootstrap.ConfigStepIntervalSeconds, 0.001f);
                Assert.IsTrue(bootstrap.ConfigAllowLateJoin);
                Assert.IsFalse(bootstrap.ConfigSendStepHistoryOnLateJoin);
                Assert.AreEqual(30f, bootstrap.ConfigHeartbeatTimeout, 0.001f);

                UnityEngine.Object.DestroyImmediate(go);
            }
            finally
            {
                ClearBootstrapEnvVars();
            }
        }
    }
}

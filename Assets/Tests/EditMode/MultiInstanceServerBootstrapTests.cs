using System;
using NUnit.Framework;
using UnityEngine;
using UnityInputSyncerUTPServer;

namespace Tests.EditMode
{
    public class MultiInstanceServerBootstrapTests
    {
        [TearDown]
        public void TearDown()
        {
            ClearAllEnvVars();
        }

        private static void ClearAllEnvVars()
        {
            Environment.SetEnvironmentVariable("INPUT_SYNCER_BASE_PORT", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_MAX_INSTANCES", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_AUTO_RECYCLE", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_ADMIN_PORT", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_ADMIN_AUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_MAX_PLAYERS", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_AUTO_START_WHEN_FULL", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_STEP_INTERVAL", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_ALLOW_LATE_JOIN", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN", null);
            Environment.SetEnvironmentVariable("INPUT_SYNCER_HEARTBEAT_TIMEOUT", null);
        }

        [Test]
        public void ApplyEnvironmentOverrides_NoEnvVarsSet_PreservesAllDefaults()
        {
            ClearAllEnvVars();
            try
            {
                var go = new GameObject();
                var bootstrap = go.AddComponent<MultiInstanceServerBootstrap>();
                bootstrap.ApplyEnvironmentOverrides();

                Assert.AreEqual((ushort)7778, bootstrap.ConfigBasePort);
                Assert.AreEqual(10, bootstrap.ConfigMaxInstances);
                Assert.IsTrue(bootstrap.ConfigAutoRecycleOnFinish);
                Assert.AreEqual((ushort)8080, bootstrap.ConfigAdminPort);
                Assert.AreEqual("", bootstrap.ConfigAuthToken);
                Assert.AreEqual(2, bootstrap.ConfigMaxPlayers);
                Assert.IsTrue(bootstrap.ConfigAutoStartWhenFull);
                Assert.AreEqual(0.1f, bootstrap.ConfigStepIntervalSeconds, 0.001f);
                Assert.IsFalse(bootstrap.ConfigAllowLateJoin);
                Assert.IsTrue(bootstrap.ConfigSendStepHistoryOnLateJoin);
                Assert.AreEqual(15f, bootstrap.ConfigHeartbeatTimeout, 0.001f);

                UnityEngine.Object.DestroyImmediate(go);
            }
            finally
            {
                ClearAllEnvVars();
            }
        }

        [Test]
        public void ApplyEnvironmentOverrides_AllEnvVarsSet_OverridesEverything()
        {
            ClearAllEnvVars();
            try
            {
                Environment.SetEnvironmentVariable("INPUT_SYNCER_BASE_PORT", "9000");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_MAX_INSTANCES", "20");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_AUTO_RECYCLE", "false");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_ADMIN_PORT", "9090");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_ADMIN_AUTH_TOKEN", "secret123");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_MAX_PLAYERS", "8");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_AUTO_START_WHEN_FULL", "false");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_STEP_INTERVAL", "0.25");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_ALLOW_LATE_JOIN", "true");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN", "false");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_HEARTBEAT_TIMEOUT", "60");

                var go = new GameObject();
                var bootstrap = go.AddComponent<MultiInstanceServerBootstrap>();
                bootstrap.ApplyEnvironmentOverrides();

                Assert.AreEqual((ushort)9000, bootstrap.ConfigBasePort);
                Assert.AreEqual(20, bootstrap.ConfigMaxInstances);
                Assert.IsFalse(bootstrap.ConfigAutoRecycleOnFinish);
                Assert.AreEqual((ushort)9090, bootstrap.ConfigAdminPort);
                Assert.AreEqual("secret123", bootstrap.ConfigAuthToken);
                Assert.AreEqual(8, bootstrap.ConfigMaxPlayers);
                Assert.IsFalse(bootstrap.ConfigAutoStartWhenFull);
                Assert.AreEqual(0.25f, bootstrap.ConfigStepIntervalSeconds, 0.001f);
                Assert.IsTrue(bootstrap.ConfigAllowLateJoin);
                Assert.IsFalse(bootstrap.ConfigSendStepHistoryOnLateJoin);
                Assert.AreEqual(60f, bootstrap.ConfigHeartbeatTimeout, 0.001f);

                UnityEngine.Object.DestroyImmediate(go);
            }
            finally
            {
                ClearAllEnvVars();
            }
        }

        [Test]
        public void ApplyEnvironmentOverrides_PoolEnvVarsOnly_OverridesPoolConfig()
        {
            ClearAllEnvVars();
            try
            {
                Environment.SetEnvironmentVariable("INPUT_SYNCER_BASE_PORT", "8500");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_MAX_INSTANCES", "5");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_AUTO_RECYCLE", "false");

                var go = new GameObject();
                var bootstrap = go.AddComponent<MultiInstanceServerBootstrap>();
                bootstrap.ApplyEnvironmentOverrides();

                // Pool overrides applied
                Assert.AreEqual((ushort)8500, bootstrap.ConfigBasePort);
                Assert.AreEqual(5, bootstrap.ConfigMaxInstances);
                Assert.IsFalse(bootstrap.ConfigAutoRecycleOnFinish);

                // Admin defaults preserved
                Assert.AreEqual((ushort)8080, bootstrap.ConfigAdminPort);
                Assert.AreEqual("", bootstrap.ConfigAuthToken);

                // Server defaults preserved
                Assert.AreEqual(2, bootstrap.ConfigMaxPlayers);
                Assert.IsTrue(bootstrap.ConfigAutoStartWhenFull);
                Assert.AreEqual(0.1f, bootstrap.ConfigStepIntervalSeconds, 0.001f);
                Assert.IsFalse(bootstrap.ConfigAllowLateJoin);
                Assert.IsTrue(bootstrap.ConfigSendStepHistoryOnLateJoin);
                Assert.AreEqual(15f, bootstrap.ConfigHeartbeatTimeout, 0.001f);

                UnityEngine.Object.DestroyImmediate(go);
            }
            finally
            {
                ClearAllEnvVars();
            }
        }

        [Test]
        public void ApplyEnvironmentOverrides_AdminEnvVarsOnly_OverridesAdminConfig()
        {
            ClearAllEnvVars();
            try
            {
                Environment.SetEnvironmentVariable("INPUT_SYNCER_ADMIN_PORT", "3000");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_ADMIN_AUTH_TOKEN", "my-token");

                var go = new GameObject();
                var bootstrap = go.AddComponent<MultiInstanceServerBootstrap>();
                bootstrap.ApplyEnvironmentOverrides();

                // Admin overrides applied
                Assert.AreEqual((ushort)3000, bootstrap.ConfigAdminPort);
                Assert.AreEqual("my-token", bootstrap.ConfigAuthToken);

                // Pool defaults preserved
                Assert.AreEqual((ushort)7778, bootstrap.ConfigBasePort);
                Assert.AreEqual(10, bootstrap.ConfigMaxInstances);
                Assert.IsTrue(bootstrap.ConfigAutoRecycleOnFinish);

                // Server defaults preserved
                Assert.AreEqual(2, bootstrap.ConfigMaxPlayers);
                Assert.IsTrue(bootstrap.ConfigAutoStartWhenFull);

                UnityEngine.Object.DestroyImmediate(go);
            }
            finally
            {
                ClearAllEnvVars();
            }
        }

        [Test]
        public void ApplyEnvironmentOverrides_ServerDefaultEnvVarsOnly_OverridesServerDefaults()
        {
            ClearAllEnvVars();
            try
            {
                Environment.SetEnvironmentVariable("INPUT_SYNCER_MAX_PLAYERS", "4");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_AUTO_START_WHEN_FULL", "false");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_STEP_INTERVAL", "0.05");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_ALLOW_LATE_JOIN", "true");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN", "false");
                Environment.SetEnvironmentVariable("INPUT_SYNCER_HEARTBEAT_TIMEOUT", "30");

                var go = new GameObject();
                var bootstrap = go.AddComponent<MultiInstanceServerBootstrap>();
                bootstrap.ApplyEnvironmentOverrides();

                // Server overrides applied
                Assert.AreEqual(4, bootstrap.ConfigMaxPlayers);
                Assert.IsFalse(bootstrap.ConfigAutoStartWhenFull);
                Assert.AreEqual(0.05f, bootstrap.ConfigStepIntervalSeconds, 0.001f);
                Assert.IsTrue(bootstrap.ConfigAllowLateJoin);
                Assert.IsFalse(bootstrap.ConfigSendStepHistoryOnLateJoin);
                Assert.AreEqual(30f, bootstrap.ConfigHeartbeatTimeout, 0.001f);

                // Pool defaults preserved
                Assert.AreEqual((ushort)7778, bootstrap.ConfigBasePort);
                Assert.AreEqual(10, bootstrap.ConfigMaxInstances);
                Assert.IsTrue(bootstrap.ConfigAutoRecycleOnFinish);

                // Admin defaults preserved
                Assert.AreEqual((ushort)8080, bootstrap.ConfigAdminPort);
                Assert.AreEqual("", bootstrap.ConfigAuthToken);

                UnityEngine.Object.DestroyImmediate(go);
            }
            finally
            {
                ClearAllEnvVars();
            }
        }

        [Test]
        public void ApplyEnvironmentOverrides_AuthTokenFromEnv_AppliesString()
        {
            ClearAllEnvVars();
            try
            {
                Environment.SetEnvironmentVariable("INPUT_SYNCER_ADMIN_AUTH_TOKEN", "bearer-test-token-xyz");

                var go = new GameObject();
                var bootstrap = go.AddComponent<MultiInstanceServerBootstrap>();
                bootstrap.ApplyEnvironmentOverrides();

                Assert.AreEqual("bearer-test-token-xyz", bootstrap.ConfigAuthToken);

                UnityEngine.Object.DestroyImmediate(go);
            }
            finally
            {
                ClearAllEnvVars();
            }
        }
    }
}

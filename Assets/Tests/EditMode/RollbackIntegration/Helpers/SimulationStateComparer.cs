using System;
using Unity.Collections;
using NUnit.Framework;
using SyncSimulation;

namespace Tests.RollbackIntegration
{
    public static class SimulationStateComparer
    {
        public static byte[] CaptureStateBytes(SyncSimulationHost host)
        {
            var step = host.CompletedSimStep;
            if (!host.Snapshots.TryGetSnapshotBlobForCompletedStep(step, out var nativeArr))
            {
                host.Snapshots.RecordSnapshotAfterCompletedStep(host.EntityManager, step);
                if (!host.Snapshots.TryGetSnapshotBlobForCompletedStep(step, out nativeArr))
                    throw new InvalidOperationException($"Failed to capture snapshot at step {step}.");
            }

            var bytes = new byte[nativeArr.Length];
            NativeArray<byte>.Copy(nativeArr, bytes, nativeArr.Length);
            return bytes;
        }

        public static byte[] CaptureSnapshotAtStep(SyncSimulationHost host, int step)
        {
            if (!host.Snapshots.TryGetSnapshotBlobForCompletedStep(step, out var nativeArr))
                throw new InvalidOperationException($"No snapshot exists at step {step}.");

            var bytes = new byte[nativeArr.Length];
            NativeArray<byte>.Copy(nativeArr, bytes, nativeArr.Length);
            return bytes;
        }

        public static void AssertStatesEqual(byte[] expected, byte[] actual, string context = "")
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"[{context}] ";

            Assert.AreEqual(expected.Length, actual.Length,
                $"{prefix}Snapshot blob size mismatch: expected {expected.Length} bytes, got {actual.Length} bytes.");

            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    int regionStart = Math.Max(0, i - 4);
                    int regionEnd = Math.Min(expected.Length, i + 5);
                    var expectedRegion = FormatBytes(expected, regionStart, regionEnd);
                    var actualRegion = FormatBytes(actual, regionStart, regionEnd);

                    Assert.Fail(
                        $"{prefix}Snapshot divergence at byte offset {i}.\n" +
                        $"  Expected[{regionStart}..{regionEnd}]: {expectedRegion}\n" +
                        $"  Actual  [{regionStart}..{regionEnd}]: {actualRegion}");
                }
            }
        }

        static string FormatBytes(byte[] data, int start, int end)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = start; i < end; i++)
            {
                if (i > start) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}

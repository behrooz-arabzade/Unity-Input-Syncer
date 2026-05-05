using System.Collections.Generic;
using NUnit.Framework;
using SyncSimulation;
using Unity.Collections;
using Unity.Entities;
using Unity.PerformanceTesting;
using UnityInputSyncerClient;

namespace Tests.RollbackIntegration
{
    [TestFixture]
    public class RollbackPerformanceTests
    {
        [TearDown]
        public void TearDown()
        {
            MockSpawnerSystem.HostRef = null;
        }

        [Test, Performance]
        [TestCase(10, 2.0)]
        [TestCase(50, 5.0)]
        [TestCase(100, 10.0)]
        public void SnapshotCapture_EntityCount(int entityCount, double maxMs)
        {
            var state = InputScenarioBuilder.BuildAuthoritative(100);
            using var host = MockSimulationFactory.Create(state, entityCount: entityCount,
                maxPredictionSteps: 0);

            for (int i = 0; i < 10; i++)
                host.Tick();

            var step = host.CompletedSimStep;

            Measure.Method(() =>
            {
                step++;
                host.Snapshots.RecordSnapshotAfterCompletedStep(host.EntityManager, step);
            })
            .WarmupCount(5)
            .MeasurementCount(50)
            .Run();

            var mean = PerformanceTest.Active.SampleGroups[0].Median;
            Assert.Less(mean, maxMs,
                $"Snapshot capture for {entityCount} entities exceeded {maxMs}ms budget (was {mean:F3}ms)");
        }

        [Test, Performance]
        [TestCase(10, 2.0)]
        [TestCase(50, 5.0)]
        [TestCase(100, 10.0)]
        public void SnapshotRestore_EntityCount(int entityCount, double maxMs)
        {
            var state = InputScenarioBuilder.BuildAuthoritative(20);
            using var host = MockSimulationFactory.Create(state, entityCount: entityCount,
                maxPredictionSteps: 0);

            for (int i = 0; i < 20; i++)
                host.Tick();

            int restoreStep = host.CompletedSimStep - 5;
            if (restoreStep < 0) restoreStep = 0;

            Measure.Method(() =>
            {
                host.Snapshots.RestoreAfterCompletedStep(host.EntityManager, restoreStep);
            })
            .WarmupCount(3)
            .MeasurementCount(30)
            .Run();

            var mean = PerformanceTest.Active.SampleGroups[0].Median;
            Assert.Less(mean, maxMs,
                $"Snapshot restore for {entityCount} entities exceeded {maxMs}ms budget (was {mean:F3}ms)");
        }

        [Test, Performance]
        [TestCase(1, 20.0)]
        [TestCase(5, 30.0)]
        [TestCase(10, 50.0)]
        [TestCase(30, 100.0)]
        public void FullRollbackAndReplay_Depth(int rollbackDepth, double maxMs)
        {
            const int entityCount = 50;
            const int totalSteps = 60;
            int divergeAt = totalSteps - rollbackDepth;

            Measure.Method(() =>
            {
                var state = new InputSyncerState();
                var initial = new List<StepInputs>();
                for (int i = 0; i < divergeAt; i++)
                    initial.Add(MakeStep(i, "correct_" + i));
                state.AddStepInputs(initial);

                using var host = MockSimulationFactory.Create(state, entityCount: entityCount,
                    maxPredictionSteps: totalSteps);

                for (int i = 0; i < totalSteps; i++)
                    host.Tick();

                var correct = new List<StepInputs>();
                for (int i = divergeAt; i < totalSteps; i++)
                    correct.Add(MakeStep(i, "correct_" + i));
                state.AddStepInputs(correct);

                host.Tick();

                MockSpawnerSystem.HostRef = null;
            })
            .WarmupCount(2)
            .MeasurementCount(10)
            .Run();

            var mean = PerformanceTest.Active.SampleGroups[0].Median;
            Assert.Less(mean, maxMs,
                $"Full rollback at depth {rollbackDepth} exceeded {maxMs}ms budget (was {mean:F3}ms)");
        }

        [Test, Performance]
        [TestCase(10, 5000)]
        [TestCase(50, 25000)]
        [TestCase(100, 50000)]
        public void SnapshotBlobSize_EntityCount(int entityCount, int maxBytes)
        {
            var state = InputScenarioBuilder.BuildAuthoritative(20);
            using var host = MockSimulationFactory.Create(state, entityCount: entityCount,
                maxPredictionSteps: 0);

            for (int i = 0; i < 10; i++)
                host.Tick();

            var step = host.CompletedSimStep;
            Assert.IsTrue(host.Snapshots.TryGetSnapshotBlobForCompletedStep(step, out var blob),
                "Failed to get snapshot blob");

            Measure.Custom(new SampleGroup("SnapshotBytes", SampleUnit.Byte), blob.Length);

            Assert.Less(blob.Length, maxBytes,
                $"Snapshot for {entityCount} entities exceeded {maxBytes} byte budget (was {blob.Length} bytes)");
        }

        [Test, Performance]
        public void SnapshotCapture_50Entities_AllTypes()
        {
            var state = InputScenarioBuilder.BuildAuthoritative(100);
            using var host = MockSimulationFactory.Create(state, entityCount: 50,
                maxPredictionSteps: 0);

            for (int i = 0; i < 10; i++)
                host.Tick();

            var step = host.CompletedSimStep;

            Measure.Method(() =>
            {
                step++;
                host.Snapshots.RecordSnapshotAfterCompletedStep(host.EntityManager, step);
            })
            .WarmupCount(5)
            .MeasurementCount(100)
            .Run();

            var mean = PerformanceTest.Active.SampleGroups[0].Median;
            Assert.Less(mean, 5.0,
                $"Baseline snapshot capture exceeded 5ms budget (was {mean:F3}ms)");
        }

        [Test, Performance]
        public void SimulationStep_50Entities_NoRollback()
        {
            var state = InputScenarioBuilder.BuildAuthoritative(200);
            using var host = MockSimulationFactory.Create(state, entityCount: 50,
                maxPredictionSteps: 0);

            Measure.Method(() =>
            {
                host.Tick();
            })
            .WarmupCount(10)
            .MeasurementCount(100)
            .Run();

            var mean = PerformanceTest.Active.SampleGroups[0].Median;
            Assert.Less(mean, 5.0,
                $"Simulation step exceeded 5ms budget (was {mean:F3}ms)");
        }

        static StepInputs MakeStep(int step, string value)
        {
            return new StepInputs
            {
                step = step,
                inputs = new List<object>
                {
                    InputScenarioBuilder.CreateInput("player1", step, value)
                }
            };
        }
    }
}

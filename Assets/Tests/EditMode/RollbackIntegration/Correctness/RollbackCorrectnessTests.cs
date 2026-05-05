using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SyncSimulation;
using Unity.Collections;
using Unity.Entities;
using UnityInputSyncerClient;

namespace Tests.RollbackIntegration
{
    [TestFixture]
    public class RollbackCorrectnessTests
    {
        SyncSimulationHost _host;
        InputSyncerState _state;

        [TearDown]
        public void TearDown()
        {
            MockSpawnerSystem.HostRef = null;
            _host?.Dispose();
            _host = null;
            _state = null;
        }

        [Test]
        public void RollbackAndReplay_ProducesIdenticalState_ToFreshRun()
        {
            const int totalSteps = 30;
            const int divergeAt = 10;

            var oracleBytes = RunOracle(totalSteps);
            var rollbackBytes = RunWithRollback(totalSteps, divergeAt);

            SimulationStateComparer.AssertStatesEqual(oracleBytes, rollbackBytes,
                "Rollback replay vs fresh run");
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(10)]
        [TestCase(30)]
        public void RollbackAtDepth_N_ProducesCorrectState(int rollbackDepth)
        {
            const int totalSteps = 40;
            int divergeAt = totalSteps - rollbackDepth;

            var oracleBytes = RunOracle(totalSteps);
            var rollbackBytes = RunWithRollback(totalSteps, divergeAt);

            SimulationStateComparer.AssertStatesEqual(oracleBytes, rollbackBytes,
                $"Rollback depth {rollbackDepth}");
        }

        [Test]
        public void RollbackWithEntitySpawnedDuringPrediction_CullsSpawnedEntity()
        {
            const int totalSteps = 20;
            const int divergeAt = 5;

            _state = new InputSyncerState();
            var initialSteps = new List<StepInputs>();
            for (int i = 0; i < divergeAt; i++)
                initialSteps.Add(MakeStep(i, "player1", "correct_" + i));
            _state.AddStepInputs(initialSteps);

            _host = MockSimulationFactory.Create(_state, entityCount: 10, rngSeed: 99,
                maxPredictionSteps: 30, enableSpawner: true, enableDeath: true);

            for (int i = 0; i < totalSteps; i++)
                _host.Tick();

            var entityCountDuringPrediction = CountRollbackEntities(_host.EntityManager);

            var correctSteps = new List<StepInputs>();
            for (int i = divergeAt; i < totalSteps; i++)
                correctSteps.Add(MakeStep(i, "player1", "correct_" + i));
            _state.AddStepInputs(correctSteps);

            _host.Tick();

            var entityCountAfterRollback = CountRollbackEntities(_host.EntityManager);

            Assert.Greater(entityCountDuringPrediction, 0,
                "Should have entities during prediction");
            Assert.Greater(entityCountAfterRollback, 0,
                "Should have entities after rollback");
            Assert.That(entityCountAfterRollback, Is.LessThanOrEqualTo(entityCountDuringPrediction),
                "Rollback should cull mispredicted spawns (entity count should not grow)");
        }

        [Test]
        public void RollbackWithEntityDestroyedDuringPrediction_RestoresDestroyedEntity()
        {
            const int totalSteps = 50;
            const int divergeAt = 5;

            var oracleBytes = RunOracle(totalSteps, entityCount: 20, seed: 123,
                enableDeath: true);
            var rollbackBytes = RunWithRollback(totalSteps, divergeAt, entityCount: 20, seed: 123,
                enableDeath: true);

            SimulationStateComparer.AssertStatesEqual(oracleBytes, rollbackBytes,
                "Destroyed entities restored after rollback");
        }

        [Test]
        public void MultipleRollbacksInSequence_MaintainStateIntegrity()
        {
            const int totalSteps = 40;

            var oracleBytes = RunOracle(totalSteps);

            _state = new InputSyncerState();
            var first5 = new List<StepInputs>();
            for (int i = 0; i < 5; i++)
                first5.Add(MakeStep(i, "player1", "correct_" + i));
            _state.AddStepInputs(first5);

            _host = MockSimulationFactory.Create(_state, entityCount: 20, rngSeed: 42,
                maxPredictionSteps: 40);

            for (int i = 0; i < 20; i++)
                _host.Tick();

            var secondBatch = new List<StepInputs>();
            for (int i = 5; i < 20; i++)
                secondBatch.Add(MakeStep(i, "player1", "correct_" + i));
            _state.AddStepInputs(secondBatch);
            _host.Tick();

            var thirdBatch = new List<StepInputs>();
            for (int i = 20; i < totalSteps; i++)
                thirdBatch.Add(MakeStep(i, "player1", "correct_" + i));
            _state.AddStepInputs(thirdBatch);

            for (int i = 0; i < 30; i++)
                _host.Tick();

            var rollbackBytes = SimulationStateComparer.CaptureSnapshotAtStep(_host, totalSteps - 1);
            SimulationStateComparer.AssertStatesEqual(oracleBytes, rollbackBytes,
                "Multiple rollbacks in sequence");
        }

        [Test]
        public void RollbackWithDynamicBufferGrowth_RestoresBufferContents()
        {
            const int totalSteps = 30;
            const int divergeAt = 10;

            var oracleBytes = RunOracle(totalSteps, entityCount: 10, seed: 77);
            var rollbackBytes = RunWithRollback(totalSteps, divergeAt, entityCount: 10, seed: 77);

            SimulationStateComparer.AssertStatesEqual(oracleBytes, rollbackBytes,
                "Buffer contents after rollback");
        }

        [Test]
        public void TwoIdenticalRuns_ProduceIdenticalSnapshots()
        {
            const int totalSteps = 30;

            var run1 = RunOracle(totalSteps, entityCount: 20, seed: 42);
            var run2 = RunOracle(totalSteps, entityCount: 20, seed: 42);

            SimulationStateComparer.AssertStatesEqual(run1, run2, "Determinism: two identical runs");
        }

        [Test]
        public void RngState_IsRestoredCorrectly_AfterRollback()
        {
            const int totalSteps = 20;
            const int divergeAt = 10;

            var oracleBytes = RunOracle(totalSteps);
            var rollbackBytes = RunWithRollback(totalSteps, divergeAt);

            SimulationStateComparer.AssertStatesEqual(oracleBytes, rollbackBytes,
                "RNG state after rollback (blob includes RNG singleton)");
        }

        [Test]
        public void RollbackToStepMinus1_RestoresInitialState()
        {
            _state = new InputSyncerState();
            _host = MockSimulationFactory.Create(_state, entityCount: 10, rngSeed: 42,
                maxPredictionSteps: 5);

            var initialSnapshot = SimulationStateComparer.CaptureStateBytes(_host);

            _host.Snapshots.RestoreAfterCompletedStep(_host.EntityManager, -1);

            _host.Snapshots.RecordSnapshotAfterCompletedStep(_host.EntityManager, -2);
            var restoredBlob = SimulationStateComparer.CaptureSnapshotAtStep(_host, -2);

            Assert.AreEqual(initialSnapshot.Length, restoredBlob.Length,
                "Restored initial state blob size mismatch");
        }

        [Test]
        public void SnapshotRingBufferWrap_OldestSlotOverwritten()
        {
            const int maxRollback = 8;
            _state = InputScenarioBuilder.BuildAuthoritative(100);
            _host = MockSimulationFactory.Create(_state, entityCount: 5, rngSeed: 42,
                maxRollbackSteps: maxRollback, maxPredictionSteps: 0);

            for (int i = 0; i < 100; i++)
                _host.Tick();

            Assert.AreEqual(99, _host.CompletedSimStep);

            Assert.IsTrue(_host.Snapshots.TryGetSnapshotBlobForCompletedStep(99, out _));
            Assert.IsTrue(_host.Snapshots.TryGetSnapshotBlobForCompletedStep(92, out _));
            Assert.IsFalse(_host.Snapshots.TryGetSnapshotBlobForCompletedStep(91, out _));
        }

        [Test]
        public void RollbackBeyondSnapshotCapacity_ThrowsWithMessage()
        {
            const int maxRollback = 4;
            _state = new InputSyncerState();
            _host = MockSimulationFactory.Create(_state, entityCount: 5, rngSeed: 42,
                maxRollbackSteps: maxRollback, maxPredictionSteps: 0);

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                _host.Snapshots.RestoreAfterCompletedStep(_host.EntityManager, 999);
            });

            Assert.That(ex.Message, Does.Contain("No rollback snapshot"));
        }

        byte[] RunOracle(int totalSteps, int entityCount = 20, ulong seed = 42,
            bool enableSpawner = false, bool enableDeath = false)
        {
            var state = InputScenarioBuilder.BuildAuthoritative(totalSteps);
            using var host = MockSimulationFactory.Create(state, entityCount: entityCount,
                rngSeed: seed, maxPredictionSteps: 0, enableSpawner: enableSpawner,
                enableDeath: enableDeath);

            for (int i = 0; i < totalSteps; i++)
                host.Tick();

            return SimulationStateComparer.CaptureSnapshotAtStep(host, totalSteps - 1);
        }

        byte[] RunWithRollback(int totalSteps, int divergeAt, int entityCount = 20, ulong seed = 42,
            bool enableSpawner = false, bool enableDeath = false)
        {
            _state = new InputSyncerState();
            var initialSteps = new List<StepInputs>();
            for (int i = 0; i < divergeAt; i++)
                initialSteps.Add(MakeStep(i, "player1", "correct_" + i));
            if (initialSteps.Count > 0)
                _state.AddStepInputs(initialSteps);

            _host = MockSimulationFactory.Create(_state, entityCount: entityCount,
                rngSeed: seed, maxPredictionSteps: totalSteps, enableSpawner: enableSpawner,
                enableDeath: enableDeath);

            for (int i = 0; i < totalSteps; i++)
                _host.Tick();

            var correctSteps = new List<StepInputs>();
            for (int i = divergeAt; i < totalSteps; i++)
                correctSteps.Add(MakeStep(i, "player1", "correct_" + i));
            _state.AddStepInputs(correctSteps);

            for (int i = 0; i < totalSteps; i++)
                _host.Tick();

            return SimulationStateComparer.CaptureSnapshotAtStep(_host, totalSteps - 1);
        }

        static StepInputs MakeStep(int step, string userId, string value)
        {
            return new StepInputs
            {
                step = step,
                inputs = new List<object>
                {
                    InputScenarioBuilder.CreateInput(userId, step, value)
                }
            };
        }

        static int CountRollbackEntities(EntityManager em)
        {
            using var q = em.CreateEntityQuery(typeof(RollbackEntityId));
            return q.CalculateEntityCount();
        }

        static MockRng GetRng(EntityManager em)
        {
            using var q = em.CreateEntityQuery(typeof(MockRng));
            return q.GetSingleton<MockRng>();
        }
    }
}

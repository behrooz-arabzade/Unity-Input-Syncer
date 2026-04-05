using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityInputSyncerClient;

namespace SyncSimulation
{
    /// <summary>
    /// Owns the dedicated simulation <see cref="World"/>, input timeline, rollback snapshots, and manual ECS stepping.
    /// </summary>
    public sealed class SyncSimulationHost : IDisposable
    {
        readonly SyncSimulationOptions _options;
        readonly InputSyncerState _state;
        readonly InputTimeline _timeline;
        readonly RollbackSnapshotStore _snapshots;
        readonly List<object> _stepInputs = new();
        int _completedSimStep = -1;
        int _nextRollbackId = 1;
        int _stepBeingSimulated;
        Entity _singleton;
        SimulationInputBridgeSystem _bridge;
        SyncSimulationSystemGroup _simGroup;
        bool _disposed;

        public SyncSimulationHost(InputSyncerState state, SyncSimulationOptions options = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _options = options ?? new SyncSimulationOptions();
            _timeline = new InputTimeline(_state, _options.LocalUserId, _options.MaxPredictionSteps);
            _snapshots = new RollbackSnapshotStore(_options.MaxRollbackSteps);

            World = new World(_options.WorldName, WorldFlags.Game);
            _simGroup = World.CreateSystemManaged<SyncSimulationSystemGroup>();
            _bridge = World.CreateSystemManaged<SimulationInputBridgeSystem>();
            _simGroup.AddSystemToUpdateList(_bridge);
            _simGroup.SortSystems();

            _singleton = EntityManager.CreateEntity(
                typeof(SimulationSingletonTag),
                typeof(SimulationStepState));
            EntityManager.AddBuffer<JsonInputEventElement>(_singleton);

            _snapshots.RecordSnapshotAfterCompletedStep(EntityManager, -1);
            RefreshNextRollbackId();
        }

        public World World { get; }

        public EntityManager EntityManager => World.EntityManager;

        public InputTimeline Timeline => _timeline;

        public RollbackSnapshotStore Snapshots => _snapshots;

        public int CompletedSimStep => _completedSimStep;

        public Entity SimulationSingleton => _singleton;

        public SyncSimulationSystemGroup SimulationGroup => _simGroup;

        /// <summary>
        /// Adds a managed <see cref="SystemBase"/> (or other <see cref="ComponentSystemBase"/>) to the simulation group. Call <see cref="World.CreateSystemManaged{T}"/> first.
        /// </summary>
        public void AddSystemToSimulation(ComponentSystemBase system)
        {
            if (system == null) throw new ArgumentNullException(nameof(system));
            _simGroup.AddSystemToUpdateList(system);
            _simGroup.SortSystems();
        }

        /// <summary>
        /// Registers a blittable <see cref="IComponentData"/> type to include in rollback snapshots.
        /// </summary>
        public void RegisterRollbackComponent<T>() where T : unmanaged, IComponentData
        {
            _snapshots.RegisterComponent<T>();
        }

        /// <summary>
        /// Creates an entity tracked for rollback. Requires <see cref="RegisterRollbackComponent{T}"/> for all sim state on the entity.
        /// </summary>
        public Entity CreateSimEntity()
        {
            var e = EntityManager.CreateEntity();
            EntityManager.AddComponentData(e, new RollbackEntityId { Value = _nextRollbackId++ });
            EntityManager.AddComponentData(e, new SpawnedOnStep { Value = _stepBeingSimulated });
            return e;
        }

        /// <summary>
        /// Call after <see cref="InputSyncerState.AddAllStepInputs"/> so the timeline and rollback baseline stay consistent.
        /// </summary>
        public void AfterFullInputResync()
        {
            _timeline.RebuildAuthoritativeMaxFromState();
            _timeline.ClearAllPredictionHashes();
            _snapshots.RestoreAfterCompletedStep(EntityManager, -1);
            _completedSimStep = -1;
            RefreshNextRollbackId();
        }

        /// <summary>
        /// Ingests authoritative steps, handles rollback/replay, then advances simulation up to budget and prediction limits.
        /// </summary>
        public void Tick()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SyncSimulationHost));

            if (_timeline.TryIngestAuthoritativeAndDetectMisprediction(out var divergent))
            {
                _timeline.ClearPredictionTrackingFromStep(divergent);
                var restoreAfter = divergent - 1;
                _snapshots.RestoreAfterCompletedStep(EntityManager, restoreAfter);
                _completedSimStep = restoreAfter;
                RefreshNextRollbackId();
            }

            var budget = _options.MaxSimulateStepsPerTick;
            while (budget-- > 0 && _completedSimStep < _timeline.MaxAllowedSimStep)
                SimulateOneStep();
        }

        void SimulateOneStep()
        {
            var next = _completedSimStep + 1;
            _stepBeingSimulated = next;
            _timeline.BuildMergedInputs(next, _stepInputs);

            var phase = _timeline.StepUsesPrediction(next) ? SimulationPhase.Predicted : SimulationPhase.Authoritative;
            if (phase == SimulationPhase.Predicted)
            {
                var lh = _timeline.ComputeLocalHashForBuiltInputs(_stepInputs);
                _timeline.RegisterSimulatedPredictionHash(next, lh);
            }
            else
            {
                _timeline.ClearSimulatedPredictionHash(next);
            }

            _bridge.PendingInputs.Clear();
            _bridge.PendingInputs.AddRange(_stepInputs);
            _bridge.PendingStep = next;
            _bridge.PendingPhase = phase;
            _simGroup.Update();

            _snapshots.RecordSnapshotAfterCompletedStep(EntityManager, next);
            _completedSimStep = next;
        }

        void RefreshNextRollbackId()
        {
            var max = 0;
            using var q = EntityManager.CreateEntityQuery(typeof(RollbackEntityId));
            using var arr = q.ToComponentDataArray<RollbackEntityId>(Allocator.Temp);
            for (var i = 0; i < arr.Length; i++)
                max = Mathf.Max(max, arr[i].Value);
            _nextRollbackId = max + 1;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _snapshots.Dispose();
            World?.Dispose();
        }
    }
}

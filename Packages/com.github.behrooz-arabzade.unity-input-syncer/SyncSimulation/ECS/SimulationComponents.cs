using Unity.Collections;
using Unity.Entities;

namespace SyncSimulation
{
    /// <summary>
    /// Marks the singleton entity that carries per-step simulation metadata and input events.
    /// </summary>
    public struct SimulationSingletonTag : IComponentData
    {
    }

    /// <summary>
    /// Per-step state visible to simulation systems. Written by <see cref="SyncSimulationHost"/> before each group update.
    /// </summary>
    public struct SimulationStepState : IComponentData
    {
        public int CurrentStep;
        public SimulationPhase Phase;
    }

    /// <summary>
    /// Next id for <see cref="RollbackEntityId"/> on entities created via <see cref="SyncSimulationHost.CreateSimEntity"/>.
    /// Lives on the simulation singleton so Burst gameplay systems can spawn rollback-tracked entities without managed host calls.
    /// </summary>
    public struct SimulationRollbackSpawnState : IComponentData
    {
        public int NextRollbackEntityId;
    }

    public enum SimulationPhase : byte
    {
        Authoritative = 0,
        Predicted = 1
    }

    /// <summary>
    /// Stable id for rollback / restore. Assigned by <see cref="SyncSimulationHost.CreateSimEntity"/>.
    /// </summary>
    public struct RollbackEntityId : IComponentData
    {
        public int Value;
    }

    /// <summary>
    /// Simulation step index during which this entity was created. Used to cull mispredicted spawns on rollback.
    /// </summary>
    public struct SpawnedOnStep : IComponentData
    {
        public int Value;
    }

    /// <summary>
    /// One JSON-serialized input event for the current step (same shape as lockstep wire payloads). Systems parse as needed.
    /// </summary>
    public struct JsonInputEventElement : IBufferElementData
    {
        public FixedString512Bytes Json;
    }
}

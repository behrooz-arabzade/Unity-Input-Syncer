namespace SyncSimulation
{
    /// <summary>
    /// Configuration for <see cref="SyncSimulationHost"/>.
    /// </summary>
    public sealed class SyncSimulationOptions
    {
        /// <summary>Name of the dedicated ECS world.</summary>
        public string WorldName { get; set; } = "SyncSimulation";

        /// <summary>Local player id; used for prediction and misprediction detection.</summary>
        public string LocalUserId { get; set; } = "";

        /// <summary>Maximum steps to simulate ahead of the latest authoritative step (0 = strict lockstep only).</summary>
        public int MaxPredictionSteps { get; set; } = 0;

        /// <summary>Ring buffer depth for rollback snapshots (completed step indices).</summary>
        public int MaxRollbackSteps { get; set; } = 64;

        /// <summary>Caps how many simulation steps run in a single <see cref="SyncSimulationHost.Tick"/> call.</summary>
        public int MaxSimulateStepsPerTick { get; set; } = 32;
    }
}

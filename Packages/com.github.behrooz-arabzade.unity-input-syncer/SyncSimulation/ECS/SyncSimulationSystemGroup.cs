using Unity.Entities;

namespace SyncSimulation
{
    /// <summary>
    /// Root group for SyncSimulation; updated manually by <see cref="SyncSimulationHost"/> (not injected into the default player loop).
    /// </summary>
    [DisableAutoCreation]
    public partial class SyncSimulationSystemGroup : ComponentSystemGroup
    {
    }
}

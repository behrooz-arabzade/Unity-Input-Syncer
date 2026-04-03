using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace SyncSimulation
{
    /// <summary>
    /// Copies managed step inputs into <see cref="JsonInputEventElement"/> on the simulation singleton for ECS systems to consume.
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SyncSimulationSystemGroup), OrderFirst = true)]
    public partial class SimulationInputBridgeSystem : SystemBase
    {
        public readonly List<object> PendingInputs = new();
        public int PendingStep;
        public SimulationPhase PendingPhase;

        protected override void OnCreate()
        {
            RequireForUpdate<SimulationSingletonTag>();
        }

        protected override void OnUpdate()
        {
            var ent = SystemAPI.GetSingletonEntity<SimulationSingletonTag>();
            EntityManager.SetComponentData(ent,
                new SimulationStepState { CurrentStep = PendingStep, Phase = PendingPhase });

            var buffer = EntityManager.GetBuffer<JsonInputEventElement>(ent);
            buffer.Clear();
            foreach (var o in PendingInputs)
            {
                var json = StepInputJson.ToJson(o);
                var fs = new FixedString512Bytes();
                if (json.Length > fs.Capacity)
                    Debug.LogWarning(
                        $"[SyncSimulation] Input JSON length {json.Length} exceeds FixedString512 capacity; truncating.");
                fs = new FixedString512Bytes(json);
                buffer.Add(new JsonInputEventElement { Json = fs });
            }
        }
    }
}

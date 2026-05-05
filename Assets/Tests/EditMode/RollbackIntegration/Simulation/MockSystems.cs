using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SyncSimulation;

namespace Tests.RollbackIntegration
{
    [DisableAutoCreation]
    public partial class MockTickSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((ref MockRng rng) => { rng.NextULong(); }).WithoutBurst().Run();
        }
    }

    [DisableAutoCreation]
    public partial class MockAutoAttackSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var rngQuery = GetEntityQuery(typeof(MockRng));
            if (rngQuery.IsEmpty) return;
            var rngEntity = rngQuery.GetSingletonEntity();
            var rng = EntityManager.GetComponentData<MockRng>(rngEntity);

            var attackerQuery = GetEntityQuery(typeof(MockAutoAttack), typeof(MockUnitFlags), typeof(RollbackEntityId));
            var attackers = attackerQuery.ToEntityArray(Allocator.Temp);
            var attackerIds = attackerQuery.ToComponentDataArray<RollbackEntityId>(Allocator.Temp);

            var order = new NativeArray<int>(attackers.Length, Allocator.Temp);
            for (int i = 0; i < order.Length; i++) order[i] = i;
            for (int i = 0; i < order.Length - 1; i++)
                for (int j = i + 1; j < order.Length; j++)
                    if (attackerIds[order[i]].Value > attackerIds[order[j]].Value)
                        (order[i], order[j]) = (order[j], order[i]);

            for (int idx = 0; idx < order.Length; idx++)
            {
                var entity = attackers[order[idx]];
                var flags = EntityManager.GetComponentData<MockUnitFlags>(entity);
                if (flags.IsAlive == 0) continue;

                var aa = EntityManager.GetComponentData<MockAutoAttack>(entity);
                if (aa.Timer > 0)
                {
                    aa.Timer--;
                    EntityManager.SetComponentData(entity, aa);
                    continue;
                }

                aa.Timer = aa.Interval;
                EntityManager.SetComponentData(entity, aa);

                var targetQuery = GetEntityQuery(typeof(MockHealth), typeof(MockUnitFlags), typeof(RollbackEntityId));
                var targets = targetQuery.ToEntityArray(Allocator.Temp);
                var targetIds = targetQuery.ToComponentDataArray<RollbackEntityId>(Allocator.Temp);

                int bestTarget = -1;
                int bestId = int.MaxValue;
                for (int i = 0; i < targets.Length; i++)
                {
                    if (targets[i] == entity) continue;
                    var tf = EntityManager.GetComponentData<MockUnitFlags>(targets[i]);
                    if (tf.IsAlive == 0) continue;
                    if (tf.Team == flags.Team) continue;
                    if (targetIds[i].Value < bestId)
                    {
                        bestId = targetIds[i].Value;
                        bestTarget = i;
                    }
                }

                if (bestTarget >= 0 && EntityManager.HasBuffer<MockDamageEvent>(targets[bestTarget]))
                {
                    var buf = EntityManager.GetBuffer<MockDamageEvent>(targets[bestTarget]);
                    buf.Add(new MockDamageEvent
                    {
                        SourceId = entity.Index,
                        Amount = aa.Damage,
                        DmgType = 0,
                        Flags = 0
                    });
                }

                targets.Dispose();
                targetIds.Dispose();
            }

            order.Dispose();
            attackers.Dispose();
            attackerIds.Dispose();
            EntityManager.SetComponentData(rngEntity, rng);
        }
    }

    [DisableAutoCreation]
    public partial class MockDamageSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var query = GetEntityQuery(typeof(MockHealth), typeof(MockDamageEvent));
            var entities = query.ToEntityArray(Allocator.Temp);

            for (int e = 0; e < entities.Length; e++)
            {
                var entity = entities[e];
                var health = EntityManager.GetComponentData<MockHealth>(entity);
                var events = EntityManager.GetBuffer<MockDamageEvent>(entity);

                for (int i = 0; i < events.Length; i++)
                    health.Current -= events[i].Amount;
                events.Clear();

                if (health.Current < 0) health.Current = 0;
                EntityManager.SetComponentData(entity, health);
            }

            entities.Dispose();
        }
    }

    [DisableAutoCreation]
    public partial class MockSpawnerSystem : SystemBase
    {
        public static SyncSimulationHost HostRef;

        protected override void OnUpdate()
        {
            if (HostRef == null) return;

            var rngQuery = GetEntityQuery(typeof(MockRng));
            if (rngQuery.IsEmpty) return;
            var rngEntity = rngQuery.GetSingletonEntity();
            var rng = EntityManager.GetComponentData<MockRng>(rngEntity);

            var chance = rng.NextFloat();
            EntityManager.SetComponentData(rngEntity, rng);

            if (chance < 0.05f)
            {
                var spawned = HostRef.CreateSimEntity();
                EntityManager.AddComponentData(spawned, new MockSpawnedTag { Type = 1, LifetimeTicks = 10 });
                EntityManager.AddComponentData(spawned, new MockHealth { Current = 50f, Max = 50f });
                EntityManager.AddComponentData(spawned, new MockPosition { Value = new float2(rng.NextFloat() * 40f, rng.NextFloat() * 40f) });
                EntityManager.AddComponentData(spawned, new MockUnitFlags { IsAlive = 1, Team = 2, OwnerSlot = 0 });
                EntityManager.AddComponentData(spawned, new MockBaseStats());
                EntityManager.AddComponentData(spawned, new MockCurrentStats());
                EntityManager.AddComponentData(spawned, new MockMoveState());
                EntityManager.AddComponentData(spawned, new MockAutoAttack());
                EntityManager.AddComponentData(spawned, new MockCooldownState());
                EntityManager.AddComponentData(spawned, new MockCastState());
                EntityManager.AddComponentData(spawned, new MockResource());
                EntityManager.AddComponentData(spawned, new MockDashEnergy());
                EntityManager.AddBuffer<MockDamageEvent>(spawned);
                EntityManager.AddBuffer<MockBuffEntry>(spawned);
                EntityManager.AddBuffer<MockAbilitySlot>(spawned);
                EntityManager.SetComponentData(rngEntity, rng);
            }
        }
    }

    [DisableAutoCreation]
    public partial class MockDeathSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            var query = GetEntityQuery(typeof(MockHealth), typeof(MockUnitFlags));
            var entities = query.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var flags = EntityManager.GetComponentData<MockUnitFlags>(entity);
                var health = EntityManager.GetComponentData<MockHealth>(entity);
                if (flags.IsAlive == 1 && health.Current <= 0f)
                {
                    flags.IsAlive = 0;
                    EntityManager.SetComponentData(entity, flags);
                    ecb.DestroyEntity(entity);
                }
            }
            entities.Dispose();

            var spawnedQuery = GetEntityQuery(typeof(MockSpawnedTag));
            var spawnedEntities = spawnedQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < spawnedEntities.Length; i++)
            {
                var entity = spawnedEntities[i];
                var spawned = EntityManager.GetComponentData<MockSpawnedTag>(entity);
                if (spawned.LifetimeTicks == 0)
                {
                    ecb.DestroyEntity(entity);
                }
                else
                {
                    spawned.LifetimeTicks--;
                    EntityManager.SetComponentData(entity, spawned);
                }
            }
            spawnedEntities.Dispose();

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }
    }
}

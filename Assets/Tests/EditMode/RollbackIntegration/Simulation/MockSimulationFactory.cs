using Unity.Entities;
using Unity.Mathematics;
using SyncSimulation;
using UnityInputSyncerClient;

namespace Tests.RollbackIntegration
{
    public static class MockSimulationFactory
    {
        public static SyncSimulationHost Create(
            InputSyncerState state,
            int entityCount = 20,
            int maxRollbackSteps = 64,
            int maxPredictionSteps = 30,
            ulong rngSeed = 42,
            string localUserId = "player1",
            bool enableSpawner = false,
            bool enableDeath = false)
        {
            var options = new SyncSimulationOptions
            {
                LocalUserId = localUserId,
                MaxRollbackSteps = maxRollbackSteps,
                MaxPredictionSteps = maxPredictionSteps,
            };

            var host = new SyncSimulationHost(state, options);

            host.RegisterRollbackComponent<MockHealth>();
            host.RegisterRollbackComponent<MockPosition>();
            host.RegisterRollbackComponent<MockBaseStats>();
            host.RegisterRollbackComponent<MockCurrentStats>();
            host.RegisterRollbackComponent<MockMoveState>();
            host.RegisterRollbackComponent<MockAutoAttack>();
            host.RegisterRollbackComponent<MockCooldownState>();
            host.RegisterRollbackComponent<MockCastState>();
            host.RegisterRollbackComponent<MockRng>();
            host.RegisterRollbackComponent<MockUnitFlags>();
            host.RegisterRollbackComponent<MockResource>();
            host.RegisterRollbackComponent<MockDashEnergy>();
            host.RegisterRollbackComponent<MockSpawnedTag>();

            host.RegisterRollbackBuffer<MockBuffEntry>();
            host.RegisterRollbackBuffer<MockDamageEvent>();
            host.RegisterRollbackBuffer<MockAbilitySlot>();

            var tickSys = host.World.CreateSystemManaged<MockTickSystem>();
            var autoAtkSys = host.World.CreateSystemManaged<MockAutoAttackSystem>();
            var dmgSys = host.World.CreateSystemManaged<MockDamageSystem>();
            var deathSys = host.World.CreateSystemManaged<MockDeathSystem>();

            host.AddSystemToSimulation(tickSys);
            host.AddSystemToSimulation(autoAtkSys);
            host.AddSystemToSimulation(dmgSys);

            if (enableSpawner)
            {
                var spawnerSys = host.World.CreateSystemManaged<MockSpawnerSystem>();
                host.AddSystemToSimulation(spawnerSys);
                MockSpawnerSystem.HostRef = host;
            }

            if (enableDeath)
            {
                host.AddSystemToSimulation(deathSys);
            }

            var rngEntity = host.CreateSimEntity();
            host.EntityManager.AddComponentData(rngEntity, MockRng.Create(rngSeed));

            for (int i = 0; i < entityCount; i++)
            {
                var entity = host.CreateSimEntity();
                byte team = (byte)(i % 2);
                var initRng = MockRng.Create(rngSeed + (ulong)(i + 1));

                host.EntityManager.AddComponentData(entity, new MockHealth
                {
                    Current = 1000f + initRng.NextFloat() * 500f,
                    Max = 1500f
                });
                host.EntityManager.AddComponentData(entity, new MockPosition
                {
                    Value = new float2(initRng.NextFloat() * 40f, initRng.NextFloat() * 40f)
                });
                host.EntityManager.AddComponentData(entity, new MockBaseStats
                {
                    Str = 10 + initRng.NextFloat() * 20,
                    Agi = 10 + initRng.NextFloat() * 20,
                    Int = 10 + initRng.NextFloat() * 20,
                    Sta = 10 + initRng.NextFloat() * 20,
                    Armor = 5 + initRng.NextFloat() * 10,
                    MagicResist = 5 + initRng.NextFloat() * 10,
                    CritChance = 0.05f,
                    CritDamage = 1.5f,
                    Dodge = 0.03f,
                    Lifesteal = 0f,
                    Haste = 0f,
                    DmgDealt = 1f,
                    DmgTaken = 1f,
                    HealDone = 1f,
                    HealRecv = 1f,
                });
                host.EntityManager.AddComponentData(entity, new MockCurrentStats
                {
                    Str = 10, Agi = 10, Int = 10, Sta = 10,
                    Armor = 5, MagicResist = 5,
                    CritChance = 0.05f, CritDamage = 1.5f,
                    Dodge = 0.03f, Lifesteal = 0f, Haste = 0f,
                    DmgDealt = 1f, DmgTaken = 1f, HealDone = 1f, HealRecv = 1f,
                });
                host.EntityManager.AddComponentData(entity, new MockMoveState
                {
                    Target = new float2(20, 20),
                    Speed = 3f,
                    Moving = 0
                });
                host.EntityManager.AddComponentData(entity, new MockAutoAttack
                {
                    Timer = (ushort)(i * 2),
                    Interval = 10,
                    Damage = 50f + initRng.NextFloat() * 30f
                });
                host.EntityManager.AddComponentData(entity, new MockCooldownState());
                host.EntityManager.AddComponentData(entity, new MockCastState());
                host.EntityManager.AddComponentData(entity, new MockUnitFlags
                {
                    IsAlive = 1,
                    Team = team,
                    OwnerSlot = (byte)i
                });
                host.EntityManager.AddComponentData(entity, new MockResource
                {
                    Current = 100f,
                    Max = 100f,
                    Regen = 2f
                });
                host.EntityManager.AddComponentData(entity, new MockDashEnergy
                {
                    Current = 50f
                });

                host.EntityManager.AddBuffer<MockDamageEvent>(entity);
                host.EntityManager.AddBuffer<MockBuffEntry>(entity);

                var abilityBuf = host.EntityManager.AddBuffer<MockAbilitySlot>(entity);
                abilityBuf.Add(new MockAbilitySlot { AbilityId = (ushort)(100 + i * 3), Cooldown = 0 });
                abilityBuf.Add(new MockAbilitySlot { AbilityId = (ushort)(101 + i * 3), Cooldown = 0 });
                abilityBuf.Add(new MockAbilitySlot { AbilityId = (ushort)(102 + i * 3), Cooldown = 0 });
            }

            return host;
        }
    }
}

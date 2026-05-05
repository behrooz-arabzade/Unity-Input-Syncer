using Unity.Entities;
using Unity.Mathematics;

namespace Tests.RollbackIntegration
{
    public struct MockHealth : IComponentData
    {
        public float Current;
        public float Max;
    }

    public struct MockPosition : IComponentData
    {
        public float2 Value;
    }

    public struct MockBaseStats : IComponentData
    {
        public float Str;
        public float Agi;
        public float Int;
        public float Sta;
        public float Armor;
        public float MagicResist;
        public float CritChance;
        public float CritDamage;
        public float Dodge;
        public float Lifesteal;
        public float Haste;
        public float DmgDealt;
        public float DmgTaken;
        public float HealDone;
        public float HealRecv;
    }

    public struct MockCurrentStats : IComponentData
    {
        public float Str;
        public float Agi;
        public float Int;
        public float Sta;
        public float Armor;
        public float MagicResist;
        public float CritChance;
        public float CritDamage;
        public float Dodge;
        public float Lifesteal;
        public float Haste;
        public float DmgDealt;
        public float DmgTaken;
        public float HealDone;
        public float HealRecv;
    }

    public struct MockMoveState : IComponentData
    {
        public float2 Target;
        public float Speed;
        public byte Moving;
    }

    public struct MockAutoAttack : IComponentData
    {
        public ushort Timer;
        public ushort Interval;
        public float Damage;
    }

    public struct MockCooldownState : IComponentData
    {
        public ushort GCD;
        public ushort Slot0CD;
        public ushort Slot1CD;
        public ushort Slot2CD;
    }

    public struct MockCastState : IComponentData
    {
        public ushort RemainingTicks;
        public ushort AbilityId;
        public byte IsCasting;
    }

    public struct MockRng : IComponentData
    {
        public ulong S0;
        public ulong S1;

        public ulong NextULong()
        {
            var s0 = S0;
            var s1 = S1;
            var result = s0 + s1;
            s1 ^= s0;
            S0 = ((s0 << 55) | (s0 >> 9)) ^ s1 ^ (s1 << 14);
            S1 = (s1 << 36) | (s1 >> 28);
            return result;
        }

        public float NextFloat()
        {
            return (NextULong() >> 40) * (1.0f / 16777216.0f);
        }

        public static MockRng Create(ulong seed)
        {
            if (seed == 0) seed = 1;
            return new MockRng { S0 = seed, S1 = seed ^ 0xBEEFCAFEul };
        }
    }

    public struct MockUnitFlags : IComponentData
    {
        public byte IsAlive;
        public byte Team;
        public byte OwnerSlot;
    }

    public struct MockResource : IComponentData
    {
        public float Current;
        public float Max;
        public float Regen;
    }

    public struct MockDashEnergy : IComponentData
    {
        public float Current;
    }

    public struct MockSpawnedTag : IComponentData
    {
        public byte Type;
        public ushort LifetimeTicks;
    }

    public struct MockBuffEntry : IBufferElementData
    {
        public ushort BuffId;
        public ushort Duration;
        public float Power;
        public byte Stacks;
    }

    public struct MockDamageEvent : IBufferElementData
    {
        public int SourceId;
        public float Amount;
        public byte DmgType;
        public byte Flags;
    }

    public struct MockAbilitySlot : IBufferElementData
    {
        public ushort AbilityId;
        public ushort Cooldown;
    }
}

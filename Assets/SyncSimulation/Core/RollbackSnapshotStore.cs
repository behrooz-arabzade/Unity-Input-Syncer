using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace SyncSimulation
{
    interface IRollbackComponentOps
    {
        ComponentType Type { get; }
        bool HasComponent(EntityManager em, Entity e);
        int SizeOf { get; }
        void Append(EntityManager em, Entity e, NativeList<byte> blob);
        void RestoreOrAdd(EntityManager em, Entity e, ref int offset, NativeArray<byte> blob);
        void RemoveIfPresent(EntityManager em, Entity e);
    }

    sealed class RollbackOps<T> : IRollbackComponentOps where T : unmanaged, IComponentData
    {
        public ComponentType Type => ComponentType.ReadWrite<T>();

        public int SizeOf => UnsafeUtility.SizeOf<T>();

        public bool HasComponent(EntityManager em, Entity e) => em.HasComponent<T>(e);

        public void Append(EntityManager em, Entity e, NativeList<byte> blob)
        {
            var data = em.GetComponentData<T>(e);
            unsafe
            {
                var p = (byte*)UnsafeUtility.AddressOf(ref data);
                for (var i = 0; i < SizeOf; i++)
                    blob.Add(p[i]);
            }
        }

        public void RestoreOrAdd(EntityManager em, Entity e, ref int offset, NativeArray<byte> blob)
        {
            if (offset + SizeOf > blob.Length)
                throw new InvalidOperationException("Rollback snapshot truncated.");
            unsafe
            {
                var src = (byte*)blob.GetUnsafeReadOnlyPtr() + offset;
                T data = default;
                UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref data), src, SizeOf);
                offset += SizeOf;
                if (em.HasComponent<T>(e))
                    em.SetComponentData(e, data);
                else
                    em.AddComponentData(e, data);
            }
        }

        public void RemoveIfPresent(EntityManager em, Entity e)
        {
            if (em.HasComponent<T>(e))
                em.RemoveComponent<T>(e);
        }
    }

    /// <summary>
    /// Ring buffer of per-step ECS snapshots for registered blittable components plus rollback ids / spawn steps.
    /// </summary>
    public sealed class RollbackSnapshotStore : IDisposable
    {
        readonly List<IRollbackComponentOps> _ops = new();
        readonly int _capacity;
        readonly NativeList<byte>[] _blobs;
        readonly int[] _blobStep;
        bool _disposed;

        public RollbackSnapshotStore(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _blobs = new NativeList<byte>[_capacity];
            _blobStep = new int[_capacity];
            for (var i = 0; i < _capacity; i++)
                _blobStep[i] = -1;
        }

        public void RegisterComponent<T>() where T : unmanaged, IComponentData
        {
            _ops.Add(new RollbackOps<T>());
        }

        int SlotForStep(int completedStep)
        {
            var r = completedStep % _capacity;
            return r < 0 ? r + _capacity : r;
        }

        public void RecordSnapshotAfterCompletedStep(EntityManager em, int completedStep)
        {
            var blob = new NativeList<byte>(Allocator.Persistent);
            try
            {
                Capture(em, blob);
            }
            catch
            {
                blob.Dispose();
                throw;
            }

            var slot = SlotForStep(completedStep);
            if (_blobs[slot].IsCreated)
                _blobs[slot].Dispose();
            _blobs[slot] = blob;
            _blobStep[slot] = completedStep;
        }

        public bool TryGetSnapshotBlobForCompletedStep(int completedStep, out NativeArray<byte> bytes)
        {
            var slot = SlotForStep(completedStep);
            if (!_blobs[slot].IsCreated || _blobStep[slot] != completedStep)
            {
                bytes = default;
                return false;
            }

            bytes = _blobs[slot].AsArray();
            return true;
        }

        /// <summary>
        /// Restores rollback state after the given completed step, then culls entities spawned strictly after that step.
        /// </summary>
        public void RestoreAfterCompletedStep(EntityManager em, int completedStep)
        {
            if (!TryGetSnapshotBlobForCompletedStep(completedStep, out var arr))
                throw new InvalidOperationException(
                    $"No rollback snapshot for completed step {completedStep}. Increase {nameof(SyncSimulationOptions.MaxRollbackSteps)} or reduce prediction depth.");

            Restore(em, arr);
            CullEntitiesSpawnedAfter(em, completedStep);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var b in _blobs)
            {
                if (b.IsCreated)
                    b.Dispose();
            }
        }

        void Capture(EntityManager em, NativeList<byte> blob)
        {
            using var q = em.CreateEntityQuery(typeof(RollbackEntityId), typeof(SpawnedOnStep));
            var entities = q.ToEntityArray(Allocator.Temp);
            var ids = q.ToComponentDataArray<RollbackEntityId>(Allocator.Temp);
            var spawns = q.ToComponentDataArray<SpawnedOnStep>(Allocator.Temp);

            var order = Enumerable.Range(0, entities.Length).OrderBy(i => ids[i].Value).ToArray();

            var count = order.Length;
            AppendInt(blob, count);

            foreach (var idx in order)
            {
                var e = entities[idx];
                var id = ids[idx].Value;
                var sp = spawns[idx].Value;
                AppendInt(blob, id);
                AppendInt(blob, sp);

                uint mask = 0;
                for (var oi = 0; oi < _ops.Count; oi++)
                {
                    if (_ops[oi].HasComponent(em, e))
                        mask |= 1u << oi;
                }

                AppendUInt(blob, mask);

                for (var oi = 0; oi < _ops.Count; oi++)
                {
                    if ((mask & (1u << oi)) != 0)
                        _ops[oi].Append(em, e, blob);
                }
            }
        }

        void Restore(EntityManager em, NativeArray<byte> blob)
        {
            var offset = 0;
            var count = ReadInt(blob, ref offset);
            var records = new List<(int id, int spawned, uint mask, int payloadStart)>(count);

            for (var i = 0; i < count; i++)
            {
                var id = ReadInt(blob, ref offset);
                var spawned = ReadInt(blob, ref offset);
                var mask = ReadUInt(blob, ref offset);
                var payloadStart = offset;
                for (var oi = 0; oi < _ops.Count; oi++)
                {
                    if ((mask & (1u << oi)) != 0)
                        offset += _ops[oi].SizeOf;
                }

                records.Add((id, spawned, mask, payloadStart));
            }

            var idSet = new HashSet<int>(records.Select(r => r.id));
            using (var q = em.CreateEntityQuery(typeof(RollbackEntityId)))
            {
                var existingEntities = q.ToEntityArray(Allocator.Temp);
                var existingIds = q.ToComponentDataArray<RollbackEntityId>(Allocator.Temp);
                for (var i = 0; i < existingEntities.Length; i++)
                {
                    if (!idSet.Contains(existingIds[i].Value))
                        em.DestroyEntity(existingEntities[i]);
                }
            }

            var map = new Dictionary<int, Entity>();
            using (var q2 = em.CreateEntityQuery(typeof(RollbackEntityId)))
            {
                var ents = q2.ToEntityArray(Allocator.Temp);
                var ids = q2.ToComponentDataArray<RollbackEntityId>(Allocator.Temp);
                for (var i = 0; i < ents.Length; i++)
                    map[ids[i].Value] = ents[i];
            }

            foreach (var rec in records)
            {
                if (!map.TryGetValue(rec.id, out var entity))
                {
                    entity = em.CreateEntity();
                    em.AddComponentData(entity, new RollbackEntityId { Value = rec.id });
                    em.AddComponentData(entity, new SpawnedOnStep { Value = rec.spawned });
                    map[rec.id] = entity;
                }
                else
                {
                    em.SetComponentData(entity, new RollbackEntityId { Value = rec.id });
                    em.SetComponentData(entity, new SpawnedOnStep { Value = rec.spawned });
                }

                for (var oi = 0; oi < _ops.Count; oi++)
                {
                    if ((rec.mask & (1u << oi)) == 0)
                        _ops[oi].RemoveIfPresent(em, entity);
                }

                var off = rec.payloadStart;
                for (var oi = 0; oi < _ops.Count; oi++)
                {
                    if ((rec.mask & (1u << oi)) != 0)
                        _ops[oi].RestoreOrAdd(em, entity, ref off, blob);
                }
            }
        }

        static void CullEntitiesSpawnedAfter(EntityManager em, int completedStep)
        {
            using var q = em.CreateEntityQuery(typeof(RollbackEntityId), typeof(SpawnedOnStep));
            using var e = q.ToEntityArray(Allocator.Temp);
            using var sp = q.ToComponentDataArray<SpawnedOnStep>(Allocator.Temp);
            for (var i = 0; i < e.Length; i++)
            {
                if (sp[i].Value > completedStep)
                    em.DestroyEntity(e[i]);
            }
        }

        static void AppendInt(NativeList<byte> blob, int v)
        {
            blob.Add((byte)(v & 0xFF));
            blob.Add((byte)((v >> 8) & 0xFF));
            blob.Add((byte)((v >> 16) & 0xFF));
            blob.Add((byte)((v >> 24) & 0xFF));
        }

        static void AppendUInt(NativeList<byte> blob, uint v)
        {
            blob.Add((byte)(v & 0xFF));
            blob.Add((byte)((v >> 8) & 0xFF));
            blob.Add((byte)((v >> 16) & 0xFF));
            blob.Add((byte)((v >> 24) & 0xFF));
        }

        static int ReadInt(NativeArray<byte> blob, ref int offset)
        {
            var u = ReadUInt(blob, ref offset);
            return unchecked((int)u);
        }

        static uint ReadUInt(NativeArray<byte> blob, ref int offset)
        {
            if (offset + 4 > blob.Length)
                throw new InvalidOperationException("Rollback snapshot corrupt.");
            uint v = blob[offset] | ((uint)blob[offset + 1] << 8) | ((uint)blob[offset + 2] << 16) | ((uint)blob[offset + 3] << 24);
            offset += 4;
            return v;
        }
    }
}

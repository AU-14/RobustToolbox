using System;
using System.Collections.Generic;
using System.Threading; // CMU14
using Microsoft.Extensions.ObjectPool;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Utility;
using SharpZstd.Interop;

namespace Robust.Server.GameStates;

// This partial class contains code for pooling objects to avoid allocations.
// This file is now blessedly small, so maybe we can just get rid of it.
internal sealed partial class PvsSystem
{
    /// <summary>
    /// Maximum number of pooled objects.
    /// </summary>
    private const int MaxVisPoolSize = 1024;

    private readonly ObjectPool<List<PvsIndex>> _entDataListPool
        = new DefaultObjectPool<List<PvsIndex>>(new ListPolicy<PvsIndex>(), MaxVisPoolSize);

    // CMU14 Begin: state generation and buffer return run on the same serialization worker.
    // Retain small buffers locally so players do not contend for one global queue.
    private const int MaxPooledComponentChangeCapacity = 64;
    private readonly ThreadLocal<Stack<List<ComponentChange>>> _componentChangeLists
        = new(static () => new());
    // CMU14 End

    private readonly ObjectPool<HashSet<EntityUid>> _uidSetPool
        = new DefaultObjectPool<HashSet<EntityUid>>(new SetPolicy<EntityUid>(), MaxVisPoolSize);

    private readonly ObjectPool<HashSet<ushort>> _netComponentSetPool
        = new DefaultObjectPool<HashSet<ushort>>(new SetPolicy<ushort>(), MaxVisPoolSize);

    private readonly ObjectPool<PvsChunk> _chunkPool =
        new DefaultObjectPool<PvsChunk>(new PvsChunkPolicy(), 256);

    private sealed class PvsChunkPolicy : PooledObjectPolicy<PvsChunk>
    {
        public override PvsChunk Create()
        {
            return new PvsChunk();
        }

        public override bool Return(PvsChunk obj)
        {
            obj.Wipe();
            return true;
        }
    }

    private sealed class PvsThreadResourcesObjectPolicy(int ce) : IPooledObjectPolicy<PvsThreadResources>
    {
        PvsThreadResources IPooledObjectPolicy<PvsThreadResources>.Create()
        {
            var res = new PvsThreadResources();
            res.CompressionContext.SetParameter(ZSTD_cParameter.ZSTD_c_compressionLevel, ce);
            return res;
        }

        bool IPooledObjectPolicy<PvsThreadResources>.Return(PvsThreadResources _)
        {
            return true;
        }
    }

    private sealed class PvsThreadResources
    {
        public ZStdCompressionContext CompressionContext = new();

        ~PvsThreadResources()
        {
            CompressionContext.Dispose();
        }
    }

    // CMU14 method: each worker has a bounded cache of cleared buffers, never component states.
    private List<ComponentChange> GetComponentChangeList(int capacity)
    {
        var pool = _componentChangeLists.Value!;
        var list = pool.TryPop(out var cached) ? cached : new List<ComponentChange>();
        list.EnsureCapacity(capacity);
        return list;
    }

    // CMU14 method
    private void ReturnComponentChangeList(List<ComponentChange> changes)
    {
        changes.Clear();
        var pool = _componentChangeLists.Value!;
        // Large initial/full-state buffers must not multiply retained memory by worker count.
        if (pool.Count < MaxVisPoolSize && changes.Capacity <= MaxPooledComponentChangeCapacity)
            pool.Push(changes);
    }

    private HashSet<ushort> GetNetComponentSet()
    {
        return _netComponentSetPool.Get();
    }

    private void ReturnEntityState(EntityState state)
    {
        if (state.ComponentChanges.Value is List<ComponentChange> changes)
            ReturnComponentChangeList(changes); // CMU14

        if (state.NetComponents is { } netComps)
        {
            _netComponentSetPool.Return(netComps);
            state.NetComponents = null;
        }
    }

    internal void ClearSessionState(PvsSession session)
    {
        foreach (var state in session.States)
        {
            ReturnEntityState(state);
        }

        session.ClearState();
    }
}

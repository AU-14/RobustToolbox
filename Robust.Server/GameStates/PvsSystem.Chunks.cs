using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Prometheus;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Enumerators;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Robust.Server.GameStates;

// Partial class for handling PVS chunks.
internal sealed partial class PvsSystem
{
    public const float ChunkSize = ChunkEntitySystem.ChunkSize;

    private readonly Dictionary<PvsChunkLocation, PvsChunk> _chunks = new();
    private readonly List<PvsChunk> _dirtyChunks = new(64);
    private readonly List<PvsChunk> _cleanChunks = new(64);

    // CMU14 Begin: reuse identical map/grid searches only within one visibility pass.
    private const int MaxVisibleChunkRegions = 16;
    private const int MaxCachedRegionChunks = 256;
    private readonly Dictionary<(EntityUid Root, Vector2i Lower, Vector2i Upper), List<PvsChunk>> _visibleChunkRegions = new();
    private readonly List<List<PvsChunk>> _visibleChunkLists = new();
    private bool _cacheVisibleChunkRegions;
    // CMU14 End

    /// <summary>
    /// Lookup of every chunk entity uid to its corresponding pvschunklocation.
    /// </summary>
    private readonly Dictionary<EntityUid, PvsChunkLocation> _chunkEntityLocations = new();

    // Store chunks grouped by the root node, for when maps/grids get deleted.
    private readonly Dictionary<EntityUid, HashSet<PvsChunkLocation>> _chunkSets = new();

    private List<Entity<MapGridComponent>> _grids = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2i GetChunkIndices(Vector2 coordinates) => (coordinates / ChunkSize).Floored();

    /// <summary>
    /// Iterate over all visible chunks and, if necessary, re-construct their list of entities.
    /// </summary>
    private void UpdateDirtyChunks(int index)
    {
        var chunk = _dirtyChunks[index];
        DebugTools.Assert(chunk.Dirty);
        DebugTools.Assert(chunk.UpdateQueued);

        if (!chunk.PopulateContents(_metaQuery, _xformQuery))
            return; // Failed to populate a dirty chunk.

        UpdateChunkPosition(chunk);
    }

    private void UpdateCleanChunks()
    {
        foreach (var chunk in CollectionsMarshal.AsSpan(_cleanChunks))
        {
            UpdateChunkPosition(chunk);
        }
    }

    /// <summary>
    /// Update a chunk's world position. This is used to prioritize sending chunks that a closer to players.
    /// </summary>
    private void UpdateChunkPosition(PvsChunk chunk)
    {
        if (chunk.Root.Comp == null
            || chunk.Map.Comp == null
            || chunk.Root.Comp.EntityLifeStage >= EntityLifeStage.Terminating
            || chunk.Map.Comp.EntityLifeStage >= EntityLifeStage.Terminating)
        {
            Log.Error($"Encountered deleted root while updating pvs chunk positions. Root: {ToPrettyString(chunk.Root, chunk.Root)}. Map: {ToPrettyString(chunk.Map, chunk.Map)}" );
            return;
        }

        var xform = Transform(chunk.Root);
        DebugTools.AssertEqual(chunk.Map.Owner, xform.MapUid);
        chunk.InvWorldMatrix = xform.InvLocalMatrix;
        var worldPos = Vector2.Transform(chunk.Centre, xform.LocalMatrix);
        chunk.Position = new(worldPos, xform.MapID);
        chunk.UpdateQueued = false;
    }

    /// <summary>
    /// Update the list of all currently visible chunks.
    /// </summary>
    internal void GetVisibleChunks()
    {
        using var _= Histogram.WithLabels("Get Chunks").NewTimer();

        DebugTools.Assert(!_chunks.Values.Any(x=> x.UpdateQueued));
        _dirtyChunks.Clear();
        _cleanChunks.Clear();
        // CMU14 Begin: cached chunks must not survive entity/grid changes between ticks.
        _cacheVisibleChunkRegions = _sessions.Length > 1;
        try
        {
            foreach (var session in _sessions)
            {
                session.Chunks.Clear();
                session.ChunkSet.Clear();
                GetSessionViewers(session);

                foreach (var eye in session.Viewers)
                {
                    GetVisibleChunks(eye, session.ChunkSet);
                }
            }
        }
        finally
        {
            foreach (var list in _visibleChunkRegions.Values)
                list.Clear();
            _visibleChunkRegions.Clear();
        }
        // CMU14 End
        DebugTools.Assert(_dirtyChunks.ToHashSet().Count == _dirtyChunks.Count);
        DebugTools.Assert(_cleanChunks.ToHashSet().Count == _cleanChunks.Count);
    }

    /// <summary>
    /// Get the chunks visible to a single entity and add them to a player's set of visible chunks.
    /// </summary>
    // CMU14: share exact chunk searches while keeping each viewer's grid intersection and bounds.
    private void GetVisibleChunks(Entity<TransformComponent, EyeComponent?> eye,
        HashSet<PvsChunk> chunks)
    {
        var (viewPos, range, mapUid) = CalcViewBounds(eye);
        if (mapUid is not {} map)
            return;

        AddVisibleChunks(map, viewPos, range, chunks);

        _grids.Clear();
        var rangeVec = new Vector2(range, range);
        var box = new Box2(viewPos - rangeVec, viewPos + rangeVec);
        _maps.FindGridsIntersecting(map, box, ref _grids, approx: true, includeMap: false);

        foreach (var (grid, _) in _grids)
        {
            var localPos = Vector2.Transform(viewPos, _transform.GetInvWorldMatrix(grid));
            AddVisibleChunks(grid, localPos, range, chunks);
        }
    }

    // CMU14: preserve the enumerator's inclusive bounds and order, including empty regions.
    private void AddVisibleChunks(EntityUid root, Vector2 position, float range, HashSet<PvsChunk> chunks)
    {
        List<PvsChunk>? cached = null;
        if (_cacheVisibleChunkRegions)
        {
            var rangeVec = new Vector2(range, range);
            var lower = ((position - rangeVec) / ChunkSize).Floored();
            var upper = ((position + rangeVec) / ChunkSize).Floored();
            var key = (root, lower, upper);
            if (_visibleChunkRegions.TryGetValue(key, out cached))
            {
                foreach (var chunk in cached)
                    chunks.Add(chunk);
                return;
            }

            // Bound retained scratch storage even with many remote eyes or unusually large view ranges.
            var width = (long) upper.X - lower.X + 1;
            var height = (long) upper.Y - lower.Y + 1;
            if (_visibleChunkRegions.Count < MaxVisibleChunkRegions &&
                width > 0 && height > 0 && width <= MaxCachedRegionChunks &&
                height <= MaxCachedRegionChunks && width * height <= MaxCachedRegionChunks)
            {
                var index = _visibleChunkRegions.Count;
                if (index == _visibleChunkLists.Count)
                    _visibleChunkLists.Add(new List<PvsChunk>());
                cached = _visibleChunkLists[index];
                _visibleChunkRegions.Add(key, cached);
            }
            else
            {
                // Dispersed viewers should not pay cache lookup costs for the rest of this pass.
                _cacheVisibleChunkRegions = false;
            }
        }

        var enumerator = new ChunkIndicesEnumerator(position, range, ChunkSize);
        while (enumerator.MoveNext(out var indices))
        {
            if (!_chunks.TryGetValue(new PvsChunkLocation(root, indices.Value), out var chunk))
                continue;

            cached?.Add(chunk);
            chunks.Add(chunk);
            if (chunk.UpdateQueued)
                continue;

            chunk.UpdateQueued = true;
            if (chunk.Dirty)
                _dirtyChunks.Add(chunk);
            else
                _cleanChunks.Add(chunk);
        }
    }

    /// <summary>
    /// Get all viewers for a given session. This is required to get a list of visible chunks.
    /// </summary>
    private void GetSessionViewers(PvsSession pvsSession)
    {
        var session = pvsSession.Session;
        if (session.Status != SessionStatus.InGame)
        {
            pvsSession.Viewers = Array.Empty<Entity<TransformComponent, EyeComponent?>>();
            return;
        }

        // The majority of players will have no view subscriptions
        if (session.ViewSubscriptions.Count == 0)
        {
            if (session.AttachedEntity is not {} attached)
            {
                pvsSession.Viewers = Array.Empty<Entity<TransformComponent, EyeComponent?>>();
                return;
            }

            Array.Resize(ref pvsSession.Viewers, 1);
            pvsSession.Viewers[0] = (attached, Transform(attached), _eyeQuery.CompOrNull(attached));
            return;
        }

        var count = session.ViewSubscriptions.Count;
        var i = 0;
        if (session.AttachedEntity is { } local)
        {
            if (!session.ViewSubscriptions.Contains(local))
                count += 1;

            Array.Resize(ref pvsSession.Viewers, count);

            // Attached entity is always the first viewer, to prioritize it and help reduce pop-in for the "main" eye.
            pvsSession.Viewers[i++] = (local, Transform(local), _eyeQuery.CompOrNull(local));
        }
        else
        {
            Array.Resize(ref pvsSession.Viewers, count);
        }

        foreach (var ent in session.ViewSubscriptions)
        {
            if (ent != session.AttachedEntity)
                pvsSession.Viewers[i++] =  (ent, Transform(ent), _eyeQuery.CompOrNull(ent));
        }

        DebugTools.AssertEqual(i, pvsSession.Viewers.Length);
    }

    private void ProcessVisibleChunks()
    {
        using var _= Histogram.WithLabels("Update Chunks & Overrides").NewTimer();
        var task = _parallelMgr.Process(_chunkJob, _chunkJob.Count);

        UpdateCleanChunks();
        CacheGlobalOverrides();

        task.WaitOne();
    }

    /// <summary>
    /// Variant of <see cref="ProcessVisibleChunks"/> that isn't multithreaded.
    /// </summary>
    internal void ProcessVisibleChunksSequential()
    {
        for (var i = 0; i < _dirtyChunks.Count; i++)
        {
            UpdateDirtyChunks(i);
        }
        UpdateCleanChunks();
        CacheGlobalOverrides();
    }

    /// <summary>
    /// Add an entity to the set of entities that are directly attached to a chunk and mark the chunk as dirty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddEntityToChunk(EntityUid uid, MetaDataComponent meta, PvsChunkLocation location)
    {
        DebugTools.Assert(meta.EntityLifeStage < EntityLifeStage.Terminating);
        ref var chunk = ref CollectionsMarshal.GetValueRefOrAddDefault(_chunks, location, out var existing);
        if (!existing)
        {
            chunk = _chunkPool.Get();
            try
            {
                chunk.Initialize(location, _metaQuery, _xformQuery);
            }
            catch (Exception)
            {
                _chunks.Remove(location);
                throw;
            }
            _chunkSets.GetOrNew(location.Uid).Add(location);
        }

        chunk!.MarkDirty();
        chunk.Children.Add(uid);
        meta.LastPvsLocation = location;
    }

    /// <summary>
    /// Remove an entity from a chunk and mark it as dirty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveEntityFromChunk(EntityUid uid, MetaDataComponent meta)
    {
        if (meta.LastPvsLocation is not {} old)
            return;

        meta.LastPvsLocation = null;
        if (!_chunks.TryGetValue(old, out var chunk))
            return;

        chunk.MarkDirty();
        chunk.Children.Remove(uid);
        if (chunk.Children.Count > 0 || chunk.AttachedChunkEntity != null)
            return;

        _chunks.Remove(old);
        _chunkPool.Return(chunk);
        _chunkSets[old.Uid].Remove(old);
    }

    private void OnChunkEntityAdded(ref ChunkEntityAddedEvent ev)
    {
        AddChunkEntityToChunk(ev.Entity, ev.Root, ev.Chunk);
    }

    private void OnChunkEntityRemoved(ref ChunkEntityRemovedEvent ev)
    {
        RemoveChunkEntityFromChunk(ev.Entity);
    }

    private void AddChunkEntityToChunk(EntityUid uid, EntityUid root, Vector2i chunkIndices)
    {
        if (!_metaQuery.TryGetComponent(uid, out var meta))
            throw new ArgumentException($"Chunk entity {uid} does not have metadata.", nameof(uid));

        DebugTools.Assert(meta.EntityLifeStage < EntityLifeStage.Terminating);
        DebugTools.Assert(!_chunkEntityLocations.ContainsKey(uid));

        var location = new PvsChunkLocation(root, chunkIndices);
        AddChunkEntityToPvsChunk(uid, location);

        _chunkEntityLocations[uid] = location;
        _metadataMemory.GetRef(meta.PvsData.Index).IsChunkEntity = true;
    }

    private void AddChunkEntityToPvsChunk(EntityUid uid, PvsChunkLocation location)
    {
        ref var chunk = ref CollectionsMarshal.GetValueRefOrAddDefault(_chunks, location, out var existing);
        if (!existing)
        {
            chunk = _chunkPool.Get();
            try
            {
                chunk.Initialize(location, _metaQuery, _xformQuery);
            }
            catch (Exception)
            {
                _chunks.Remove(location);
                throw;
            }

            _chunkSets.GetOrNew(location.Uid).Add(location);
        }

        if (chunk!.AttachedChunkEntity is { } attached && attached != uid)
            throw new InvalidOperationException($"PVS chunk {location} already has chunk entity {ToPrettyString(attached)} attached while adding {ToPrettyString(uid)}.");

        chunk.MarkDirty();
        chunk.AttachedChunkEntity = uid;
    }

    private void RemoveChunkEntityFromChunk(EntityUid uid)
    {
        if (!_chunkEntityLocations.Remove(uid, out var location))
            return;

        if (_metaQuery.TryGetComponent(uid, out var meta) && meta.PvsData != PvsIndex.Invalid)
            _metadataMemory.GetRef(meta.PvsData.Index).IsChunkEntity = false;

        RemoveChunkEntityFromPvsChunk(uid, location);
    }

    private void RemoveChunkEntityFromPvsChunk(EntityUid uid, PvsChunkLocation old)
    {
        if (!_chunks.TryGetValue(old, out var chunk))
            return;

        if (chunk.AttachedChunkEntity != uid)
            return;

        chunk.MarkDirty();
        chunk.AttachedChunkEntity = null;
        if (chunk.Children.Count > 0)
            return;

        _chunks.Remove(old);
        _chunkPool.Return(chunk);
        if (_chunkSets.TryGetValue(old.Uid, out var locations))
            locations.Remove(old);
    }

    /// <summary>
    /// Mark a chunk as dirty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DirtyChunk(PvsChunkLocation location)
    {
        if (_chunks.TryGetValue(location, out var chunk))
            chunk.MarkDirty();
    }

    /// <summary>
    /// Mark all chunks as dirty.
    /// </summary>
    private void DirtyAllChunks()
    {
        foreach (var chunk in _chunks.Values)
        {
            chunk.MarkDirty();
        }
    }

    private void OnGridRemoved(GridRemovalEvent ev)
    {
        RemoveRoot(ev.EntityUid);
    }

    private void OnMapChanged(MapRemovedEvent ev)
    {
        RemoveRoot(ev.Uid);
    }

    private void RemoveRoot(EntityUid root)
    {
        if (!_chunkSets.Remove(root, out var locations))
        {
            DebugTools.Assert(_chunks.Values.All(x => x.Map.Owner != root && x.Root.Owner != root));
            return;
        }

        DebugTools.Assert(_chunks.Values.All(x => locations.Contains(x.Location) || x.Root.Owner != root));

        foreach (var loc in locations)
        {
            if (_chunks.Remove(loc, out var chunk))
            {
                if (chunk.AttachedChunkEntity is { } chunkEntity)
                {
                    if (_chunkEntityLocations.TryGetValue(chunkEntity, out var chunkLocation) &&
                        chunkLocation == loc)
                    {
                        _chunkEntityLocations.Remove(chunkEntity);
                    }

                    if (_metaQuery.TryGetComponent(chunkEntity, out var meta) && meta.PvsData != PvsIndex.Invalid)
                        _metadataMemory.GetRef(meta.PvsData.Index).IsChunkEntity = false;
                }

                _chunkPool.Return(chunk);
            }
        }
        DebugTools.Assert(_chunks.Values.All(x => x.Map.Owner != root && x.Root.Owner != root));
    }

    internal void GridParentChanged(Entity<TransformComponent, MetaDataComponent> grid)
    {
        if (!_chunkSets.TryGetValue(grid.Owner, out var locations))
        {
            DebugTools.Assert(_chunks.Values.All(x => x.Root.Owner != grid.Owner));
            return;
        }

        DebugTools.Assert(_chunks.Values.All(x => locations.Contains(x.Location) || x.Root.Owner != grid.Owner));
        if (grid.Comp1.MapUid is not { } map || !TryComp(map, out MetaDataComponent? meta))
        {
            if (grid.Comp1.MapUid != null && grid.Comp2.EntityLifeStage < EntityLifeStage.Terminating)
                Log.Error($"Grid {ToPrettyString(grid)} has no map?");
            RemoveRoot(grid.Owner);
            return;
        }

        var newMap = new Entity<MetaDataComponent>(map, meta);
        foreach (var loc in locations)
        {
            if (_chunks.TryGetValue(loc, out var chunk))
                chunk.Map = newMap;
        }
    }
}

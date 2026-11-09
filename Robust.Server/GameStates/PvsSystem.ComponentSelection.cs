using System.Collections.Generic;
using System.Threading;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Robust.Server.GameStates;

// CMU14: nearby viewers repeatedly inspect the same mostly unchanged components.
internal sealed partial class PvsSystem
{
    // Bound retained scratch per worker; additional entities use the ordinary component scan.
    private const int MaxComponentSelections = 512;
    private readonly ThreadLocal<ComponentSelectionCache> _componentSelections = new(static () => new(), true);
    private bool _componentSelectionActive;

    private sealed class ComponentSelectionCache
    {
        public readonly Dictionary<MetaDataComponent, ComponentSelection> Entries = new();
        public readonly Stack<ComponentSelection> Buffers = new();
    }

    private sealed class ComponentSelection
    {
        public GameTick FromTick;
        public readonly Dictionary<ushort, IComponent> Components = new();
    }

    private Dictionary<ushort, IComponent> SelectStateComponents(MetaDataComponent meta, GameTick fromTick,
        bool sendComponentList)
    {
        // Removals need the complete recipient-visible component list. Initial states also usually need every component.
        if (!_componentSelectionActive || sendComponentList || fromTick <= meta.CreationTick)
            return meta.NetComponents;

        var cache = _componentSelections.Value!;
        if (cache.Entries.TryGetValue(meta, out var selection))
        {
            // A more recent ACK can use this superset: the state loop still checks its own fromTick.
            if (fromTick >= selection.FromTick)
                return selection.Components;

            selection.Components.Clear();
        }
        else
        {
            if (cache.Entries.Count >= MaxComponentSelections)
                return meta.NetComponents;

            selection = cache.Buffers.TryPop(out var buffer) ? buffer : new();
            cache.Entries.Add(meta, selection);
        }

        selection.FromTick = fromTick;
        foreach (var (netId, component) in meta.NetComponents)
        {
            // Keep invalid entries so the ordinary state path still reports them.
            if (component.Deleted || !component.Initialized || component.LastModifiedTick > fromTick)
                selection.Components.Add(netId, component);
        }

        return selection.Components;
    }

    private void ClearComponentSelections()
    {
        // All workers have joined. Release entity/component references even if a worker is not used next tick.
        foreach (var cache in _componentSelections.Values)
        {
            foreach (var selection in cache.Entries.Values)
            {
                selection.Components.Clear();
                cache.Buffers.Push(selection);
            }

            cache.Entries.Clear();
        }
    }
}

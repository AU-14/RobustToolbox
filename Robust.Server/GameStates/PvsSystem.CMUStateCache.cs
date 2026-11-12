using System.Collections.Generic;
using System.Threading;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Robust.Server.GameStates;

// CMU14: only these engine-owned handlers produce recipient-independent, read-only snapshots.
// Cache before recipient filters, for exactly one serialization pass and one ACK baseline.
internal sealed partial class PvsSystem
{
    private const int CMUMaxSharedStates = 8192;
    private readonly ThreadLocal<Dictionary<(IComponent Component, GameTick From), IComponentState>> _cmuSharedStates
        = new(static () => new(), true);

    private bool CMUCanShareState(IComponent component)
        => _componentSelectionActive && component is TransformComponent or PhysicsComponent or MetaDataComponent;

    private bool CMUTrySharedState(IComponent component, ref ComponentGetState ev)
    {
        if (!CMUCanShareState(component) || !_cmuSharedStates.Value!.TryGetValue((component, ev.FromTick), out var state))
            return false;

        ev.State = state;
        return true;
    }

    private void CMUStoreSharedState(IComponent component, ref ComponentGetState ev)
    {
        if (!CMUCanShareState(component) || ev.ExcludeReplays || ev.State == null)
            return;

        var cache = _cmuSharedStates.Value!;
        if (cache.Count < CMUMaxSharedStates)
            cache.TryAdd((component, ev.FromTick), ev.State);
    }

    private void CMUClearSharedStates()
    {
        foreach (var cache in _cmuSharedStates.Values)
            cache.Clear();
    }
}

using System;
using System.Buffers;
using System.Collections.Generic;
using Robust.Shared.Physics.Components;

namespace Robust.Shared.GameObjects;

public partial class EntityManager
{
    // CMU14 Component Removal Begin: callbacks may mutate components or recursively delete other entities.
    // Each enumeration owns a snapshot, with stable ordering inside the four existing priority groups.
    private readonly struct CMUComponentRemovalOrder(HashSet<IComponent> components, bool forCreation)
    {
        public Enumerator GetEnumerator() => new(components, forCreation);

        public struct Enumerator : IDisposable
        {
            private IComponent[] _snapshot;
            private readonly int _count;
            private readonly bool _forCreation;
            private int _group;
            private int _index;

            public IComponent Current { get; private set; }

            public Enumerator(HashSet<IComponent> components, bool forCreation)
            {
                _count = components.Count;
                _snapshot = ArrayPool<IComponent>.Shared.Rent(_count);
                components.CopyTo(_snapshot);
                _forCreation = forCreation;
                _group = 0;
                _index = -1;
                Current = default!;
            }

            public bool MoveNext()
            {
                for (; _group < 4; _group++)
                {
                    var priority = _forCreation ? _group : 3 - _group;
                    while (++_index < _count)
                    {
                        var component = _snapshot[_index];
                        var sequence = component switch
                        {
                            MetaDataComponent => 0,
                            TransformComponent => 1,
                            PhysicsComponent => 2,
                            _ => 3,
                        };
                        if (sequence != priority)
                            continue;

                        Current = component;
                        return true;
                    }

                    _index = -1;
                }

                return false;
            }

            public void Dispose()
            {
                if (_snapshot.Length == 0)
                    return;

                Array.Clear(_snapshot, 0, _count);
                ArrayPool<IComponent>.Shared.Return(_snapshot);
                _snapshot = Array.Empty<IComponent>();
                Current = default!;
            }
        }
    }
    // CMU14 End
}

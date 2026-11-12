using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Robust.Shared.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics.Contacts;
using Robust.Shared.Physics.Dynamics.Joints;
using Robust.Shared.Utility;

namespace Robust.Shared.Physics.Systems;

// CMU14 partial class: each solver worker reuses scratch only after its previous island has finished.
public abstract partial class SharedPhysicsSystem
{
    // Lists are borrowed/returned only by BuildIslands/ReturnIsland on the simulation thread.
    // Retain the observed peak instead of discarding every island beyond the old fixed pool limit.
    private sealed class CMUIslandListPool<T> : ObjectPool<List<T>>
    {
        private readonly Stack<List<T>> _available = new(MaxIslands);

        public override List<T> Get() => _available.TryPop(out var list) ? list : new List<T>();

        public override void Return(List<T> list)
        {
            list.Clear();
            _available.Push(list);
        }
    }

    // These are the same body instances whose indices and state define the island.
    internal readonly record struct CMUSolverJoint(
        Joint Original,
        Joint Joint,
        PhysicsComponent BodyA,
        PhysicsComponent BodyB);

    private readonly ThreadLocal<CMUSolverScratch> _cmuSolverScratch = new(static () => new CMUSolverScratch());
    private readonly CMUContactScratch _cmuContactScratch = new();
    private readonly ManifoldsJob _cmuManifoldsJob = new();
    private readonly ConditionalWeakTable<DistanceJoint, DistanceJoint> _cmuDistanceJointCopies = new();

    private Joint CMUGetSolverJoint(Joint joint, EntityUid uidA, EntityUid uidB)
    {
        if (joint is not DistanceJoint distance)
            return joint.Clone(uidA, uidB);

        // Preserve the original joint until ReturnIsland, including what break callbacks observe.
        // Recreate after configuration/anchor/relay changes so constructor clamping stays identical.
        if (_cmuDistanceJointCopies.TryGetValue(distance, out var copy)
            && copy.BodyAUid == uidA && copy.BodyBUid == uidB
            && copy.LocalAnchorA == distance.LocalAnchorA && copy.LocalAnchorB == distance.LocalAnchorB
            && copy.Length == distance.Length && copy.MinLength == distance.MinLength && copy.MaxLength == distance.MaxLength
            && copy.Stiffness == distance.Stiffness && copy.Damping == distance.Damping
            && copy.Enabled == distance.Enabled && copy.Breakpoint == distance.Breakpoint)
        {
            distance.CopyTo(copy);
            return copy;
        }

        copy = (DistanceJoint) distance.Clone(uidA, uidB);
        _cmuDistanceJointCopies.Remove(distance);
        _cmuDistanceJointCopies.Add(distance, copy);
        return copy;
    }

    private sealed class CMUContactScratch
    {
        public Contact[] Contacts = Array.Empty<Contact>();
        public ContactStatus[] Status = Array.Empty<ContactStatus>();
        public FixedArray4<Vector2>[] WorldPoints = Array.Empty<FixedArray4<Vector2>>();
        public bool[] Wake = Array.Empty<bool>();

        public void EnsureContacts(int count)
        {
            if (Contacts.Length < count)
            {
                // Filtering callbacks can append contacts. Keep the same spare capacity as ArrayPool.
                var capacity = (int) BitOperations.RoundUpToPowerOf2((uint) count);
                Contacts = new Contact[Math.Max(capacity, 16)];
            }
        }

        public void EnsureManifolds(int count)
        {
            if (Status.Length >= count)
                return;

            var capacity = Math.Max(count, Math.Max(16, Status.Length * 2));
            Status = new ContactStatus[capacity];
            WorldPoints = new FixedArray4<Vector2>[capacity];
            Wake = new bool[capacity];
        }
    }

    private sealed class CMUSolverScratch
    {
        public Vector2[] Positions = Array.Empty<Vector2>();
        public float[] Angles = Array.Empty<float>();
        public ContactVelocityConstraint[] Velocities = Array.Empty<ContactVelocityConstraint>();
        public ContactPositionConstraint[] Contacts = Array.Empty<ContactPositionConstraint>();

        public void EnsureCapacity(int bodies, int contacts)
        {
            if (Positions.Length < bodies)
            {
                var capacity = Math.Max(bodies, Math.Max(16, Positions.Length * 2));
                Positions = new Vector2[capacity];
                Angles = new float[capacity];
            }

            if (Velocities.Length < contacts)
            {
                var capacity = Math.Max(contacts, Math.Max(16, Velocities.Length * 2));
                Velocities = new ContactVelocityConstraint[capacity];
                Contacts = new ContactPositionConstraint[capacity];
            }
        }
    }
}

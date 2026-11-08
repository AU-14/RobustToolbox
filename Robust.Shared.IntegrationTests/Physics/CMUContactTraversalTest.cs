using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Dynamics.Contacts;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

// CMU14: collision callbacks can remove later contacts while filtering the current one.
[TestFixture]
public sealed class CMUContactTraversalTest
{
    [TestCase(false)]
    [TestCase(true)]
    public void CallbacksRemovingContactsDoNotSkipRemainingContacts(bool removeCurrent)
    {
        var sim = RobustServerSimulation.NewSimulation()
            .RegisterComponents(f => f.RegisterClass<CMUContactCallbackComponent>())
            .RegisterEntitySystems(f => f.LoadExtraSystemType<CMUContactCallbackSystem>())
            .InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var physics = entities.System<SharedPhysicsSystem>();
        var fixtures = entities.System<FixtureSystem>();
        var map = sim.CreateMap();
        var first = Pair(0);
        var next = Pair(10);
        var last = Pair(20);
        physics.CollideContacts();
        Assert.That(first.Contact.MapNode.Next!.Value, Is.SameAs(next.Contact));
        Assert.That(next.Contact.MapNode.Next!.Value, Is.SameAs(last.Contact));
        Assert.That(first.Contact.IsTouching && next.Contact.IsTouching && last.Contact.IsTouching);

        var callback = entities.AddComponent<CMUContactCallbackComponent>(first.Uid);
        if (removeCurrent)
        {
            callback.DeleteOnFilter = first.Uid;
            physics.SetCollisionMask(first.Uid, "shape", first.Fixture, 3);
        }
        else
        {
            callback.DeleteOnEnd = next.Uid;
        }
        foreach (var pair in removeCurrent ? new[] { last } : new[] { first, last })
        {
            physics.SetCollisionMask(pair.Uid, "shape", pair.Fixture, 0);
            physics.SetCollisionLayer(pair.Uid, "shape", pair.Fixture, 0);
        }

        physics.CollideContacts();

        Assert.That(entities.Deleted(removeCurrent ? first.Uid : next.Uid), Is.True,
            "The callback must actually remove the selected contact.");
        Assert.That(last.Body.ContactCount, Is.Zero,
            "Filtering must reach the remaining contact in this pass instead of leaving a stale collision.");
        entities.DeleteEntity(map.Uid);
        return;

        (EntityUid Uid, PhysicsComponent Body, Fixture Fixture, Contact Contact) Pair(float x)
        {
            var a = Spawn(new Vector2(x, 0));
            var b = Spawn(new Vector2(x + .5f, 0));
            physics.AddPair("shape", "shape", a.Fixture.Proxies[0], b.Fixture.Proxies[0]);
            return (a.Uid, a.Body, a.Fixture, a.Body.Contacts.First!.Value);
        }

        (EntityUid Uid, PhysicsComponent Body, Fixture Fixture) Spawn(Vector2 position)
        {
            var uid = entities.SpawnAttachedTo(null, new EntityCoordinates(map.Uid, position));
            var body = entities.AddComponent<PhysicsComponent>(uid);
            physics.SetBodyType(uid, BodyType.Dynamic, body: body);
            var fixture = new Fixture(new PhysShapeCircle(.5f), 1, 1, true);
            fixtures.CreateFixture(uid, "shape", fixture, body: body);
            physics.SetCanCollide(uid, true, body: body);
            physics.WakeBody(uid, body: body);
            return (uid, body, fixture);
        }
    }
}

[RegisterComponent]
public sealed partial class CMUContactCallbackComponent : Component
{
    public EntityUid? DeleteOnEnd;
    public EntityUid? DeleteOnFilter;
}

public sealed class CMUContactCallbackSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<CMUContactCallbackComponent, EndCollideEvent>(OnEnd);
        SubscribeLocalEvent<CMUContactCallbackComponent, PreventCollideEvent>(OnFilter);
    }

    private void OnFilter(Entity<CMUContactCallbackComponent> ent, ref PreventCollideEvent args)
    {
        if (ent.Comp.DeleteOnFilter is not { } target)
            return;
        ent.Comp.DeleteOnFilter = null;
        args.Cancelled = true;
        Del(target);
    }

    private void OnEnd(Entity<CMUContactCallbackComponent> ent, ref EndCollideEvent args)
    {
        if (ent.Comp.DeleteOnEnd is not { } target)
            return;
        ent.Comp.DeleteOnEnd = null;
        Del(target);
    }
}

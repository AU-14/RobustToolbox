using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

// CMU14: compacting retained broadphase scratch storage must preserve collision discovery and removal.
[TestFixture]
public sealed class CMUMoveBufferCompactionTest
{
    [Test]
    public void ContactsSurviveCompactionAndTrackReplacedObstacles()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var physics = entities.System<SharedPhysicsSystem>();
        var fixtures = entities.System<FixtureSystem>();
        var map = sim.CreateMap();
        var mover = Spawn(0, true);
        var wall = Spawn(2, false);
        // Model a large startup/burst allocation followed by normal movement.
        physics.MoveBuffer.EnsureCapacity(200000);
        Advance(180);
        Assert.That(physics.MoveBuffer.EnsureCapacity(0), Is.LessThan(200000));
        Assert.That(entities.GetComponent<TransformComponent>(mover.Uid).LocalPosition.X, Is.InRange(.9f, 1.1f));
        Assert.That(mover.Body.ContactCount, Is.EqualTo(1));

        entities.DeleteEntity(wall.Uid);
        Advance(90);
        Assert.That(entities.GetComponent<TransformComponent>(mover.Uid).LocalPosition.X, Is.GreaterThan(3.5f));
        Assert.That(mover.Body.ContactCount, Is.Zero);
        Spawn(6, false);
        Advance(90);
        Assert.That(entities.GetComponent<TransformComponent>(mover.Uid).LocalPosition.X, Is.InRange(4.9f, 5.1f));
        Assert.That(mover.Body.ContactCount, Is.EqualTo(1));
        entities.DeleteEntity(map.Uid);
        return;

        void Advance(int ticks)
        {
            for (var tick = 0; tick < ticks; tick++)
            {
                physics.SetLinearVelocity(mover.Uid, Vector2.UnitX, body: mover.Body);
                physics.MoveBuffer.Add(mover.Fixture.Proxies[0]);
                physics.Update(1f / 30);
            }
        }

        (EntityUid Uid, PhysicsComponent Body, Fixture Fixture) Spawn(float x, bool dynamic)
        {
            var uid = entities.SpawnAttachedTo(null, new EntityCoordinates(map.Uid, x, 0));
            var body = entities.AddComponent<PhysicsComponent>(uid);
            physics.SetBodyType(uid, dynamic ? BodyType.Dynamic : BodyType.Static, body: body);
            physics.SetSleepingAllowed(uid, body, false);
            var fixture = new Fixture(new PhysShapeCircle(.5f), 1, 1, true);
            fixtures.CreateFixture(uid, "shape", fixture, body: body);
            physics.SetCanCollide(uid, true, body: body);
            if (dynamic)
                physics.WakeBody(uid, body: body);
            return (uid, body, fixture);
        }
    }
}

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

// CMU14: island bookkeeping must survive transitions between free motion, constraints and sleep.
[TestFixture]
public sealed class CMUIslandTransitionTest
{
    [Test]
    public void FreeBodyCanCollideJoinSleepAndMoveAgain()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var physics = entities.System<SharedPhysicsSystem>();
        var fixtures = entities.System<FixtureSystem>();
        var joints = entities.System<SharedJointSystem>();
        var map = sim.CreateMap();
        var mover = Spawn(Vector2.Zero, BodyType.Dynamic);
        var body = entities.GetComponent<PhysicsComponent>(mover);
        var xform = entities.GetComponent<TransformComponent>(mover);
        physics.SetLinearVelocity(mover, Vector2.UnitX);
        Step(3);
        Assert.That(xform.LocalPosition.X, Is.GreaterThan(0));

        var wallPosition = xform.LocalPosition + new Vector2(.8f, 0);
        var wall = Spawn(wallPosition, BodyType.Static);
        var otherMover = Spawn(wallPosition + new Vector2(.8f, 0), BodyType.Dynamic);
        Step(30);
        Assert.That(body.ContactCount, Is.GreaterThan(0));
        Assert.That(xform.LocalPosition.X, Is.LessThan(wallPosition.X - .98f),
            "A formerly free body must participate in contact position correction.");
        Assert.That(entities.GetComponent<TransformComponent>(otherMover).LocalPosition.X,
            Is.GreaterThan(wallPosition.X + .98f), "The static wall must participate in both independent islands.");

        entities.DeleteEntity(wall);
        entities.DeleteEntity(otherMover);
        var before = xform.LocalPosition;
        physics.SetLinearVelocity(mover, Vector2.UnitX);
        Step(3);
        Assert.That(body.ContactCount, Is.Zero);
        Assert.That(xform.LocalPosition.X, Is.GreaterThan(before.X));

        var anchorPosition = xform.LocalPosition + new Vector2(2, 0);
        var anchor = Spawn(anchorPosition, BodyType.Static);
        var joint = joints.CreateDistanceJoint(mover, anchor);
        joints.Update(1f / 30);
        physics.SetLinearVelocity(mover, new Vector2(-5, 0));
        Step(30);
        Assert.That(Vector2.Distance(xform.LocalPosition, anchorPosition), Is.EqualTo(2).Within(.01f),
            "Adding a joint must restore constrained solver indices for the formerly free body.");

        joints.RemoveJoint(joint);
        joints.Update(1f / 30);
        entities.DeleteEntity(anchor);
        physics.SetLinearVelocity(mover, Vector2.Zero);
        Step(60);
        Assert.That(body.Awake, Is.False, "The unconstrained body must still accumulate sleep time.");

        before = xform.LocalPosition;
        physics.SetLinearVelocity(mover, Vector2.UnitX);
        Step(3);
        Assert.That(body.Awake, Is.True);
        Assert.That(xform.LocalPosition.X, Is.GreaterThan(before.X),
            "A stale island flag must not prevent a woken body from moving.");
        entities.DeleteEntity(map.Uid);
        return;

        void Step(int ticks)
        {
            for (var i = 0; i < ticks; i++)
                physics.Update(1f / 30);
        }

        EntityUid Spawn(Vector2 position, BodyType bodyType)
        {
            var uid = entities.SpawnAttachedTo(null, new EntityCoordinates(map.Uid, position));
            var physicsBody = entities.AddComponent<PhysicsComponent>(uid);
            physics.SetBodyType(uid, bodyType, body: physicsBody);
            fixtures.CreateFixture(uid, "shape", new Fixture(new PhysShapeCircle(.5f), 1, 1, true), body: physicsBody);
            physics.SetCanCollide(uid, true, body: physicsBody);
            if (bodyType != BodyType.Static)
                physics.WakeBody(uid, body: physicsBody);
            return uid;
        }
    }
}

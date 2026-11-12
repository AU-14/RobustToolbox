using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Dynamics.Joints;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

// CMU14: retained solver copies must observe edits and preserve break-event ordering.
[TestFixture]
public sealed class CMUJointSolverReuseTest
{
    [Test]
    public void DistanceJointEditsAndBreakCallbacksKeepTheirBehavior()
    {
        var sim = RobustServerSimulation.NewSimulation()
            .RegisterEntitySystems(f => f.LoadExtraSystemType<CMUJointBreakObserver>())
            .InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var physics = entities.System<SharedPhysicsSystem>();
        var fixtures = entities.System<FixtureSystem>();
        var joints = entities.System<SharedJointSystem>();
        var observer = entities.System<CMUJointBreakObserver>();
        var map = sim.CreateMap();
        var mover = Spawn(Vector2.Zero, BodyType.Dynamic);
        var anchor = Spawn(new Vector2(2, 0), BodyType.Static);
        var joint = joints.CreateDistanceJoint(mover, anchor);
        joints.Update(1f / 30);
        Step(4);

        joint.MaxLength = 3;
        joint.Length = 3;
        joint.MinLength = 3;
        joint.LocalAnchorA = new Vector2(.5f, 0);
        physics.SetLinearVelocity(mover, -Vector2.UnitX);
        Step(30);
        var position = entities.GetComponent<TransformComponent>(mover).LocalPosition;
        Assert.That(Vector2.Distance(position + joint.LocalAnchorA, new Vector2(2, 0)),
            Is.EqualTo(3).Within(.01f), "A reused solver copy must observe both length and anchor edits.");

        joint.Breakpoint = .001f;
        physics.SetLinearVelocity(mover, new Vector2(-10, 0));
        Step(1);
        Assert.That(observer.Breaks, Is.EqualTo(1));
        Assert.That(observer.WasEnabled, Is.True,
            "Break callbacks observe the original joint before solver state is copied back.");
        Assert.That(joint.Enabled, Is.False, "The disabled solver state must still reach the original afterward.");
        entities.DeleteEntity(map.Uid);
        return;

        void Step(int ticks)
        {
            for (var i = 0; i < ticks; i++)
                physics.Update(1f / 30);
        }

        EntityUid Spawn(Vector2 position, BodyType type)
        {
            var uid = entities.SpawnAttachedTo(null, new EntityCoordinates(map.Uid, position));
            var body = entities.AddComponent<PhysicsComponent>(uid);
            physics.SetBodyType(uid, type, body: body);
            fixtures.CreateFixture(uid, "body", new Fixture(new PhysShapeCircle(.25f), 1, 1, true), body: body);
            physics.SetCanCollide(uid, true, body: body);
            if (type != BodyType.Static)
            {
                physics.SetSleepingAllowed(uid, body, false);
                physics.WakeBody(uid, body: body);
            }
            return uid;
        }
    }
}

// CMU14 test system: observe the public callback before solver state is copied back.
public sealed class CMUJointBreakObserver : EntitySystem
{
    public int Breaks;
    public bool WasEnabled;

    public override void Initialize() => SubscribeLocalEvent<JointBreakEvent>(OnBreak);

    private void OnBreak(ref JointBreakEvent args)
    {
        Breaks++;
        WasEnabled = args.Joint.Enabled;
    }
}

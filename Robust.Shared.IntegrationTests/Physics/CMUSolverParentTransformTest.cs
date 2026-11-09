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

// CMU14: solver-local parent reuse must follow parent changes within and between solves.
[TestFixture]
public sealed class CMUSolverParentTransformTest
{
    [Test]
    public void SiblingsUnderDifferentParentsKeepWorldMovementAfterReparenting()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var physics = entities.System<SharedPhysicsSystem>();
        var fixtures = entities.System<FixtureSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        entities.System<SharedGridTraversalSystem>().Enabled = false;
        var map = sim.CreateMap();
        var parents = new[]
        {
            entities.SpawnAttachedTo(null, new EntityCoordinates(map.Uid, new Vector2(3, 4))),
            entities.SpawnAttachedTo(null, new EntityCoordinates(map.Uid, new Vector2(-5, 2))),
        };
        transforms.SetLocalRotation(parents[0], .5);
        transforms.SetLocalRotation(parents[1], -.7);
        var movers = new List<EntityUid>();
        var velocity = new Vector2(1.5f, -.75f);
        foreach (var parent in parents)
        {
            for (var i = 0; i < 3; i++)
            {
                var uid = entities.SpawnAttachedTo(null, new EntityCoordinates(parent, new Vector2(i * 3, 1)));
                var body = entities.AddComponent<PhysicsComponent>(uid);
                physics.SetBodyType(uid, BodyType.Kinematic, body: body);
                fixtures.CreateFixture(uid, "shape", new Fixture(new PhysShapeCircle(.25f), 1, 1, true), body: body);
                physics.SetCanCollide(uid, true, body: body);
                physics.SetLinearVelocity(uid, velocity, body: body);
                movers.Add(uid);
            }
        }

        CheckMovement();
        transforms.SetLocalPositionRotation(parents[0], new Vector2(6, -3), 1.1);
        transforms.SetParent(movers[1], parents[1]);
        CheckMovement();
        entities.DeleteEntity(map.Uid);
        return;

        void CheckMovement()
        {
            var before = movers.Select(uid => transforms.GetWorldPosition(uid)).ToArray();
            physics.Step(1f / 30, false);
            for (var i = 0; i < movers.Count; i++)
            {
                var expected = before[i] + velocity / 30;
                Assert.That(Vector2.Distance(transforms.GetWorldPosition(movers[i]), expected), Is.LessThan(0.00001f),
                    "Each body must use its current parent's transform, even when neighboring bodies have different parents.");
            }
        }
    }
}

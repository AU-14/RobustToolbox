using System.Numerics;
using NUnit.Framework;
using Robust.Server.GameObjects;
using Robust.Shared.ComponentTrees;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

// CMU14 class
[TestFixture]
public sealed class CMUFiniteRayIntegrationTest
{
    [Test]
    public void OcclusionRayTracksMovedOccludersAndGridTransforms()
    {
        var sim = RobustServerSimulation.NewSimulation()
            .RegisterEntitySystems(systems =>
            {
                systems.LoadExtraSystemType<ServerOccluderSystem>();
                systems.LoadExtraSystemType<RecursiveMoveSystem>();
            })
            .InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var maps = sim.System<SharedMapSystem>();
        var transforms = sim.System<SharedTransformSystem>();
        var occluders = sim.System<OccluderSystem>();
        maps.CreateMap(out var mapId);
        var grid = maps.CreateGridEntity(mapId);
        for (var x = 0; x <= 6; x++)
            maps.SetTile(grid, new Vector2i(x, 0), new Tile(1));
        var near = entities.SpawnAttachedTo(null, new EntityCoordinates(grid.Owner, 2.5f, 0.5f));
        var far = entities.SpawnAttachedTo(null, new EntityCoordinates(grid.Owner, 6.5f, 0.5f));
        entities.AddComponent<OccluderComponent>(near);
        entities.AddComponent<OccluderComponent>(far);

        void Check(bool blocked)
        {
            var start = transforms.ToMapCoordinates(new EntityCoordinates(grid.Owner, 0.5f, 0.5f));
            var end = transforms.ToMapCoordinates(new EntityCoordinates(grid.Owner, 4.5f, 0.5f));
            var ray = new Ray(start.Position, Vector2.Normalize(end.Position - start.Position));
            var hit = occluders.IntersectRay(mapId, ray, 3, EntityUid.Invalid,
                static (ent, skip) => ent.Owner == skip);
            Assert.That(hit?.HitEntity, Is.EqualTo(blocked ? near : (EntityUid?) null));
            Assert.That(occluders.IntersectRay(mapId, ray, 3, near,
                static (ent, skip) => ent.Owner == skip), Is.Null);
        }

        transforms.SetLocalPosition(grid.Owner, new Vector2(10, -3));
        transforms.SetLocalRotation(grid.Owner, Angle.FromDegrees(30));
        Check(true);
        transforms.SetLocalPosition(near, new Vector2(5, 0.5f));
        transforms.SetLocalPosition(grid.Owner, new Vector2(-8, 7));
        transforms.SetLocalRotation(grid.Owner, Angle.FromDegrees(-60));
        Check(false);
    }

    [Test]
    public void PredicateRayKeepsFiltersAndStaticDynamicHitsOnMovingRotatedGrid()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var maps = sim.System<SharedMapSystem>();
        var fixtures = sim.System<FixtureSystem>();
        var physics = sim.System<SharedPhysicsSystem>();
        var transforms = sim.System<SharedTransformSystem>();
        maps.CreateMap(out var mapId);
        var grid = maps.CreateGridEntity(mapId);
        for (var x = 0; x <= 6; x++)
            maps.SetTile(grid, new Vector2i(x, 0), new Tile(1));

        EntityUid Spawn(float x, BodyType type, int layer = 1, bool hard = true)
        {
            var uid = entities.SpawnAttachedTo(null, new EntityCoordinates(grid.Owner, x, 0.5f));
            var body = entities.AddComponent<PhysicsComponent>(uid);
            fixtures.CreateFixture(uid, "ray", new Fixture(new PhysShapeCircle(0.25f), layer, 1, hard));
            physics.SetBodyType(uid, type, body: body);
            physics.SetCanCollide(uid, true, body: body);
            return uid;
        }

        var ignored = Spawn(1, BodyType.Static);
        Spawn(1.5f, BodyType.Static, hard: false);
        Spawn(1.75f, BodyType.Static, layer: 2);
        var near = Spawn(2.5f, BodyType.Static);
        var farther = Spawn(3.5f, BodyType.Dynamic);
        Spawn(6, BodyType.Static);

        // Exact endpoint hits remain inclusive before introducing rotation rounding.
        var endpointRay = new CollisionRay(new Vector2(0.5f, 0.5f), Vector2.UnitX, 1);
        var endpointHits = physics.IntersectRayWithPredicate(mapId, endpointRay, ignored,
            static (uid, skip) => uid == skip, 1.75f).ToArray();
        Assert.That(endpointHits.Select(hit => hit.HitEntity), Is.EqualTo(new[] { near }));

        void Check()
        {
            var start = transforms.ToMapCoordinates(new EntityCoordinates(grid.Owner, 0.5f, 0.5f));
            var end = transforms.ToMapCoordinates(new EntityCoordinates(grid.Owner, 4.5f, 0.5f));
            var ray = new CollisionRay(start.Position, Vector2.Normalize(end.Position - start.Position), 1);
            var hits = physics.IntersectRayWithPredicate(mapId, ray, ignored,
                static (uid, skip) => uid == skip, 3, returnOnFirstHit: false).ToArray();
            Assert.That(hits.Select(hit => hit.HitEntity), Is.EqualTo(new[] { near, farther }));
            Assert.That(hits[0].Distance, Is.EqualTo(1.75f).Within(0.001f));
            Assert.That(hits[1].Distance, Is.EqualTo(2.75f).Within(0.001f));

            Assert.That(physics.IntersectRayWithPredicate(mapId, ray, ignored,
                static (uid, skip) => uid == skip, 1.5f), Is.Empty);
            var first = physics.IntersectRayWithPredicate(mapId, ray, (ignored, near),
                static (uid, skip) => uid == skip.ignored || uid == skip.near, 3).ToArray();
            Assert.That(first.Select(hit => hit.HitEntity), Is.EqualTo(new[] { farther }));
        }

        transforms.SetLocalPosition(grid.Owner, new Vector2(10, -3));
        transforms.SetLocalRotation(grid.Owner, Angle.FromDegrees(30));
        Check();
        transforms.SetLocalPosition(grid.Owner, new Vector2(-8, 7));
        transforms.SetLocalRotation(grid.Owner, Angle.FromDegrees(-60));
        Check();
    }
}

using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Robust.Server.GameStates;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.GameState;

// CMU14: sharing chunk lookups must preserve per-eye results and discard them between visibility passes.
[TestFixture]
public sealed class CMUVisibleChunkRegionTest
{
    [Test]
    public void SharedRegionsMatchIndividualViewersAcrossGridAndEntityChanges()
    {
        var sim = RobustServerSimulation.NewSimulation()
            .RegisterComponents(f => f.RegisterClass<ViewSubscriberComponent>())
            .RegisterEntitySystems(s =>
            {
                s.LoadExtraSystemType<Robust.Server.GameObjects.EyeSystem>();
                s.LoadExtraSystemType<EyeComponent.EyeComponent_AutoNetworkSystem>();
                s.LoadExtraSystemType<Robust.Server.GameObjects.ViewSubscriberSystem>();
            })
            .InitializeInstance();
        var entities = sim.Resolve<IEntityManager>();
        var pvs = entities.System<PvsSystem>();
        var maps = entities.System<SharedMapSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var map = sim.CreateMap();
        var otherMap = sim.CreateMap();
        var grid = maps.CreateGridEntity(map.Uid);
        maps.SetTile(grid, Vector2i.Zero, new Tile(1));
        var onGrid = entities.SpawnAttachedTo(null, new EntityCoordinates(grid.Owner, .5f, .5f));
        var removable = entities.SpawnAttachedTo(null, new EntityCoordinates(map.Uid, -16, -16));
        entities.SpawnAttachedTo(null, new EntityCoordinates(otherMap.Uid, -16, -16));
        var sessions = new PvsSession[20];
        for (var i = 0; i < sessions.Length; i++)
        {
            var root = i == 3 ? otherMap.Uid : map.Uid;
            // Keep viewers off the grid tile so moving the grid does not carry them along.
            var pos = i < 4 ? new Vector2(2, 2) : new Vector2(i * 32, 0);
            var viewer = entities.SpawnAttachedTo(null, new EntityCoordinates(root, pos));
            var session = new DummySession(default, "viewer", new SessionData(default, "viewer"))
            {
                Status = SessionStatus.InGame,
                AttachedEntity = viewer,
            };
            // Only chunk discovery is exercised, so per-client state memory is not needed.
            sessions[i] = new PvsSession(session, null!);
        }
        var eyeUid = sessions[2].Session.AttachedEntity!.Value;
        var eye = entities.AddComponent<EyeComponent>(eyeUid);
        var eyes = entities.System<SharedEyeSystem>();
        eyes.SetOffset(eyeUid, new Vector2(-8, 4), eye);
        eyes.SetPvsScale((eyeUid, eye), 2);
        entities.System<SharedViewSubscriberSystem>().AddViewSubscriber(
            sessions[3].Session.AttachedEntity!.Value, sessions[2].Session);
        var sessionField = typeof(PvsSystem).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Check();
        var oldLocation = new PvsChunkLocation(map.Uid, PvsSystem.GetChunkIndices(new Vector2(-16, -16)));
        Assert.That(sessions[0].ChunkSet.Select(c => c.Location), Does.Contain(oldLocation));
        Assert.That(sessions[0].ChunkSet.Any(c => c.Root.Owner == grid.Owner), Is.True);
        Assert.That(sessions[3].ChunkSet.Any(c => c.Root.Owner == grid.Owner), Is.False);

        entities.DeleteEntity(removable);
        entities.SpawnAttachedTo(null, new EntityCoordinates(map.Uid, 16, 16));
        transforms.SetLocalPosition(grid.Owner, new Vector2(-5, 7));
        transforms.SetLocalRotation(grid.Owner, Angle.FromDegrees(35));
        Check();
        Assert.That(sessions[0].ChunkSet.Select(c => c.Location), Does.Not.Contain(oldLocation));
        Assert.That(sessions[0].ChunkSet.Select(c => c.Location),
            Does.Contain(new PvsChunkLocation(map.Uid, PvsSystem.GetChunkIndices(new Vector2(16, 16)))));

        transforms.SetCoordinates(grid.Owner, new EntityCoordinates(otherMap.Uid, Vector2.Zero));
        Check();
        Assert.That(sessions[0].ChunkSet.Any(c => c.Root.Owner == grid.Owner), Is.False);
        Assert.That(sessions[3].ChunkSet.Any(c => c.Root.Owner == grid.Owner), Is.True);
        entities.DeleteEntity(onGrid);
        entities.DeleteEntity(grid.Owner);
        Check();
        Assert.That(sessions.All(s => s.ChunkSet.All(c => c.Root.Owner != grid.Owner)), Is.True);
        entities.DeleteEntity(map.Uid);
        entities.DeleteEntity(otherMap.Uid);
        return;

        void Check()
        {
            sessionField.SetValue(pvs, sessions);
            pvs.GetVisibleChunks();
            var combined = sessions.Select(s => s.ChunkSet.Select(c => c.Location).ToArray()).ToArray();
            pvs.ProcessVisibleChunksSequential();
            for (var i = 0; i < sessions.Length; i++)
            {
                sessionField.SetValue(pvs, new[] { sessions[i] });
                pvs.GetVisibleChunks();
                Assert.That(sessions[i].ChunkSet.Select(c => c.Location), Is.EqualTo(combined[i]),
                    "Sharing lookups must preserve the same chunks in the same order as a viewer searched alone.");
                pvs.ProcessVisibleChunksSequential();
            }
        }
    }
}

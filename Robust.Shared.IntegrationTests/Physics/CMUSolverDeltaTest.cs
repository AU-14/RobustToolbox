using System.IO;
using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Robust.UnitTesting.Shared.Physics;

// CMU14: solver velocity updates must retain delta replication and all force-reset state.
public sealed class CMUSolverDeltaTest : RobustIntegrationTest
{
    [Test]
    public async Task SolvedVelocityDeltaReconstructsFullStateAcrossAcknowledgements()
    {
        using var server = StartServer(new ServerIntegrationOptions { Pool = false });
        await server.WaitIdleAsync();
        var entities = server.EntMan;
        var physics = server.System<SharedPhysicsSystem>();
        EntityUid uid = default;
        PhysicsComponent body = default!;
        await server.WaitPost(() =>
        {
            var map = server.System<SharedMapSystem>().CreateMap();
            uid = entities.SpawnAttachedTo(null, new EntityCoordinates(map, Vector2.Zero));
            body = entities.AddComponent<PhysicsComponent>(uid);
            physics.SetBodyType(uid, BodyType.Dynamic, body: body);
            server.System<FixtureSystem>().CreateFixture(uid, "body", new Fixture(new PhysShapeCircle(.3f), 1, 1, true), body: body);
            physics.SetSleepingAllowed(uid, body, false);
            physics.SetCanCollide(uid, true, body: body);
            physics.SetLinearDamping(uid, body, .5f);
            physics.SetAngularVelocity(uid, 2f, body: body);
            physics.SetLinearVelocity(uid, new Vector2(3, 1), body: body);
        });
        await server.WaitRunTicks(3);
        PhysicsComponentState initial = default!;
        GameTick earlierAck = default;
        await server.WaitPost(() =>
        {
            initial = FullState();
            earlierAck = server.Timing.CurTick;
        });
        await server.WaitRunTicks(2);
        await server.WaitPost(() =>
        {
            // These fields must still clear in the delta after the solver consumes them.
            physics.ApplyForce(uid, new Vector2(4, 2), body: body);
            physics.ApplyTorque(uid, 1.5f, body: body);
        });
        await server.WaitRunTicks(1);
        PhysicsComponentState intermediate = default!;
        GameTick laterAck = default;
        await server.WaitPost(() =>
        {
            CheckDelta(initial, earlierAck);
            intermediate = FullState();
            laterAck = server.Timing.CurTick;
        });
        await server.WaitRunTicks(2);
        await server.WaitPost(() =>
        {
            CheckDelta(initial, earlierAck);
            CheckDelta(intermediate, laterAck);
        });
        return;

        PhysicsComponentState FullState() => (PhysicsComponentState) entities.GetComponentState(
            entities.EventBus, body, null, GameTick.Zero)!;

        void CheckDelta(PhysicsComponentState baseline, GameTick ack)
        {
            var state = entities.GetComponentState(entities.EventBus, body, null, ack);
            Assert.That(state, Is.TypeOf<PhysicsComponentDeltaState>(), "ordinary solver changes must not force a full state");
            var delta = (PhysicsComponentDeltaState) state!;
            var serializer = server.ResolveDependency<IRobustSerializer>();
            using var wire = new MemoryStream();
            serializer.Serialize(wire, delta);
            var deltaBytes = wire.Length;
            wire.Position = 0;
            var decoded = (PhysicsComponentDeltaState) serializer.Deserialize(wire);
            var reconstructed = decoded.CreateNewFullState(baseline);
            var expected = FullState();
            using var a = new MemoryStream();
            using var b = new MemoryStream();
            serializer.SerializeDirect(a, reconstructed);
            serializer.SerializeDirect(b, expected);
            Assert.That(a.ToArray(), Is.EqualTo(b.ToArray()), "the existing wire decoder must reconstruct every field exactly");
            wire.SetLength(0);
            serializer.Serialize(wire, expected);
            TestContext.Out.WriteLine($"CMU-PHYSICS-DELTA deltaBytes={deltaBytes} fullBytes={wire.Length} mask={delta.ChangedFields}");
        }
    }
}

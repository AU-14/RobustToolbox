using System.IO;
using System.Numerics;
using System.Reflection;
using Moq;
using NUnit.Framework;
using Robust.Server.GameStates;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Robust.UnitTesting.Shared.GameState;

// CMU14: shared component selection must preserve recipient-specific state and ACK/lifecycle boundaries.
public sealed class CMUPvsComponentSelectionTest : RobustIntegrationTest
{
    [Test]
    public async Task TwoObserversReceiveChangesAcrossSerializationPasses()
    {
        using var server = StartServer(new ServerIntegrationOptions { Pool = false });
        using var first = StartClient(new ClientIntegrationOptions { Pool = false });
        using var second = StartClient(new ClientIntegrationOptions { Pool = false });
        await ConnectClient(server, first, "first");
        await ConnectClient(server, second, "second");
        await RunTicks(10);
        EntityUid target = default;
        NetEntity netTarget = default;
        await server.WaitPost(() =>
        {
            server.CfgMan.SetCVar(CVars.NetPVS, true);
            var map = server.System<SharedMapSystem>().CreateMap();
            var coords = new EntityCoordinates(map, default);
            foreach (var session in server.PlayerMan.Sessions)
            {
                var player = server.EntMan.SpawnAttachedTo(null, coords);
                server.PlayerMan.SetAttachedEntity(session, player);
                server.PlayerMan.JoinGame(session);
            }
            target = server.EntMan.SpawnAttachedTo(null, coords);
            netTarget = server.EntMan.GetNetEntity(target);
        });
        await RunTicks(10);
        await server.WaitPost(() => server.System<SharedTransformSystem>().SetLocalPosition(target, Vector2.UnitX));
        await RunTicks(10);
        await server.WaitPost(() =>
        {
            server.System<MetaDataSystem>().SetEntityName(target, "changed after movement");
            server.System<SharedTransformSystem>().SetLocalPosition(target, Vector2.UnitX * 2);
        });
        await RunTicks(10);
        foreach (var client in new[] { first, second })
        {
            await client.WaitPost(() =>
            {
                var uid = client.EntMan.GetEntity(netTarget);
                Assert.That(client.Transform(uid).LocalPosition, Is.EqualTo(Vector2.UnitX * 2));
                Assert.That(client.MetaData(uid).EntityName, Is.EqualTo("changed after movement"));
            });
        }
        await DisconnectClient(server, first);
        await DisconnectClient(server, second);
        return;

        async Task RunTicks(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await server.WaitRunTicks(1);
                await first.WaitRunTicks(1);
                await second.WaitRunTicks(1);
            }
        }
    }

    [Test]
    public async Task CachedSelectionPreservesRecipientStatesAndLifecycle()
    {
        using var server = StartServer(new ServerIntegrationOptions { Pool = false });
        await server.WaitIdleAsync();
        var entities = server.EntMan;
        EntityUid entity = default;
        CMUSelectionTestComponent component = default!;
        CMUSelectionOwnerTestComponent ownerComponent = default!;
        var owner = new Mock<ICommonSession>();
        var observer = new Mock<ICommonSession>();
        owner.SetupGet(p => p.Name).Returns("owner");
        observer.SetupGet(p => p.Name).Returns("observer");
        await server.WaitPost(() =>
        {
            var map = server.System<SharedMapSystem>().CreateMap();
            entity = entities.SpawnAttachedTo(null, new EntityCoordinates(map, default));
            component = entities.AddComponent<CMUSelectionTestComponent>(entity);
            ownerComponent = entities.AddComponent<CMUSelectionOwnerTestComponent>(entity);
            owner.SetupGet(p => p.AttachedEntity).Returns(entity);
        });
        await server.WaitRunTicks(3);
        var earlierAck = server.Timing.CurTick - 1;
        await server.WaitPost(() =>
        {
            component.Value = 12;
            entities.Dirty(entity, component);
            entities.Dirty(entity, ownerComponent);
        });
        await server.WaitRunTicks(2);
        var laterAck = server.Timing.CurTick - 1;

        await server.WaitPost(() =>
        {
            var pvs = entities.System<PvsSystem>();
            var meta = entities.GetComponent<MetaDataComponent>(entity);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var active = typeof(PvsSystem).GetField("_componentSelectionActive", flags)!;
            var clear = typeof(PvsSystem).GetMethod("ClearComponentSelections", flags)!.CreateDelegate<Action>(pvs);
            var get = typeof(PvsSystem).GetMethod("TryGetEntityState", flags)!
                .CreateDelegate<Func<ICommonSession?, EntityUid, GameTick, MetaDataComponent, bool, EntityState?>>(pvs);
            var giveBack = typeof(PvsSystem).GetMethod("ReturnEntityState", flags)!.CreateDelegate<Action<EntityState>>(pvs);
            var serializer = server.ResolveDependency<IRobustSerializer>();
            var factory = server.ResolveDependency<IComponentFactory>();
            var ownerId = factory.GetRegistration<CMUSelectionOwnerTestComponent>().NetID!.Value;
            var sessionId = factory.GetRegistration<CMUSelectionTestComponent>().NetID!.Value;

            byte[] Snapshot(ICommonSession? session, GameTick ack, bool cached, Action<EntityState>? check = null)
            {
                active.SetValue(pvs, cached);
                var state = get(session, entity, ack, meta, true)!;
                try
                {
                    check?.Invoke(state);
                    using var stream = new MemoryStream();
                    serializer.SerializeDirect(stream, state);
                    return stream.ToArray();
                }
                finally { giveBack(state); }
            }

            void Compare(ICommonSession? session, GameTick ack, Action<EntityState>? check = null)
            {
                var expected = Snapshot(session, ack, false, check);
                var actual = Snapshot(session, ack, true, check);
                Assert.That(actual, Is.EqualTo(expected), "Caching must preserve the exact serialized entity state.");
            }

            try
            {
                // A later ACK can initially select nothing; an older observer must expand that selection.
                Compare(owner.Object, laterAck, state => Assert.That(state.ComponentChanges.Value.Any(c => c.NetID == sessionId), Is.False));
                // Warm the same entity/ACK selection with one recipient, then reuse it for a different one.
                Compare(owner.Object, earlierAck, state =>
                {
                    Assert.That(state.ComponentChanges.Value.Any(c => c.NetID == ownerId), Is.True);
                    var payload = (CMUSelectionTestState) state.ComponentChanges.Value.Single(c => c.NetID == sessionId).State!;
                    Assert.That(payload.Recipient, Is.EqualTo("owner"));
                    Assert.That(payload.Value, Is.EqualTo(12));
                });
                Compare(observer.Object, earlierAck, state =>
                {
                    Assert.That(state.ComponentChanges.Value.Any(c => c.NetID == ownerId), Is.False);
                    Assert.That(((CMUSelectionTestState) state.ComponentChanges.Value.Single(c => c.NetID == sessionId).State!).Recipient,
                        Is.EqualTo("observer"));
                });
                component.DeniedRecipient = "observer";
                Compare(observer.Object, earlierAck, state => Assert.That(state.ComponentChanges.Value.Any(c => c.NetID == sessionId), Is.False));
                Compare(null, earlierAck, state => Assert.That(state.ComponentChanges.Value.Any(c => c.NetID == sessionId), Is.False));
                Compare(owner.Object, laterAck, state => Assert.That(state.ComponentChanges.Value.Any(c => c.NetID == sessionId), Is.False));
                Compare(owner.Object, GameTick.Zero);

                // Another serialization pass in the same game tick must see components dirtied after the previous pass.
                active.SetValue(pvs, false);
                clear();
                component.Value = 34;
                entities.Dirty(entity, component);
                Compare(owner.Object, laterAck, state => Assert.That(
                    ((CMUSelectionTestState) state.ComponentChanges.Value.Single(c => c.NetID == sessionId).State!).Value,
                    Is.EqualTo(34)));

                // Removal needs the full visible component list, including unchanged components, for each recipient.
                active.SetValue(pvs, false);
                clear();
                entities.RemoveComponent<CMUSelectionTestComponent>(entity);
                Compare(owner.Object, laterAck, state =>
                {
                    Assert.That(state.NetComponents, Does.Contain(ownerId));
                    Assert.That(state.NetComponents, Does.Not.Contain(sessionId));
                });
                Compare(observer.Object, laterAck, state => Assert.That(state.NetComponents, Does.Not.Contain(ownerId)));
            }
            finally
            {
                active.SetValue(pvs, false);
                clear();
            }
        });
    }
}

[RegisterComponent, NetworkedComponent]
public sealed partial class CMUSelectionTestComponent : Component
{
    public override bool SessionSpecific => true;
    public int Value;
    public string? DeniedRecipient;
}

[RegisterComponent, NetworkedComponent]
public sealed partial class CMUSelectionOwnerTestComponent : Component
{
    public override bool SendOnlyToOwner => true;
}

[Serializable, NetSerializable]
public sealed class CMUSelectionTestState(int value, string? recipient) : ComponentState
{
    public readonly int Value = value;
    public readonly string? Recipient = recipient;
}

public sealed class CMUSelectionTestSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<CMUSelectionTestComponent, ComponentGetState>(GetState);
        SubscribeLocalEvent<CMUSelectionTestComponent, ComponentGetStateAttemptEvent>(Attempt);
    }

    private void GetState(EntityUid uid, CMUSelectionTestComponent component, ref ComponentGetState args)
    {
        args.State = new CMUSelectionTestState(component.Value, args.Player?.Name);
        args.ExcludeReplays = true;
    }

    private void Attempt(EntityUid uid, CMUSelectionTestComponent component, ref ComponentGetStateAttemptEvent args)
    {
        if (args.Player?.Name == component.DeniedRecipient)
            args.Cancelled = true;
    }
}

using System.Reflection;
using Moq;
using NUnit.Framework;
using Robust.Server.GameStates;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.UnitTesting.Shared.GameState;

// CMU14: a bad appearance value must not corrupt outbound state or poison the next retry.
public sealed class CMUPvsSerializationFailureTest : RobustIntegrationTest
{
    private enum UnregisteredVisual : byte { Color }

    [Test]
    public async Task FailedStateIsDiscardedWithoutAckAndNextStateRecovers()
    {
        using var server = StartServer(new ServerIntegrationOptions { Pool = false });
        await server.WaitIdleAsync();
        await server.WaitRunTicks(3);
        await server.WaitPost(() =>
        {
            var entities = server.EntMan;
            var map = server.System<SharedMapSystem>().CreateMap();
            var target = entities.SpawnAttachedTo(null, new EntityCoordinates(map, default));
            entities.AddComponent<AppearanceComponent>(target);
            var appearance = server.System<SharedAppearanceSystem>();
            appearance.SetData(target, UnregisteredVisual.Color, "#FFB300");

            var channel = new Mock<INetChannel>(MockBehavior.Strict);
            var session = new Mock<ICommonSession>();
            session.SetupGet(s => s.Channel).Returns(channel.Object);
            session.SetupGet(s => s.Status).Returns(SessionStatus.InGame);
            using var memory = new ResizableMemoryRegion<PvsData>(1, 1);
            var ack = server.Timing.CurTick - 1;
            var data = new PvsSession(session.Object, memory) { DisableCulling = true, LastReceivedAck = ack };
            var pvs = server.System<PvsSystem>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var serialize = typeof(PvsSystem).GetMethod("SerializeSessionState", flags)!
                .CreateDelegate<Action<PvsSession>>(pvs);
            var send = typeof(PvsSystem).GetMethod("SendSessionState", flags)!
                .CreateDelegate<Action<PvsSession, ZStdCompressionContext, GameTick, byte[]?>>(pvs);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    Assert.Throws<KeyNotFoundException>(() => serialize(data));
                    Assert.Multiple(() =>
                    {
                        Assert.That(data.StateStream, Is.Null, "Partial bytes must never be handed to the send job.");
                        Assert.That(data.States, Is.Empty, "Failed states must return their temporary component lists.");
                        Assert.That(data.PlayerStates, Is.Empty);
                        Assert.That(data.RequestedFull, Is.True, "A later valid state must rebuild the client baseline.");
                        Assert.That(data.LastReceivedAck, Is.EqualTo(ack));
                    });
                    // A failed send must not even inspect the channel, compress bytes, or synthesize an ACK.
                    Assert.DoesNotThrow(() => send(data, null!, server.Timing.CurTick, null));
                    Assert.That(data.LastReceivedAck, Is.EqualTo(ack));
                }

                appearance.RemoveData(target, UnregisteredVisual.Color);
                serialize(data);
                using var stream = data.StateStream!;
                data.StateStream = null;
                stream.Position = 0;
                server.ResolveDependency<IRobustSerializer>().DeserializeDirect<Robust.Shared.GameStates.GameState>(stream, out var recovered);
                var entityState = recovered.EntityStates.Value.Single(s => s.NetEntity == entities.GetNetEntity(target));
                var appearanceState = entityState.ComponentChanges!.Value.Select(c => c.State)
                    .OfType<AppearanceComponentState>().Single();
                Assert.Multiple(() =>
                {
                    Assert.That(recovered.FromSequence, Is.EqualTo(GameTick.Zero));
                    Assert.That(appearanceState.Data, Is.Empty, "The corrected state must not retain the failed appearance payload.");
                    Assert.That(data.StateSerializationFailed, Is.False);
                    Assert.That(data.LastReceivedAck, Is.EqualTo(ack), "Serialization alone must not acknowledge the recovery.");
                });
            }
            finally
            {
                data.StateStream?.Dispose();
                data.StateStream = null;
                appearance.RemoveData(target, UnregisteredVisual.Color);
            }
        });
    }
}

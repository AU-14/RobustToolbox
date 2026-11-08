using System.Reflection;
using NUnit.Framework;
using Robust.Server.GameStates;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.UnitTesting.Server.GameStates;

// CMU14 class: ACK processing must release retired history without clearing live history.
[TestFixture]
public sealed class CMUPvsAckTest
{
    private static readonly MethodInfo ProcessAck = typeof(PvsSystem).GetMethod(
        "ProcessQueuedAck", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Func<List<PvsIndex>> Pool(PvsSystem pvs)
    {
        var pool = typeof(PvsSystem).GetField(
            "_entDataListPool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pvs)!;
        return pool.GetType().GetMethod("Get")!.CreateDelegate<Func<List<PvsIndex>>>(pool);
    }

    [Test]
    public void OverflowAckUpdatesEntitiesAndReusesRetiredList()
    {
        var pvs = new PvsSystem();
        var pool = Pool(pvs);
        using var memory = new ResizableMemoryRegion<PvsData>(3, 3);
        var session = new PvsSession(null!, memory) { RequestedFull = true };
        var retired = pool();
        retired.Add(new PvsIndex(0));
        retired.Add(new PvsIndex(1));
        var ack = new GameTick(10);
        session.PreviouslySent.Add(ack, retired);

        // Expire a real bounded-history entry, retaining all the newer entries for leave processing.
        for (uint i = 1; i <= PvsSystem.DirtyBufferSize; i++)
        {
            var sent = pool();
            sent.Add(new PvsIndex(2));
            if (session.PreviouslySent.Add(ack + i, sent, out var overflow))
                session.Overflow = overflow;
        }
        session.LastSent = (ack + PvsSystem.DirtyBufferSize,
            session.PreviouslySent[ack + PvsSystem.DirtyBufferSize - 1]);
        foreach (ref var data in memory.GetSpan<PvsData>())
            data.LastSeen = ack + PvsSystem.DirtyBufferSize;
        session.LastReceivedAck = ack;

        ProcessAck.CreateDelegate<Action<PvsSession>>(pvs)(session);

        Assert.Multiple(() =>
        {
            Assert.That(memory.GetRef(0).EntityLastAcked, Is.EqualTo(ack));
            Assert.That(memory.GetRef(1).EntityLastAcked, Is.EqualTo(ack));
            Assert.That(memory.GetRef(2).EntityLastAcked, Is.EqualTo(GameTick.Zero));
            Assert.That(session.RequestedFull, Is.False);
            Assert.That(session.Overflow, Is.Null);
            Assert.That(session.LastSent.Value.PreviouslySent, Has.Count.EqualTo(1));
            Assert.That(session.PreviouslySent.Values.All(list => list.Count == 1), Is.True);
        });
        var reused = pool();
        Assert.That(reused, Is.SameAs(retired), "A delayed ACK must return its retired visibility buffer for reuse.");
        Assert.That(reused, Is.Empty);
    }

    [Test]
    public void HistoryAckPreservesListForLeaveProcessing()
    {
        var pvs = new PvsSystem();
        var pool = Pool(pvs);
        using var memory = new ResizableMemoryRegion<PvsData>(1, 1);
        var ack = new GameTick(10);
        var session = new PvsSession(null!, memory) { LastReceivedAck = ack, RequestedFull = true };
        var sent = pool();
        sent.Add(new PvsIndex(0));
        session.PreviouslySent.Add(ack, sent);
        session.LastSent = (ack + 1, sent);
        memory.GetRef(0).LastSeen = ack;

        ProcessAck.CreateDelegate<Action<PvsSession>>(pvs)(session);

        Assert.Multiple(() =>
        {
            Assert.That(memory.GetRef(0).EntityLastAcked, Is.EqualTo(ack));
            Assert.That(session.RequestedFull, Is.False);
            Assert.That(session.LastSent.Value.PreviouslySent, Is.EqualTo(new[] { new PvsIndex(0) }));
            Assert.That(pool(), Is.Not.SameAs(sent), "History still owns the buffer after a normal ACK.");
        });
    }

    [Test]
    public void SkippedOverflowReturnsListWithoutAcknowledgingUnreceivedEntities()
    {
        var pvs = new PvsSystem();
        var pool = Pool(pvs);
        using var memory = new ResizableMemoryRegion<PvsData>(2, 2);
        var ack = new GameTick(11);
        var session = new PvsSession(null!, memory) { LastReceivedAck = ack, RequestedFull = true };
        var retired = pool();
        retired.Add(new PvsIndex(0));
        session.Overflow = (ack - 1, retired);
        session.PreviouslySent.Add(ack, [new PvsIndex(1)]);
        foreach (ref var data in memory.GetSpan<PvsData>())
            data.LastSeen = ack;

        ProcessAck.CreateDelegate<Action<PvsSession>>(pvs)(session);

        Assert.Multiple(() =>
        {
            Assert.That(memory.GetRef(0).EntityLastAcked, Is.EqualTo(GameTick.Zero));
            Assert.That(memory.GetRef(1).EntityLastAcked, Is.EqualTo(GameTick.Zero));
            Assert.That(session.RequestedFull, Is.True);
            Assert.That(session.PreviouslySent[ack], Has.Count.EqualTo(1));
            Assert.That(session.Overflow, Is.Null);
        });
        Assert.That(pool(), Is.SameAs(retired));
    }
}

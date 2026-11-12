using Lidgren.Network;
using NUnit.Framework;
using Robust.Shared.Network.Messages;
using Robust.Shared.Utility;

namespace Robust.UnitTesting.Server.GameStates;

// CMU14: precompression must retain the deployed client's packet format and delivery threshold.
public sealed class CMUStateCompressionTest
{
    [TestCase(256, false)]
    [TestCase(257, false)]
    [TestCase(32768, true)]
    public void PreparedPacketMatchesOriginalWriter(int size, bool random)
    {
        var payload = new byte[size];
        if (random)
            new System.Random(14).NextBytes(payload);
        using var stream = new MemoryStream(payload, 0, payload.Length, false, true);
        using var compression = new ZStdCompressionContext();
        var original = new MsgState { StateStream = stream, CompressionContext = compression };
        var prepared = new MsgState { StateStream = stream, CompressionContext = compression };
        prepared.CMUPrepare(new byte[ZStd.CompressBound(size)]);
        var peer = new NetPeer(new NetPeerConfiguration("CMUPacketCompatibility"));
        var oldPacket = peer.CreateMessage(original.EstimateBufferSize());
        var newPacket = peer.CreateMessage(prepared.EstimateBufferSize());
        oldPacket.Write((byte) 42);
        newPacket.Write((byte) 42);
        original.WriteToBuffer(oldPacket, null!);
        prepared.WriteToBuffer(newPacket, null!);

        Assert.That(newPacket.Data.AsSpan(0, newPacket.LengthBytes).ToArray(),
            Is.EqualTo(oldPacket.Data.AsSpan(0, oldPacket.LengthBytes).ToArray()));
        Assert.That(newPacket.LengthBytes, Is.EqualTo(prepared.EstimateBufferSize()));
        Assert.That(prepared.DeliveryMethod, Is.EqualTo(original.DeliveryMethod));
    }
}

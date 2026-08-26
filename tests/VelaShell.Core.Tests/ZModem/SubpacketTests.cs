using VelaShell.Core.ZModem.Protocol;
using VelaShell.Core.Tests.FileTransfer;

namespace VelaShell.Core.Tests.ZModem;

[TestClass]
[TestCategory("ZModem")]
public class SubpacketTests
{
    private static async Task<ZModemSubpacketResult> ReadOneAsync(byte[] wire, bool useCrc32, int chunkSize)
    {
        var chunks = new List<ReadOnlyMemory<byte>>();
        for (int i = 0; i < wire.Length; i += chunkSize)
        {
            chunks.Add(wire.AsMemory(i, Math.Min(chunkSize, wire.Length - i)));
        }
        await using var duplex = InMemoryByteDuplex.FromInbound(chunks);
        var reader = new ZModemFrameReader(duplex);
        return await ZModemSubpacket.ReadAsync(reader, useCrc32, CancellationToken.None);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Subpacket_RoundTrips_TextPayload(bool useCrc32)
    {
        byte[] payload = "Hello, ZMODEM subpacket!"u8.ToArray();
        byte[] wire = ZModemSubpacket.Write(payload, ZModemSubpacketEnd.EndNoAck, useCrc32);

        ZModemSubpacketResult result = await ReadOneAsync(wire, useCrc32, wire.Length);
        Assert.AreEqual(ZModemSubpacketStatus.Ok, result.Status);
        Assert.AreEqual(ZModemSubpacketEnd.EndNoAck, result.End);
        Assert.AreSequenceEqual(payload, result.Data);
    }

    [TestMethod]
    [DataRow(ZModemSubpacketEnd.EndNoAck)]
    [DataRow(ZModemSubpacketEnd.MoreNoAck)]
    [DataRow(ZModemSubpacketEnd.MoreAck)]
    [DataRow(ZModemSubpacketEnd.EndAck)]
    public async Task Subpacket_PreservesFrameEndSemantics(ZModemSubpacketEnd end)
    {
        byte[] payload = [1, 2, 3, 4, 5];
        byte[] wire = ZModemSubpacket.Write(payload, end, useCrc32: true);

        ZModemSubpacketResult result = await ReadOneAsync(wire, useCrc32: true, wire.Length);
        Assert.AreEqual(ZModemSubpacketStatus.Ok, result.Status);
        Assert.AreEqual(end, result.End);
        Assert.AreSequenceEqual(payload, result.Data);
    }

    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(false, 3)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    public async Task Subpacket_BinarySafe_AcrossChunkBoundaries(bool useCrc32, int chunkSize)
    {
        // Payload full of bytes that require ZDLE escaping plus arbitrary binary.
        byte[] payload =
        [
            0x18, 0x10, 0x11, 0x13, 0x90, 0x91, 0x93,
            0x00, 0xFF, 0x7F, 0x18, 0x18, 0x42, 0x0D, 0x0A
        ];
        byte[] wire = ZModemSubpacket.Write(payload, ZModemSubpacketEnd.EndAck, useCrc32);

        ZModemSubpacketResult result = await ReadOneAsync(wire, useCrc32, chunkSize);
        Assert.AreEqual(ZModemSubpacketStatus.Ok, result.Status);
        Assert.AreSequenceEqual(payload, result.Data);
    }

    [TestMethod]
    public async Task Subpacket_EmptyPayload_RoundTrips()
    {
        byte[] wire = ZModemSubpacket.Write([], ZModemSubpacketEnd.EndNoAck, useCrc32: false);
        ZModemSubpacketResult result = await ReadOneAsync(wire, useCrc32: false, wire.Length);
        Assert.AreEqual(ZModemSubpacketStatus.Ok, result.Status);
        Assert.IsEmpty(result.Data);
    }

    [TestMethod]
    public async Task Subpacket_CorruptCrc_ReturnsCrcError()
    {
        byte[] payload = "corrupt me"u8.ToArray();
        byte[] wire = ZModemSubpacket.Write(payload, ZModemSubpacketEnd.EndNoAck, useCrc32: false);
        // Corrupt the first payload byte (index 0 is unescaped 'c').
        wire[0] ^= 0xFF;

        ZModemSubpacketResult result = await ReadOneAsync(wire, useCrc32: false, wire.Length);
        Assert.AreEqual(ZModemSubpacketStatus.CrcError, result.Status);
    }

    [TestMethod]
    public async Task Subpacket_LargeBinaryPayload_RoundTrips()
    {
        byte[] payload = new byte[4096];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 + 7);
        }
        byte[] wire = ZModemSubpacket.Write(payload, ZModemSubpacketEnd.EndNoAck, useCrc32: true);

        ZModemSubpacketResult result = await ReadOneAsync(wire, useCrc32: true, 64);
        Assert.AreEqual(ZModemSubpacketStatus.Ok, result.Status);
        Assert.AreSequenceEqual(payload, result.Data);
    }
}

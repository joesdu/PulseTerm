using VelaShell.Core.Tests.FileTransfer;
using VelaShell.Core.ZModem.Protocol;

namespace VelaShell.Core.Tests.ZModem;

[TestClass]
[TestCategory("ZModem")]
public class FrameCodecTests
{
    private static async Task<ZModemHeaderResult> ReadSingleHeaderAsync(byte[] wire, int chunkSize)
    {
        var chunks = new List<ReadOnlyMemory<byte>>();
        for (int i = 0; i < wire.Length; i += chunkSize)
        {
            chunks.Add(wire.AsMemory(i, Math.Min(chunkSize, wire.Length - i)));
        }
        await using var duplex = InMemoryByteDuplex.FromInbound(chunks);
        var reader = new ZModemFrameReader(duplex);
        return await reader.ReadHeaderAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task HexHeader_RoundTrips()
    {
        var header = ZModemHeader.Empty(ZModemFrameType.ZRQINIT);
        byte[] wire = ZModemFrameWriter.Write(header, ZModemHeaderFormat.Hex);

        ZModemHeaderResult result = await ReadSingleHeaderAsync(wire, wire.Length);
        Assert.AreEqual(ZModemReadStatus.Header, result.Status);
        Assert.AreEqual(ZModemFrameType.ZRQINIT, result.Header.Type);
        Assert.AreEqual(ZModemHeaderFormat.Hex, result.Format);
    }

    [TestMethod]
    public async Task Binary16Header_RoundTrips_WithPosition()
    {
        var header = ZModemHeader.WithPosition(ZModemFrameType.ZRPOS, 0x00123456);
        byte[] wire = ZModemFrameWriter.Write(header, ZModemHeaderFormat.Binary16);

        ZModemHeaderResult result = await ReadSingleHeaderAsync(wire, wire.Length);
        Assert.AreEqual(ZModemReadStatus.Header, result.Status);
        Assert.AreEqual(ZModemFrameType.ZRPOS, result.Header.Type);
        Assert.AreEqual(0x00123456u, result.Header.Position);
        Assert.AreEqual(ZModemHeaderFormat.Binary16, result.Format);
    }

    [TestMethod]
    public async Task Binary32Header_RoundTrips_WithPosition()
    {
        var header = ZModemHeader.WithPosition(ZModemFrameType.ZDATA, 0xDEADBEEF);
        byte[] wire = ZModemFrameWriter.Write(header, ZModemHeaderFormat.Binary32);

        ZModemHeaderResult result = await ReadSingleHeaderAsync(wire, wire.Length);
        Assert.AreEqual(ZModemReadStatus.Header, result.Status);
        Assert.AreEqual(ZModemFrameType.ZDATA, result.Header.Type);
        Assert.AreEqual(0xDEADBEEFu, result.Header.Position);
        Assert.AreEqual(ZModemHeaderFormat.Binary32, result.Format);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(5)]
    public async Task Binary32Header_SurvivesArbitraryChunkBoundaries(int chunkSize)
    {
        // Position 0x18111318 forces multiple ZDLE escapes inside the header body,
        // stressing the split-chunk decode path.
        var header = ZModemHeader.WithPosition(ZModemFrameType.ZDATA, 0x18111318);
        byte[] wire = ZModemFrameWriter.Write(header, ZModemHeaderFormat.Binary32);

        ZModemHeaderResult result = await ReadSingleHeaderAsync(wire, chunkSize);
        Assert.AreEqual(ZModemReadStatus.Header, result.Status);
        Assert.AreEqual(0x18111318u, result.Header.Position);
    }

    [TestMethod]
    public async Task ReadHeader_SkipsLeadingNoiseBeforeFrame()
    {
        var header = ZModemHeader.Empty(ZModemFrameType.ZRINIT);
        byte[] frame = ZModemFrameWriter.Write(header, ZModemHeaderFormat.Hex);
        // Simulate a shell prompt + "rz\r" preceding the ZMODEM frame.
        byte[] noise = "user@host:~$ rz\r\n"u8.ToArray();
        byte[] wire = [.. noise, .. frame];

        ZModemHeaderResult result = await ReadSingleHeaderAsync(wire, 4);
        Assert.AreEqual(ZModemReadStatus.Header, result.Status);
        Assert.AreEqual(ZModemFrameType.ZRINIT, result.Header.Type);
    }

    [TestMethod]
    public async Task ReadHeader_CorruptCrc_ReturnsCrcError()
    {
        var header = ZModemHeader.WithPosition(ZModemFrameType.ZDATA, 1000);
        byte[] wire = ZModemFrameWriter.Write(header, ZModemHeaderFormat.Binary16);
        // Flip a byte inside the header body (skip the 3-byte ZPAD/ZDLE/ZBIN prefix).
        wire[4] ^= 0xFF;

        ZModemHeaderResult result = await ReadSingleHeaderAsync(wire, wire.Length);
        Assert.AreEqual(ZModemReadStatus.CrcError, result.Status);
    }

    [TestMethod]
    public async Task ReadHeader_CancelSequence_ReportsCancelled()
    {
        byte[] wire = [0x2A, 0x2A, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18];
        ZModemHeaderResult result = await ReadSingleHeaderAsync(wire, 3);
        Assert.AreEqual(ZModemReadStatus.Cancelled, result.Status);
    }

    [TestMethod]
    public async Task ReadHeader_EndOfStream_ReportsEof()
    {
        byte[] wire = "just noise, no frame"u8.ToArray();
        ZModemHeaderResult result = await ReadSingleHeaderAsync(wire, 4);
        Assert.AreEqual(ZModemReadStatus.EndOfStream, result.Status);
    }
}

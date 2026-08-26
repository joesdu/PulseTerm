using System.Text;
using VelaShell.Core.ZModem.Protocol;
using VelaShell.Core.FileTransfer.Protocol;
using VelaShell.Core.Tests.FileTransfer;

namespace VelaShell.Core.Tests.ZModem;

[TestClass]
[TestCategory("ZModem")]
public class CrcTests
{
    [TestMethod]
    public void Crc16Xmodem_CheckVector_MatchesKnownValue()
    {
        // Standard CRC-16/XMODEM check vector: "123456789" => 0x31C3.
        byte[] input = Encoding.ASCII.GetBytes("123456789");
        Assert.AreEqual((ushort)0x31C3, Crc16Xmodem.Compute(input));
    }

    [TestMethod]
    public void Crc16Xmodem_Empty_IsZero()
    {
        Assert.AreEqual((ushort)0x0000, Crc16Xmodem.Compute([]));
    }

    [TestMethod]
    public void Crc16Xmodem_MatchesRealLrzszWireValue()
    {
        // 地面真值:真实 Ubuntu 22.04 lrzsz sz 对 ZNAK 帧头 [06 00 00 00 00] 上链的 CRC 是 0xCD85
        // (2026-07-16 实测抓包 "**\x18B0600000000cd85")。lrzsz zm.c 的 updcrc 是旧式 XMODEM 算法,
        // 其「补两个零字节」收尾与本实现的现代查表算法数学等价 —— 增广已内建,绝不能再补。
        // 此前这里断言过 Augment("123456789")==0xDF8B 并声称"已对照 lrzsz 源码验证",实为双重增广:
        // 它让收发两侧自洽地一起错,与真实 sz/rz 的每一个非全零帧头互相报 CRC 错,握手永远谈不拢。
        Assert.AreEqual((ushort)0xCD85, Crc16Xmodem.Compute([0x06, 0x00, 0x00, 0x00, 0x00]));
    }

    [TestMethod]
    public void Crc16Xmodem_ChunkedUpdate_EqualsSinglePass()
    {
        byte[] input = Encoding.ASCII.GetBytes("The quick brown fox");
        ushort single = Crc16Xmodem.Compute(input);

        ushort chunked = 0;
        chunked = Crc16Xmodem.Update(chunked, input.AsSpan(0, 4));
        chunked = Crc16Xmodem.Update(chunked, input.AsSpan(4, 10));
        chunked = Crc16Xmodem.Update(chunked, input.AsSpan(14));
        Assert.AreEqual(single, chunked);
    }

    [TestMethod]
    public void Crc16Xmodem_SingleByteUpdate_EqualsSpanUpdate()
    {
        byte[] input = [0x00, 0x18, 0x7F, 0xFF, 0x41];
        ushort span = Crc16Xmodem.Compute(input);

        ushort perByte = 0;
        foreach (byte b in input)
        {
            perByte = Crc16Xmodem.Update(perByte, b);
        }
        Assert.AreEqual(span, perByte);
    }

    [TestMethod]
    public void Crc32ZModem_CheckVector_MatchesKnownValue()
    {
        // Standard CRC-32 (zlib/PKZIP) check vector: "123456789" => 0xCBF43926.
        byte[] input = Encoding.ASCII.GetBytes("123456789");
        Assert.AreEqual(0xCBF43926u, Crc32ZModem.Compute(input));
    }

    [TestMethod]
    public void Crc32ZModem_ChunkedRunning_EqualsSinglePass()
    {
        byte[] input = Encoding.ASCII.GetBytes("The quick brown fox jumps over");
        uint single = Crc32ZModem.Compute(input);

        uint running = Crc32ZModem.Initial;
        running = Crc32ZModem.UpdateRunning(running, input.AsSpan(0, 9));
        running = Crc32ZModem.UpdateRunning(running, input.AsSpan(9));
        Assert.AreEqual(single, running ^ 0xFFFFFFFFu);
    }

    [TestMethod]
    public void Crc32ZModem_ResidualMagic_VerifiesRoundTrip()
    {
        // Emulate wire format: append the transmitted (inverted) CRC bytes little-endian-first
        // as ZMODEM does, feed everything through the running algorithm, and expect the residue.
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x18, 0x11, 0x13];
        uint crc = Crc32ZModem.Compute(payload);

        uint running = Crc32ZModem.Initial;
        running = Crc32ZModem.UpdateRunning(running, payload);
        // ZMODEM transmits the complemented CRC, least-significant byte first.
        Span<byte> crcBytes =
        [
            (byte)(crc & 0xFF),
            (byte)((crc >> 8) & 0xFF),
            (byte)((crc >> 16) & 0xFF),
            (byte)((crc >> 24) & 0xFF)
        ];
        running = Crc32ZModem.UpdateRunning(running, crcBytes);
        Assert.AreEqual(Crc32ZModem.ResidualMagic, running);
    }
}

using VelaShell.Core.FileTransfer.Protocol;
using VelaShell.Core.XYModem.Protocol;

namespace VelaShell.Core.Tests.XYModem;

/// <summary>
/// XMODEM / YMODEM 数据块编解码的地面真值测试。这里的期望值不是拿我们自己的编码器生成的,
/// 而是按 Chuck Forsberg 的 <c>xmodem.txt</c> / <c>ymodem.txt</c> 里写死的块布局手工推出来的 ——
/// 否则测试只是在自证,编码器和解码器一起错的时候依然全绿。
/// </summary>
[TestClass]
[TestCategory("XYModem")]
public class XYModemBlockTests
{
    /// <summary>
    /// 128 字节块的布局必须是 <c>SOH 块号 ~块号 负载[128] CRC-HI CRC-LO</c>。
    /// 用全零负载做地面真值:CRC-16/XMODEM 初值为 0、无最终异或,全零输入的 CRC 必然是 0,
    /// 因此整块字节完全可以手推 —— 这正是 YMODEM 批结束块在链路上的样子。
    /// </summary>
    [TestMethod]
    public void SmallBlock_AllZeroPayload_MatchesHandDerivedWireBytes()
    {
        byte[] payload = new byte[128];
        byte[] wire = new byte[XYModemBlock.EncodedLength(128, useCrc: true)];

        int written = XYModemBlock.Write(payload, 0, useCrc: true, wire);

        Assert.AreEqual(3 + 128 + 2, written);
        Assert.AreEqual(0x01, wire[0], "128 字节块必须以 SOH(0x01)引导");
        Assert.AreEqual(0x00, wire[1], "块号");
        Assert.AreEqual(0xFF, wire[2], "块号取反");
        CollectionAssert.AreEqual(payload, wire[3..131], "负载应原样上链(这一族协议不做转义)");
        Assert.AreEqual(0x00, wire[131], "全零负载的 CRC-16/XMODEM 高字节必为 0");
        Assert.AreEqual(0x00, wire[132], "全零负载的 CRC-16/XMODEM 低字节必为 0");
    }

    /// <summary>1024 字节块必须改用 STX(0x02)引导,其余布局不变。</summary>
    [TestMethod]
    public void LargeBlock_UsesStxLead()
    {
        byte[] payload = new byte[1024];
        byte[] wire = new byte[XYModemBlock.EncodedLength(1024, useCrc: true)];

        int written = XYModemBlock.Write(payload, 7, useCrc: true, wire);

        Assert.AreEqual(3 + 1024 + 2, written);
        Assert.AreEqual(0x02, wire[0], "1024 字节块必须以 STX(0x02)引导");
        Assert.AreEqual(0x07, wire[1]);
        Assert.AreEqual(0xF8, wire[2], "~0x07 == 0xF8");
    }

    /// <summary>块号按 256 回绕:第 256 块的块号字节是 0x00,取反是 0xFF。</summary>
    [TestMethod]
    public void BlockNumber_WrapsAtByteBoundary()
    {
        byte[] wire = new byte[XYModemBlock.EncodedLength(128, useCrc: true)];

        XYModemBlock.Write(new byte[128], 256, useCrc: true, wire);

        Assert.AreEqual(0x00, wire[1]);
        Assert.AreEqual(0xFF, wire[2]);
    }

    /// <summary>
    /// 校验和模式(XMODEM 最初的形态)是 8 位算术和,可以直接手算:
    /// 128 个 0x01 相加 = 128 = 0x80;128 个 0x02 相加 = 256 → 溢出回绕为 0x00。
    /// </summary>
    [TestMethod]
    public void ChecksumMode_IsPlainEightBitSum()
    {
        byte[] ones = new byte[128];
        Array.Fill(ones, (byte)0x01);
        byte[] twos = new byte[128];
        Array.Fill(twos, (byte)0x02);

        Assert.AreEqual(0x80, XYModemBlock.Checksum(ones));
        Assert.AreEqual(0x00, XYModemBlock.Checksum(twos), "8 位和必须自然溢出回绕");

        byte[] wire = new byte[XYModemBlock.EncodedLength(128, useCrc: false)];
        int written = XYModemBlock.Write(ones, 1, useCrc: false, wire);
        Assert.AreEqual(3 + 128 + 1, written, "校验和模式的块尾只有一个字节");
        Assert.AreEqual(0x80, wire[131]);
    }

    /// <summary>
    /// 校验只覆盖负载,不覆盖块头。用「同一段负载、不同块号」的两个块做判据:
    /// 若实现误把块头也算进去,两块的校验字段就会不同。
    /// </summary>
    [TestMethod]
    public void Checksum_CoversPayloadOnly_NotHeader()
    {
        byte[] payload = new byte[128];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }
        byte[] first = new byte[XYModemBlock.EncodedLength(128, useCrc: true)];
        byte[] second = new byte[XYModemBlock.EncodedLength(128, useCrc: true)];

        XYModemBlock.Write(payload, 1, useCrc: true, first);
        XYModemBlock.Write(payload, 200, useCrc: true, second);

        CollectionAssert.AreEqual(first[131..133], second[131..133], "块号变了但校验字段不该变");
        // 与独立实现的 CRC 例程比对(Crc16Xmodem 自身有已知向量测试托底)。
        ushort expected = Crc16Xmodem.Compute(payload);
        Assert.AreEqual((byte)(expected >> 8), first[131]);
        Assert.AreEqual((byte)(expected & 0xFF), first[132]);
    }

    /// <summary>负载里翻一个比特,校验必须判失败 —— 否则块级纠错形同虚设。</summary>
    [TestMethod]
    public void Verify_RejectsSingleBitFlip()
    {
        byte[] payload = new byte[128];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 7);
        }
        byte[] wire = new byte[XYModemBlock.EncodedLength(128, useCrc: true)];
        XYModemBlock.Write(payload, 1, useCrc: true, wire);

        Assert.IsTrue(XYModemBlock.Verify(wire.AsSpan(3, 128), wire.AsSpan(131, 2), useCrc: true));

        wire[3 + 64] ^= 0x01;
        Assert.IsFalse(XYModemBlock.Verify(wire.AsSpan(3, 128), wire.AsSpan(131, 2), useCrc: true));
    }

    /// <summary>负载长度只能是 128 或 1024,别的一律拒绝(尾块补齐是调用方的责任)。</summary>
    [TestMethod]
    public void Write_RejectsNonStandardPayloadLength()
    {
        byte[] wire = new byte[2048];
        Assert.ThrowsExactly<ArgumentException>(() => XYModemBlock.Write(new byte[100], 1, true, wire));
        Assert.ThrowsExactly<ArgumentException>(() => XYModemBlock.Write(new byte[512], 1, true, wire));
    }
}

using VelaShell.Core.ZModem.Protocol;

namespace VelaShell.Core.Tests.ZModem;

[TestClass]
[TestCategory("ZModem")]
public class ZdleCodecTests
{
    [TestMethod]
    [DataRow((byte)0x18)] // ZDLE
    [DataRow((byte)0x10)] // DLE
    [DataRow((byte)0x11)] // XON
    [DataRow((byte)0x13)] // XOFF
    [DataRow((byte)0x90)]
    [DataRow((byte)0x91)]
    [DataRow((byte)0x93)]
    public void NeedsEscape_RequiredBytes_ReturnsTrue(byte value)
    {
        Assert.IsTrue(ZdleCodec.NeedsEscape(value));
    }

    [TestMethod]
    [DataRow((byte)0x00)]
    [DataRow((byte)0x41)] // 'A'
    [DataRow((byte)0x7F)]
    [DataRow((byte)0xFF)]
    public void NeedsEscape_OrdinaryBytes_ReturnsFalse(byte value)
    {
        Assert.IsFalse(ZdleCodec.NeedsEscape(value));
    }

    [TestMethod]
    public void EscapeByte_Zdle_ProducesEscapedPair()
    {
        var output = new List<byte>();
        ZdleCodec.EscapeByte(0x18, output);
        Assert.AreSequenceEqual(new byte[] { 0x18, 0x58 }, output);
    }

    [TestMethod]
    public void EscapeByte_Ordinary_PassesThrough()
    {
        var output = new List<byte>();
        ZdleCodec.EscapeByte(0x41, output);
        Assert.AreSequenceEqual("A"u8.ToArray(), output);
    }

    [TestMethod]
    public void ZctlEsc_EscapesAllControlBytes()
    {
        // 0x07 (BEL) is not in the default set but must be escaped under Zctlesc.
        Assert.IsFalse(ZdleCodec.NeedsEscape(0x07));
        Assert.IsTrue(ZdleCodec.NeedsEscape(0x07, escapeAllControl: true));
    }

    [TestMethod]
    public void Escape_ThenManualDecode_RoundTripsBinaryPayload()
    {
        byte[] original =
        [
            0x00, 0x18, 0x10, 0x11, 0x13, 0x90, 0x91, 0x93,
            0x41, 0x42, 0x7F, 0xFF, 0x18, 0x18, 0x01
        ];
        byte[] escaped = ZdleCodec.Escape(original);

        // Decode by walking ZDLE tokens, mirroring the receiver's subpacket parser.
        var decoded = new List<byte>();
        for (int i = 0; i < escaped.Length; i++)
        {
            if (escaped[i] == ZModemConstants.ZDLE)
            {
                ZdleToken token = ZdleCodec.ClassifyEscaped(escaped[++i]);
                Assert.AreEqual(ZdleTokenKind.DataByte, token.Kind);
                decoded.Add(token.Value);
            }
            else
            {
                decoded.Add(escaped[i]);
            }
        }
        Assert.AreSequenceEqual(original, [.. decoded]);
    }

    [TestMethod]
    public void ClassifyEscaped_SubpacketTerminators_MappedWithOriginalByte()
    {
        Assert.AreEqual(ZdleTokenKind.SubpacketEndNoAck, ZdleCodec.ClassifyEscaped(ZModemConstants.ZCRCE).Kind);
        Assert.AreEqual(ZdleTokenKind.SubpacketMoreNoAck, ZdleCodec.ClassifyEscaped(ZModemConstants.ZCRCG).Kind);
        Assert.AreEqual(ZdleTokenKind.SubpacketMoreAck, ZdleCodec.ClassifyEscaped(ZModemConstants.ZCRCQ).Kind);
        Assert.AreEqual(ZdleTokenKind.SubpacketEndAck, ZdleCodec.ClassifyEscaped(ZModemConstants.ZCRCW).Kind);
        // Terminator byte itself is carried through for CRC inclusion.
        Assert.AreEqual(ZModemConstants.ZCRCW, ZdleCodec.ClassifyEscaped(ZModemConstants.ZCRCW).Value);
    }

    [TestMethod]
    public void ClassifyEscaped_RubAndCancel_Recognized()
    {
        Assert.AreEqual(ZdleTokenKind.Rub0, ZdleCodec.ClassifyEscaped(ZdleCodec.ZRUB0).Kind);
        Assert.AreEqual((byte)0x7F, ZdleCodec.ClassifyEscaped(ZdleCodec.ZRUB0).Value);
        Assert.AreEqual(ZdleTokenKind.Rub1, ZdleCodec.ClassifyEscaped(ZdleCodec.ZRUB1).Kind);
        Assert.AreEqual((byte)0xFF, ZdleCodec.ClassifyEscaped(ZdleCodec.ZRUB1).Value);
        Assert.AreEqual(ZdleTokenKind.Cancel, ZdleCodec.ClassifyEscaped(ZModemConstants.ZDLE).Kind);
    }

    [TestMethod]
    public void ClassifyEscaped_IllegalByte_ReportsInvalid()
    {
        // 0x00 after ZDLE is not a legal escape (fails the (b & 0x60) == 0x40 test).
        Assert.AreEqual(ZdleTokenKind.Invalid, ZdleCodec.ClassifyEscaped(0x00).Kind);
    }
}

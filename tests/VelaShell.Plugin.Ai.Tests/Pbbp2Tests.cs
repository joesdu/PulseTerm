using System.Text;
using VelaShell.Plugin.Ai.Bridge.Channels.Feishu;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 飞书长连接的帧编解码。
/// </summary>
/// <remarks>
/// 这套 protobuf 是<b>手写</b>的(见 <see cref="Pbbp2" /> 的注释),没有生成器兜底,
/// 所以字段号、线格式与跳过未知字段这三件事必须有回归保护 —— 错一个字节的表现是
/// "连上了但一条消息都收不到",而那种故障在真机上极难定位。
/// </remarks>
[TestClass]
public sealed class Pbbp2Tests
{
    [TestMethod]
    public void Encode_ThenDecode_RoundTripsEveryField()
    {
        var frame = new Pbbp2.Frame
        {
            SeqId = 42,
            LogId = 987654321,
            Service = 7,
            Method = Pbbp2.FrameTypeData,
            PayloadEncoding = "gzip",
            PayloadType = "json",
            Payload = Encoding.UTF8.GetBytes("""{"hello":"世界"}"""),
            LogIdNew = "log-new"
        };
        frame.SetHeader(Pbbp2.HeaderNames.Type, "event");
        frame.SetHeader(Pbbp2.HeaderNames.MessageId, "msg-1");

        Pbbp2.Frame decoded = Pbbp2.Decode(Pbbp2.Encode(frame));

        Assert.AreEqual(42ul, decoded.SeqId);
        Assert.AreEqual(987654321ul, decoded.LogId);
        Assert.AreEqual(7, decoded.Service);
        Assert.AreEqual(Pbbp2.FrameTypeData, decoded.Method);
        Assert.AreEqual("gzip", decoded.PayloadEncoding);
        Assert.AreEqual("json", decoded.PayloadType);
        Assert.AreEqual("""{"hello":"世界"}""", Encoding.UTF8.GetString(decoded.Payload));
        Assert.AreEqual("log-new", decoded.LogIdNew);
        Assert.AreEqual("event", decoded.Header(Pbbp2.HeaderNames.Type));
        Assert.AreEqual("msg-1", decoded.Header(Pbbp2.HeaderNames.MessageId));
    }

    /// <summary>varint 是变长的,所以大于一个字节的值必须单独验一遍。</summary>
    [TestMethod]
    public void Encode_HandlesMultiByteVarints()
    {
        var frame = new Pbbp2.Frame { SeqId = ulong.MaxValue, LogId = 300, Service = 128, Method = 1 };

        Pbbp2.Frame decoded = Pbbp2.Decode(Pbbp2.Encode(frame));

        Assert.AreEqual(ulong.MaxValue, decoded.SeqId);
        Assert.AreEqual(300ul, decoded.LogId);
        Assert.AreEqual(128, decoded.Service);
    }

    /// <summary>ping 帧只有类型与服务号,别的字段一个都不该占位。</summary>
    [TestMethod]
    public void PingFrame_CarriesOnlyTypeAndService()
    {
        var ping = new Pbbp2.Frame { Method = Pbbp2.FrameTypeControl, Service = 3 };
        ping.SetHeader(Pbbp2.HeaderNames.Type, "ping");

        Pbbp2.Frame decoded = Pbbp2.Decode(Pbbp2.Encode(ping));

        Assert.AreEqual(Pbbp2.FrameTypeControl, decoded.Method);
        Assert.AreEqual("ping", decoded.Header(Pbbp2.HeaderNames.Type));
        Assert.IsEmpty(decoded.Payload);
        Assert.HasCount(1, decoded.Headers);
    }

    /// <summary>同名头只留最后一份 —— 应答帧会重复设 biz_rt,不能越堆越多。</summary>
    [TestMethod]
    public void SetHeader_ReplacesInsteadOfAppending()
    {
        var frame = new Pbbp2.Frame();
        frame.SetHeader(Pbbp2.HeaderNames.BizRt, "10");
        frame.SetHeader(Pbbp2.HeaderNames.BizRt, "20");

        Assert.HasCount(1, frame.Headers);
        Assert.AreEqual("20", frame.Header(Pbbp2.HeaderNames.BizRt));
    }

    /// <summary>
    /// 平台以后加字段时,解码不能崩 —— 未知字段按线格式跳过就好。
    /// </summary>
    [TestMethod]
    public void Decode_SkipsUnknownFields()
    {
        byte[] known = Pbbp2.Encode(new Pbbp2.Frame { SeqId = 5, Method = Pbbp2.FrameTypeData });
        // 手工追加两个未知字段:15 号(varint)与 16 号(长度前缀)。
        // 16 号的 tag 是 130,超过一个字节,所以它本身也得按 varint 写成 0x82 0x01 ——
        // 写成单字节的话解码器会把它当成"还有后续字节",整帧就歪了。
        byte[] withUnknown =
        [
            .. known,
            (15 << 3) | 0, 0x96, 0x01,          // 字段 15 = varint 150
            0x82, 0x01, 3, 1, 2, 3              // 字段 16 = bytes {1,2,3}
        ];

        Pbbp2.Frame decoded = Pbbp2.Decode(withUnknown);

        Assert.AreEqual(5ul, decoded.SeqId);
        Assert.AreEqual(Pbbp2.FrameTypeData, decoded.Method);
    }

    /// <summary>截断的字节流要抛,不能读出一个"看起来还行"的半截帧。</summary>
    [TestMethod]
    public void Decode_ThrowsOnTruncatedInput()
    {
        byte[] full = Pbbp2.Encode(new Pbbp2.Frame
        {
            SeqId = 1,
            Payload = Encoding.UTF8.GetBytes("0123456789")
        });

        byte[] truncated = full[..^4];

        Assert.ThrowsExactly<InvalidDataException>(() => Pbbp2.Decode(truncated));
    }
}

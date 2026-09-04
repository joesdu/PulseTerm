using System.Text;

namespace VelaShell.Plugin.Ai.Bridge.Channels.Feishu;

/// <summary>
/// 飞书长连接的帧(pbbp2)编解码。
/// </summary>
/// <remarks>
/// <b>手写而不是引 protobuf 运行时。</b>整份协议只有两个 message、十一个字段,全是 varint
/// 与长度前缀两种线格式;为它多背一个 Google.Protobuf 依赖(以及随之而来的 .proto 生成步骤)
/// 不划算。字段号照官方 Go SDK 的 <c>ws/pbbp2.pb.go</c>:
/// <code>
/// Header { key = 1 (string); value = 2 (string) }
/// Frame  { SeqID = 1 (uint64); LogID = 2 (uint64); service = 3 (int32); method = 4 (int32);
///          headers = 5 (repeated Header); payload_encoding = 6 (string);
///          payload_type = 7 (string); payload = 8 (bytes); LogIDNew = 9 (string) }
/// </code>
/// 解码遇到不认识的字段按线格式跳过 —— 平台加字段时不该把连接搞断。
/// </remarks>
internal static class Pbbp2
{
    /// <summary>帧类型(<c>Frame.method</c>)。</summary>
    public const int FrameTypeControl = 0;

    /// <summary>帧类型(<c>Frame.method</c>)。</summary>
    public const int FrameTypeData = 1;

    /// <summary>一帧。</summary>
    public sealed class Frame
    {
        /// <summary>序号(应答时原样回去)。</summary>
        public ulong SeqId { get; set; }

        /// <summary>日志 id(应答时原样回去)。</summary>
        public ulong LogId { get; set; }

        /// <summary>服务 id(建连时从 ws 地址的 query 里取)。</summary>
        public int Service { get; set; }

        /// <summary>帧类型:<see cref="FrameTypeControl" /> / <see cref="FrameTypeData" />。</summary>
        public int Method { get; set; }

        /// <summary>头(<c>type</c> / <c>message_id</c> / <c>sum</c> / <c>seq</c> / <c>trace_id</c> / <c>biz_rt</c>)。</summary>
        public List<KeyValuePair<string, string>> Headers { get; } = [];

        /// <summary>载荷编码(平台目前不填)。</summary>
        public string PayloadEncoding { get; set; } = "";

        /// <summary>载荷类型(平台目前不填)。</summary>
        public string PayloadType { get; set; } = "";

        /// <summary>载荷(事件是一段 JSON)。</summary>
        public byte[] Payload { get; set; } = [];

        /// <summary>新版日志 id。</summary>
        public string LogIdNew { get; set; } = "";

        /// <summary>取一个头(没有则返回空串)。</summary>
        public string Header(string key)
        {
            foreach ((string k, string v) in Headers)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                {
                    return v;
                }
            }
            return "";
        }

        /// <summary>取一个整数头(取不到或不是数字则返回 <paramref name="fallback" />)。</summary>
        public int HeaderInt(string key, int fallback = 0)
            => int.TryParse(Header(key), out int value) ? value : fallback;

        /// <summary>加一个头(同名的先去掉,避免应答帧里出现两份 <c>biz_rt</c>)。</summary>
        public void SetHeader(string key, string value)
        {
            Headers.RemoveAll(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase));
            Headers.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    /// <summary>头的名字(与官方 SDK 一致)。</summary>
    public static class HeaderNames
    {
        /// <summary>消息类型:<c>event</c> / <c>card</c> / <c>ping</c> / <c>pong</c>。</summary>
        public const string Type = "type";

        /// <summary>消息 id(拆包后各片继承同一个)。</summary>
        public const string MessageId = "message_id";

        /// <summary>拆包总数(没拆包为 1)。</summary>
        public const string Sum = "sum";

        /// <summary>包序号(没拆包为 0)。</summary>
        public const string Seq = "seq";

        /// <summary>链路 id。</summary>
        public const string TraceId = "trace_id";

        /// <summary>业务处理耗时(毫秒),应答时带上。</summary>
        public const string BizRt = "biz_rt";
    }

    /// <summary>编成一帧字节。</summary>
    public static byte[] Encode(Frame frame)
    {
        var buffer = new List<byte>(256);
        WriteVarintField(buffer, 1, frame.SeqId);
        WriteVarintField(buffer, 2, frame.LogId);
        WriteVarintField(buffer, 3, (uint)frame.Service);
        WriteVarintField(buffer, 4, (uint)frame.Method);
        foreach ((string key, string value) in frame.Headers)
        {
            var header = new List<byte>(key.Length + value.Length + 8);
            WriteBytesField(header, 1, Encoding.UTF8.GetBytes(key));
            WriteBytesField(header, 2, Encoding.UTF8.GetBytes(value));
            WriteBytesField(buffer, 5, [.. header]);
        }
        if (frame.PayloadEncoding.Length > 0)
        {
            WriteBytesField(buffer, 6, Encoding.UTF8.GetBytes(frame.PayloadEncoding));
        }
        if (frame.PayloadType.Length > 0)
        {
            WriteBytesField(buffer, 7, Encoding.UTF8.GetBytes(frame.PayloadType));
        }
        if (frame.Payload.Length > 0)
        {
            WriteBytesField(buffer, 8, frame.Payload);
        }
        if (frame.LogIdNew.Length > 0)
        {
            WriteBytesField(buffer, 9, Encoding.UTF8.GetBytes(frame.LogIdNew));
        }
        return [.. buffer];
    }

    /// <summary>解一帧。字节流坏掉时抛 <see cref="InvalidDataException" />(由上层当作断线处理)。</summary>
    public static Frame Decode(ReadOnlySpan<byte> data)
    {
        var frame = new Frame();
        int index = 0;
        while (index < data.Length)
        {
            ulong tag = ReadVarint(data, ref index);
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            switch (field)
            {
                case 1 when wire == 0:
                    frame.SeqId = ReadVarint(data, ref index);
                    break;
                case 2 when wire == 0:
                    frame.LogId = ReadVarint(data, ref index);
                    break;
                case 3 when wire == 0:
                    frame.Service = (int)ReadVarint(data, ref index);
                    break;
                case 4 when wire == 0:
                    frame.Method = (int)ReadVarint(data, ref index);
                    break;
                case 5 when wire == 2:
                    frame.Headers.Add(DecodeHeader(ReadBytes(data, ref index)));
                    break;
                case 6 when wire == 2:
                    frame.PayloadEncoding = Encoding.UTF8.GetString(ReadBytes(data, ref index));
                    break;
                case 7 when wire == 2:
                    frame.PayloadType = Encoding.UTF8.GetString(ReadBytes(data, ref index));
                    break;
                case 8 when wire == 2:
                    frame.Payload = ReadBytes(data, ref index).ToArray();
                    break;
                case 9 when wire == 2:
                    frame.LogIdNew = Encoding.UTF8.GetString(ReadBytes(data, ref index));
                    break;
                default:
                    Skip(data, ref index, wire);
                    break;
            }
        }
        return frame;
    }

    private static KeyValuePair<string, string> DecodeHeader(ReadOnlySpan<byte> data)
    {
        string key = "", value = "";
        int index = 0;
        while (index < data.Length)
        {
            ulong tag = ReadVarint(data, ref index);
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (field == 1 && wire == 2)
            {
                key = Encoding.UTF8.GetString(ReadBytes(data, ref index));
            }
            else if (field == 2 && wire == 2)
            {
                value = Encoding.UTF8.GetString(ReadBytes(data, ref index));
            }
            else
            {
                Skip(data, ref index, wire);
            }
        }
        return new KeyValuePair<string, string>(key, value);
    }

    private static void WriteVarintField(List<byte> buffer, int field, ulong value)
    {
        WriteVarint(buffer, (ulong)(field << 3));
        WriteVarint(buffer, value);
    }

    private static void WriteBytesField(List<byte> buffer, int field, ReadOnlySpan<byte> value)
    {
        WriteVarint(buffer, (ulong)((field << 3) | 2));
        WriteVarint(buffer, (ulong)value.Length);
        foreach (byte b in value)
        {
            buffer.Add(b);
        }
    }

    private static void WriteVarint(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }
        buffer.Add((byte)value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int index)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (index >= data.Length || shift > 63)
            {
                throw new InvalidDataException("pbbp2: truncated varint.");
            }
            byte b = data[index++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
            shift += 7;
        }
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> data, ref int index)
    {
        int length = (int)ReadVarint(data, ref index);
        if (length < 0 || index + length > data.Length)
        {
            throw new InvalidDataException("pbbp2: truncated length-delimited field.");
        }
        ReadOnlySpan<byte> slice = data.Slice(index, length);
        index += length;
        return slice;
    }

    private static void Skip(ReadOnlySpan<byte> data, ref int index, int wire)
    {
        switch (wire)
        {
            case 0:
                ReadVarint(data, ref index);
                break;
            case 1:
                index += 8;
                break;
            case 2:
                ReadBytes(data, ref index);
                break;
            case 5:
                index += 4;
                break;
            default:
                throw new InvalidDataException($"pbbp2: unsupported wire type {wire}.");
        }
        if (index > data.Length)
        {
            throw new InvalidDataException("pbbp2: truncated frame.");
        }
    }
}

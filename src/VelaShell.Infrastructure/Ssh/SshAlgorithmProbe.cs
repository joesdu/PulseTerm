using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>对端在 <c>SSH_MSG_KEXINIT</c> 里报出来的算法名单(原样,不做规整)。</summary>
public sealed record SshPeerAlgorithms
{
    /// <summary>对端的版本串(<c>SSH-2.0-OpenSSH_9.5</c> 这种)。</summary>
    public required string ServerVersion { get; init; }

    /// <summary>密钥交换算法。</summary>
    public required IReadOnlyList<string> KeyExchange { get; init; }

    /// <summary>主机密钥算法。</summary>
    public required IReadOnlyList<string> HostKey { get; init; }

    /// <summary>加密算法(客户端 → 服务端)。</summary>
    public required IReadOnlyList<string> EncryptionClientToServer { get; init; }

    /// <summary>加密算法(服务端 → 客户端)。</summary>
    public required IReadOnlyList<string> EncryptionServerToClient { get; init; }

    /// <summary>完整性算法(客户端 → 服务端)。</summary>
    public required IReadOnlyList<string> MacClientToServer { get; init; }

    /// <summary>完整性算法(服务端 → 客户端)。</summary>
    public required IReadOnlyList<string> MacServerToClient { get; init; }
}

/// <summary>
/// 算法协商失败后的回探:再开一条 TCP,做一次 SSH 版本串交换,读对端的第一个
/// <c>SSH_MSG_KEXINIT</c>,把它提供的算法名单取出来。
/// <para>
/// 存在的理由是 <c>KeyExchangeFailed</c> 这个错误本身什么都没说明:用户只看到"连不上",
/// 而真正该知道的是"对端只提供 aes128-ctr,本客户端一个都不支持"。这条信息没法从失败的
/// 连接里拿到 —— 底层库没有把对端的 KEXINIT 暴露出来 —— 所以只能再问一次。
/// </para>
/// <para>
/// KEXINIT 是握手的第一个包,处在**加密之前**的明文阶段,因此读它既不需要认证,也不发送
/// 任何凭据:本探针只发一行版本串,读一个包,然后关闭。代价是对端会多看到一条建立后立刻
/// 断开的连接(日志里多一行),所以只在确认协商失败后跑一次,绝不放在正常连接路径上。
/// </para>
/// </summary>
public static class SshAlgorithmProbe
{
    /// <summary>SSH_MSG_KEXINIT。</summary>
    private const byte MsgKexInit = 20;

    /// <summary>RFC 4253 §4.2:版本串含 CR LF 不超过 255 字节。</summary>
    private const int MaxIdentificationLine = 255;

    /// <summary>版本串之前允许有若干行 banner(法律声明之类);给个上限免得被无限喂数据。</summary>
    private const int MaxIdentificationLines = 64;

    /// <summary>RFC 4253 §6 要求实现至少支持 35000 字节的包;KEXINIT 远小于此。</summary>
    private const int MaxPacketLength = 35000;

    /// <summary>msg 类型 1 字节 + cookie 16 字节,后面才是第一个 name-list。</summary>
    private const int CookieEnd = 1 + 16;

    /// <summary>KEXINIT 里的 name-list 个数(kex、主机密钥、加密 ×2、MAC ×2、压缩 ×2、语言 ×2)。</summary>
    private const int NameListCount = 10;

    /// <summary>
    /// 探一次对端算法;任何失败(连不上、超时、不是 SSH、包读不全)一律返回 <see langword="null" />,
    /// 绝不抛 —— 调用方正走在错误路径上,诊断失败最多是少一段说明,不能再掀一次异常。
    /// </summary>
    public static async Task<SshPeerAlgorithms?> TryProbeAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            await using NetworkStream stream = tcp.GetStream();
            if (await ReadIdentificationAsync(stream, cts.Token).ConfigureAwait(false) is not { } serverVersion)
            {
                return null;
            }
            // 对端要等我们的版本串才会发 KEXINIT(OpenSSH 会先发,但不是所有实现都这样)。
            byte[] identification = Encoding.ASCII.GetBytes("SSH-2.0-VelaShell_Probe\r\n");
            await stream.WriteAsync(identification, cts.Token).ConfigureAwait(false);
            return await ReadKexInitAsync(stream, serverVersion, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>读到第一行以 <c>SSH-</c> 开头的版本串;之前的 banner 行原样丢掉。</summary>
    private static async Task<string?> ReadIdentificationAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new StringBuilder(MaxIdentificationLine);
        byte[] one = new byte[1];
        for (int lines = 0; lines < MaxIdentificationLines;)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                return null;
            }
            if (one[0] != (byte)'\n')
            {
                // 超长且还没换行:对面多半不是 SSH(HTTP 服务、TLS 端口),别再往内存里堆。
                if (line.Length >= MaxIdentificationLine)
                {
                    return null;
                }
                line.Append((char)one[0]);
                continue;
            }
            string text = line.ToString().TrimEnd('\r');
            line.Clear();
            lines++;
            if (text.StartsWith("SSH-", StringComparison.Ordinal))
            {
                return text;
            }
        }
        return null;
    }

    /// <summary>读一个明文二进制包并按 KEXINIT 解析。</summary>
    /// <remarks>
    /// 明文阶段的包结构(RFC 4253 §6):<c>uint32 包长 | byte 填充长 | payload | 填充</c>。
    /// 这里不做 MAC 校验 —— 密钥还没协商出来,明文阶段本来就没有 MAC。
    /// </remarks>
    private static async Task<SshPeerAlgorithms?> ReadKexInitAsync(
        Stream stream, string serverVersion, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        uint packetLength = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (packetLength is < 8 or > MaxPacketLength)
        {
            return null;
        }
        byte[] packet = new byte[packetLength];
        await stream.ReadExactlyAsync(packet, cancellationToken).ConfigureAwait(false);

        int payloadLength = (int)packetLength - 1 - packet[0];
        if (payloadLength <= CookieEnd)
        {
            return null;
        }
        ReadOnlySpan<byte> payload = packet.AsSpan(1, payloadLength);
        if (payload[0] != MsgKexInit)
        {
            return null;
        }

        int offset = CookieEnd;
        string[] lists = new string[NameListCount];
        for (int i = 0; i < NameListCount; i++)
        {
            if (!TryReadNameList(payload, ref offset, out lists[i]))
            {
                return null;
            }
        }
        return new SshPeerAlgorithms
        {
            ServerVersion = serverVersion,
            KeyExchange = Split(lists[0]),
            HostKey = Split(lists[1]),
            EncryptionClientToServer = Split(lists[2]),
            EncryptionServerToClient = Split(lists[3]),
            MacClientToServer = Split(lists[4]),
            MacServerToClient = Split(lists[5])
        };
    }

    /// <summary>读一条 <c>uint32 长度 + ASCII</c> 的 name-list;越界即判包不完整。</summary>
    private static bool TryReadNameList(ReadOnlySpan<byte> payload, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset + 4 > payload.Length)
        {
            return false;
        }
        uint length = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
        offset += 4;
        if (length > (uint)(payload.Length - offset))
        {
            return false;
        }
        value = Encoding.ASCII.GetString(payload.Slice(offset, (int)length));
        offset += (int)length;
        return true;
    }

    private static string[] Split(string nameList) =>
        nameList.Length == 0 ? [] : nameList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

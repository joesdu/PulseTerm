using System.Text;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.ZModem.Model;
using VelaShell.Core.ZModem.Protocol;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.Tests.FileTransfer;

namespace VelaShell.Core.Tests.ZModem;

/// <summary>
/// 极简 ZMODEM 发送方:只实现足以驱动 ZModemReceiver 的一侧(ZFILE→等 ZRPOS→ZDATA→ZEOF→ZFIN),
/// 用于在内存双工上做进程内互操作测试。
/// </summary>
internal sealed class TestSender(IByteDuplex duplex, bool useCrc32, int subpacketSize = 1024)
{
    private readonly ZModemFrameReader _reader = new(duplex);
    private readonly ZModemHeaderFormat _dataFormat = useCrc32 ? ZModemHeaderFormat.Binary32 : ZModemHeaderFormat.Binary16;
    private readonly bool _useCrc32 = useCrc32;

    public async Task SendBatchAsync((string Name, byte[] Data)[] files, CancellationToken ct)
    {
        // 等接收方的 ZRINIT。
        await WaitForAsync(ZModemFrameType.ZRINIT, ct).ConfigureAwait(false);

        foreach ((string name, byte[] data) in files)
        {
            await SendFileAsync(name, data, ct).ConfigureAwait(false);
        }

        // 结束握手:ZFIN ↔ ZFIN,再发 "OO"。
        await WriteHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZFIN), ZModemHeaderFormat.Hex, ct).ConfigureAwait(false);
        await WaitForAsync(ZModemFrameType.ZFIN, ct).ConfigureAwait(false);
        await duplex.WriteAsync("OO"u8.ToArray(), ct).ConfigureAwait(false); // "OO"
        await duplex.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task SendFileAsync(string name, byte[] data, CancellationToken ct)
    {
        // ZFILE 帧头 + 文件信息子包。
        await WriteHeaderAsync(ZModemHeader.Empty(ZModemFrameType.ZFILE), _dataFormat, ct).ConfigureAwait(false);
        var info = new List<byte>();
        info.AddRange(Encoding.ASCII.GetBytes(name));
        info.Add(0);
        info.AddRange(Encoding.ASCII.GetBytes($"{data.Length} 0 0 0 1 {data.Length}"));
        info.Add(0);
        byte[] infoWire = ZModemSubpacket.Write(info.ToArray(), ZModemSubpacketEnd.EndNoAck, _useCrc32);
        await duplex.WriteAsync(infoWire, ct).ConfigureAwait(false);

        // 等 ZRPOS(接收方就绪,给出起始偏移)。
        ZModemHeaderResult rpos = await WaitForAsync(ZModemFrameType.ZRPOS, ct).ConfigureAwait(false);
        int offset = (int)rpos.Header.Position;

        // ZDATA 帧头 + 数据子包流。
        await WriteHeaderAsync(ZModemHeader.WithPosition(ZModemFrameType.ZDATA, (uint)offset), _dataFormat, ct).ConfigureAwait(false);
        int pos = offset;
        while (pos < data.Length)
        {
            int len = Math.Min(subpacketSize, data.Length - pos);
            bool last = pos + len >= data.Length;
            ZModemSubpacketEnd end = last ? ZModemSubpacketEnd.EndNoAck : ZModemSubpacketEnd.MoreNoAck;
            byte[] wire = ZModemSubpacket.Write(data.AsSpan(pos, len), end, _useCrc32);
            await duplex.WriteAsync(wire, ct).ConfigureAwait(false);
            pos += len;
        }
        if (data.Length == offset)
        {
            // 空文件:仍需一个结束子包。
            byte[] wire = ZModemSubpacket.Write([], ZModemSubpacketEnd.EndNoAck, _useCrc32);
            await duplex.WriteAsync(wire, ct).ConfigureAwait(false);
        }

        // ZEOF(位置 = 文件总长)。
        await WriteHeaderAsync(ZModemHeader.WithPosition(ZModemFrameType.ZEOF, (uint)data.Length), _dataFormat, ct).ConfigureAwait(false);

        // 接收方完成该文件后回 ZRINIT,准备下一个。
        await WaitForAsync(ZModemFrameType.ZRINIT, ct).ConfigureAwait(false);
    }

    private async Task<ZModemHeaderResult> WaitForAsync(ZModemFrameType type, CancellationToken ct)
    {
        while (true)
        {
            ZModemHeaderResult r = await _reader.ReadHeaderAsync(ct).ConfigureAwait(false);
            if (r.Status == ZModemReadStatus.Header && r.Header.Type == type)
            {
                return r;
            }
            if (r.Status is ZModemReadStatus.EndOfStream or ZModemReadStatus.Cancelled)
            {
                throw new InvalidOperationException($"Sender aborted waiting for {type}: {r.Status}");
            }
        }
    }

    private async Task WriteHeaderAsync(ZModemHeader header, ZModemHeaderFormat format, CancellationToken ct)
    {
        await duplex.WriteAsync(ZModemFrameWriter.Write(header, format), ct).ConfigureAwait(false);
        await duplex.FlushAsync(ct).ConfigureAwait(false);
    }
}

namespace VelaShell.Terminal;

/// <summary>
/// 一次性回显抑制器:连接初始化命令是作为击键注入远端 shell 的,PTY 会把它原样回显
/// (可能出现两次:内核规范模式回显 + readline 预输入重绘),需静默执行以避免干扰终端输出。
/// 本类在时间窗内从输出流里剥除该命令的回显字节;回显可能被网络分块任意切开,
/// 因此做跨块流式匹配:块尾的部分命中(≥<see cref="MinHold" /> 字节,避免把提示符
/// 尾部空格之类的巧合扣住)先扣下,下一块续判。超时或命中数用尽后放行一切。
/// </summary>
public sealed class EchoSuppressor
{
    /// <summary>块尾部分命中至少要匹配这么多字节才扣住等下一块,防止普通输出被延迟显示。</summary>
    private const int MinHold = 4;

    private readonly DateTime _deadline;

    private readonly byte[] _needle;
    private int _held; // 上一块末尾已匹配的 needle 前缀长度(字节内容即 needle[.._held])
    private int _hitsLeft;

    /// <summary>
    /// 创建回显抑制器。
    /// </summary>
    /// <param name="needle">要从输出流中剥除的命令回显字节序列,长度须不小于 <see cref="MinHold" />。</param>
    /// <param name="maxHits">最多剥除多少次该回显(用尽后放行一切)。</param>
    /// <param name="window">抑制生效的时间窗,超时后放行一切。</param>
    public EchoSuppressor(byte[] needle, int maxHits, TimeSpan window)
    {
        if (needle.Length < MinHold)
        {
            throw new ArgumentException(@"Needle too short to suppress safely.", nameof(needle));
        }
        _needle = needle;
        _hitsLeft = maxHits;
        _deadline = DateTime.UtcNow + window;
    }

    /// <summary>
    /// 抑制是否已失效:命中次数用尽或超过时间窗;失效后 <see cref="Process" /> 放行一切。
    /// </summary>
    public bool Expired => _hitsLeft <= 0 || DateTime.UtcNow > _deadline;

    /// <summary>
    /// 取回并清空当前扣住的块尾部分命中,交还终端。
    /// <para>
    /// 扣住的字节只会在<b>下一次</b> <see cref="Process" /> 里放出来;调用方一旦因
    /// <see cref="Expired" /> 弃用本实例,就再也没有下一次——不在此处取回,那几个字节
    /// 就被永久吞掉了(抑制窗恰好在扣住的同一块内到期时发生)。
    /// </para>
    /// </summary>
    /// <returns>此前扣住、尚未喂终端的字节;没有则为空数组。</returns>
    public byte[] TakeHeld()
    {
        if (_held == 0)
        {
            return [];
        }
        byte[] result = _needle[.._held];
        _held = 0;
        return result;
    }

    /// <summary>
    /// 处理一块输出字节,剥除其中匹配到的命令回显;块尾的部分命中会被扣下,合并进下一块续判。
    /// </summary>
    public byte[] Process(byte[] data)
    {
        if (Expired)
        {
            return ReleaseHeld(data);
        }

        // 快路径:无扣留前缀且整块不含 needle 首字节 → 原样返回,零分配零拷贝。
        // 抑制窗内绝大多数输出块(MOTD、提示符刷新)都走这里。
        if (_held == 0 && Array.IndexOf(data, _needle[0]) < 0)
        {
            return data;
        }

        // 把上次扣住的前缀接回输入统一扫描(其内容与 needle 前缀相同,可直接重建)。
        byte[] input;
        if (_held > 0)
        {
            input = new byte[_held + data.Length];
            Array.Copy(_needle, 0, input, 0, _held);
            Array.Copy(data, 0, input, _held, data.Length);
            _held = 0;
        }
        else
        {
            input = data;
        }
        var output = new MemoryStream(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            if (_hitsLeft > 0 && input[i] == _needle[0])
            {
                int matched = MatchFrom(input, i);
                if (matched == _needle.Length)
                {
                    // 整段命中:剥除。
                    i += matched;
                    _hitsLeft--;
                    continue;
                }
                if (matched > 0 && i + matched == input.Length && matched >= MinHold)
                {
                    // 块尾部分命中:扣住,等下一块续判。
                    _held = matched;
                    i += matched;
                    continue;
                }
            }
            output.WriteByte(input[i]);
            i++;
        }
        return output.ToArray();
    }

    /// <summary>
    /// 从 <paramref name="start" /> 起与 needle 连续匹配的字节数;首个不匹配即返回 0..k。
    /// 返回值等于可用长度表示"到块尾都还在匹配"。
    /// </summary>
    private int MatchFrom(byte[] input, int start)
    {
        int limit = Math.Min(_needle.Length, input.Length - start);
        for (int k = 0; k < limit; k++)
        {
            if (input[start + k] != _needle[k])
            {
                return 0;
            }
        }
        return limit;
    }

    private byte[] ReleaseHeld(byte[] data)
    {
        if (_held == 0)
        {
            return data;
        }
        byte[] result = new byte[_held + data.Length];
        Array.Copy(_needle, 0, result, 0, _held);
        Array.Copy(data, 0, result, _held, data.Length);
        _held = 0;
        return result;
    }
}

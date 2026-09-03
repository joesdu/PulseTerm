using System.Text;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>二维码纠错等级。数值即 ISO/IEC 18004 里的表序,不是格式信息位(那份另有映射)。</summary>
internal enum QrEcc
{
    /// <summary>约 7% 可恢复。</summary>
    Low = 0,

    /// <summary>约 15% 可恢复。屏幕上给人扫的链接用这一档就够。</summary>
    Medium = 1,

    /// <summary>约 25% 可恢复。</summary>
    Quartile = 2,

    /// <summary>约 30% 可恢复。</summary>
    High = 3
}

/// <summary>
/// 二维码编码器(ISO/IEC 18004),只出模块矩阵,不碰任何绘图 API。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自己写。</b>这里原来用的是 QRCoder,它在 netstandard2.0 目标上传递依赖
/// <c>System.Drawing.Common 6.0.0</c>(再带 <c>Microsoft.Win32.SystemEvents</c>)——
/// 那个库早就不再跨平台(6.0 之后官方只支持 Windows,Linux 上要 libgdiplus),
/// 而且它带来的 <c>runtimes/{win,unix}/lib/net6.0/</c> 目录名里有点号,
/// 会被 macOS 的 <c>codesign</c> 当成嵌套 bundle 直接把打包炸掉(1.4.8 踩过,
/// 与 <c>plugins/README.md</c> 记的插件目录名那次是同一个坑)。
/// 我们只需要"把一条邀请链接画成码",为此拖进一条不跨平台的依赖不划算。
/// </para>
/// <para>
/// <b>只做字节模式(UTF-8)。</b>数字/字母数字模式只在纯数字或纯大写内容上更紧凑,
/// 而这里编的全是带小写的 URL,那两种模式一次也用不上 —— 少两条分支,少两处能错的地方。
/// 字节模式是所有识读器都支持的基本模式,不存在兼容性问题。
/// </para>
/// <para>
/// 算法是公开规范,实现按 ISO/IEC 18004 的标准做法:选版本 → 拼比特流 → 分块算
/// Reed-Solomon 纠错码 → 交织 → 铺功能图形与数据 → 八种掩码各算一次罚分取最优。
/// 正确性由 <c>QrCodeTests</c> 的黄金用例把关(那些矩阵是与独立实现逐格比对过的)。
/// </para>
/// </remarks>
internal sealed class QrCode
{
    /// <summary>字节模式的模式指示符。</summary>
    private const int ByteMode = 0b0100;

    /// <summary>GF(256) 的本原多项式 x⁸+x⁴+x³+x²+1,Reed-Solomon 乘法的约简用。</summary>
    private const int GfPrimitive = 0x11D;

    /// <summary>格式信息的 BCH(15,5) 生成多项式。</summary>
    private const int FormatGenerator = 0x537;

    /// <summary>格式信息的固定掩码,防止全零格式串被误认成功能图形。</summary>
    private const int FormatMask = 0x5412;

    /// <summary>版本信息的 BCH(18,6) 生成多项式(版本 7 起才有这块)。</summary>
    private const int VersionGenerator = 0x1F25;

    /// <summary>纠错等级到格式信息位的映射:L=01、M=00、Q=11、H=10(不是表序)。</summary>
    private static readonly int[] FormatBitsForEcc = [1, 0, 3, 2];

    /// <summary>每块的纠错码字数,按 [纠错等级][版本] 取;下标 0 占位,版本从 1 起。</summary>
    private static readonly byte[][] EccCodewordsPerBlock =
    [
        [0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [0, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [0, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30]
    ];

    /// <summary>纠错块数,按 [纠错等级][版本] 取;下标 0 占位,版本从 1 起。</summary>
    private static readonly byte[][] NumEccBlocks =
    [
        [0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [0, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [0, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81]
    ];

    /// <summary>深色为 true。按行优先存,长度 <see cref="Size" />²。</summary>
    private readonly bool[] _modules;

    /// <summary>功能图形(定位/校正/时序/格式/版本)占用的格子,铺数据与打掩码时都要避开。</summary>
    private readonly bool[] _isFunction;

    private readonly QrEcc _ecc;

    private QrCode(int version, QrEcc ecc)
    {
        Version = version;
        _ecc = ecc;
        Size = version * 4 + 17;
        _modules = new bool[Size * Size];
        _isFunction = new bool[Size * Size];
    }

    /// <summary>版本号 1–40,决定边长(<c>版本 × 4 + 17</c>)。</summary>
    public int Version { get; }

    /// <summary>边长(模块数),不含静默区。</summary>
    public int Size { get; }

    /// <summary>取一个模块,深色为 true。原点在左上角。</summary>
    public bool this[int x, int y] => _modules[(y * Size) + x];

    /// <summary>把一段文本编成二维码。</summary>
    /// <param name="text">要编的内容,按 UTF-8 走字节模式。</param>
    /// <param name="ecc">纠错等级。</param>
    /// <exception cref="ArgumentException">内容超出版本 40 在该纠错等级下的容量。</exception>
    public static QrCode Encode(string text, QrEcc ecc = QrEcc.Medium)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = ChooseVersion(data.Length, ecc);
        QrCode qr = new(version, ecc);
        qr.Draw(qr.BuildCodewords(data));
        return qr;
    }

    /// <summary>
    /// 挑能装下的最小版本。
    /// </summary>
    /// <remarks>
    /// 字符数指示符的宽度随版本变(1–9 是 8 位,10 起是 16 位),所以容量判断必须逐版本算,
    /// 不能先按某个宽度估一次再回头修正。
    /// </remarks>
    private static int ChooseVersion(int byteCount, QrEcc ecc)
    {
        for (int version = 1; version <= 40; version++)
        {
            int capacityBits = GetDataCodewords(version, ecc) * 8;
            int headerBits = 4 + (version <= 9 ? 8 : 16);
            if (headerBits + (byteCount * 8) <= capacityBits)
            {
                return version;
            }
        }
        throw new ArgumentException(
            $"内容太长({byteCount} 字节),超出二维码在该纠错等级下的最大容量。", nameof(byteCount));
    }

    /// <summary>该版本整块符号能放下的数据模块数(不含功能图形,含尾部不足一字节的余位)。</summary>
    private static int GetNumRawDataModules(int version)
    {
        int result = ((16 * version) + 128) * version + 64;
        if (version >= 2)
        {
            int numAlign = (version / 7) + 2;
            // 减去校正图形占的格子,再把它与时序图形重叠的部分加回来。
            result -= ((25 * numAlign) - 10) * numAlign - 55;
            if (version >= 7)
            {
                // 版本 7 起两块 6×3 的版本信息。
                result -= 36;
            }
        }
        return result;
    }

    /// <summary>该版本 + 纠错等级下能放的数据码字数(总码字减纠错码字)。</summary>
    private static int GetDataCodewords(int version, QrEcc ecc) =>
        (GetNumRawDataModules(version) / 8)
        - (EccCodewordsPerBlock[(int)ecc][version] * NumEccBlocks[(int)ecc][version]);

    /// <summary>GF(256) 上的乘法,按本原多项式约简。</summary>
    private static byte GfMultiply(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * GfPrimitive);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    /// <summary>算出 <paramref name="degree" /> 次的 Reed-Solomon 生成多项式(不含最高次项)。</summary>
    private static byte[] ReedSolomonDivisor(int degree)
    {
        byte[] result = new byte[degree];
        result[degree - 1] = 1;
        // 逐个乘上 (x - α^i),i 从 0 到 degree-1。
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = GfMultiply(result[j], (byte)root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }
            root = GfMultiply((byte)root, 0x02);
        }
        return result;
    }

    /// <summary>数据除以生成多项式的余数,即这一块的纠错码字。</summary>
    private static byte[] ReedSolomonRemainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        byte[] result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
            {
                result[i] ^= GfMultiply(divisor[i], factor);
            }
        }
        return result;
    }

    private static bool GetBit(int value, int index) => ((value >> index) & 1) != 0;

    /// <summary>拼数据比特流,再分块加纠错码并交织成最终码字序列。</summary>
    private byte[] BuildCodewords(byte[] data)
    {
        int dataCodewords = GetDataCodewords(Version, _ecc);
        int capacityBits = dataCodewords * 8;

        QrBitBuffer bits = new();
        bits.Append(ByteMode, 4);
        bits.Append(data.Length, Version <= 9 ? 8 : 16);
        foreach (byte b in data)
        {
            bits.Append(b, 8);
        }
        // 结束符最多 4 位,剩余空间不足时按剩多少写多少;再补齐到整字节。
        bits.Append(0, Math.Min(4, capacityBits - bits.Length));
        bits.Append(0, (8 - (bits.Length % 8)) % 8);
        // 余下的位用规范指定的 0xEC / 0x11 交替填满(异或 0xFD 就是在这两个值之间来回)。
        for (int pad = 0xEC; bits.Length < capacityBits; pad ^= 0xEC ^ 0x11)
        {
            bits.Append(pad, 8);
        }

        return AddEccAndInterleave(bits.ToArray());
    }

    /// <summary>
    /// 按版本与纠错等级分块,逐块算纠错码,再按规范的交织顺序拼回一条序列。
    /// </summary>
    /// <remarks>
    /// 块长可能差一个字节:前 <c>numShort</c> 块短一位。交织时给短块在数据段末尾留了个"空洞",
    /// 遍历到那一列时跳过它 —— 这样两种块长可以用同一段循环走完。
    /// </remarks>
    private byte[] AddEccAndInterleave(byte[] data)
    {
        int numBlocks = NumEccBlocks[(int)_ecc][Version];
        int eccLen = EccCodewordsPerBlock[(int)_ecc][Version];
        int rawCodewords = GetNumRawDataModules(Version) / 8;
        int numShort = numBlocks - (rawCodewords % numBlocks);
        int shortLen = rawCodewords / numBlocks;

        byte[] divisor = ReedSolomonDivisor(eccLen);
        byte[][] blocks = new byte[numBlocks][];
        for (int i = 0, offset = 0; i < numBlocks; i++)
        {
            int dataLen = shortLen - eccLen + (i < numShort ? 0 : 1);
            ReadOnlySpan<byte> chunk = data.AsSpan(offset, dataLen);
            offset += dataLen;

            byte[] block = new byte[shortLen + 1];
            chunk.CopyTo(block);
            ReedSolomonRemainder(chunk, divisor).CopyTo(block.AsSpan(block.Length - eccLen));
            blocks[i] = block;
        }

        byte[] result = new byte[rawCodewords];
        for (int i = 0, k = 0; i < shortLen + 1; i++)
        {
            for (int j = 0; j < numBlocks; j++)
            {
                // 短块在数据段最后一格是不存在的,跳过。
                if (i != shortLen - eccLen || j >= numShort)
                {
                    result[k++] = blocks[j][i];
                }
            }
        }
        return result;
    }

    /// <summary>铺功能图形与数据,再选出罚分最低的掩码定稿。</summary>
    private void Draw(byte[] codewords)
    {
        DrawFunctionPatterns();
        DrawCodewords(codewords);

        int bestMask = 0;
        int minPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            int penalty = GetPenaltyScore();
            if (penalty < minPenalty)
            {
                bestMask = mask;
                minPenalty = penalty;
            }
            // 掩码是异或,再来一次就撤销了。
            ApplyMask(mask);
        }
        ApplyMask(bestMask);
        DrawFormatBits(bestMask);
    }

    private void SetFunctionModule(int x, int y, bool dark)
    {
        _modules[(y * Size) + x] = dark;
        _isFunction[(y * Size) + x] = true;
    }

    private void DrawFunctionPatterns()
    {
        // 时序图形:第 6 行与第 6 列的明暗交替。
        for (int i = 0; i < Size; i++)
        {
            SetFunctionModule(6, i, i % 2 == 0);
            SetFunctionModule(i, 6, i % 2 == 0);
        }

        // 三个定位图形(连同分隔带),中心分别在三个角。
        DrawFinderPattern(3, 3);
        DrawFinderPattern(Size - 4, 3);
        DrawFinderPattern(3, Size - 4);

        // 校正图形:三个定位图形所在的角上不放。
        int[] positions = GetAlignmentPatternPositions();
        int last = positions.Length - 1;
        for (int i = 0; i <= last; i++)
        {
            for (int j = 0; j <= last; j++)
            {
                bool corner = (i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0);
                if (!corner)
                {
                    DrawAlignmentPattern(positions[i], positions[j]);
                }
            }
        }

        // 先占位,真正的格式信息在选定掩码后再写一遍。
        DrawFormatBits(0);
        DrawVersionBits();
    }

    /// <summary>7×7 的定位图形加外圈分隔带,按到中心的切比雪夫距离决定明暗。</summary>
    private void DrawFinderPattern(int centerX, int centerY)
    {
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int x = centerX + dx;
                int y = centerY + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size)
                {
                    int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunctionModule(x, y, distance is not (2 or 4));
                }
            }
        }
    }

    /// <summary>5×5 的校正图形:中心一点、外圈一环。</summary>
    private void DrawAlignmentPattern(int centerX, int centerY)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                SetFunctionModule(centerX + dx, centerY + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    /// <summary>校正图形的中心坐标。版本 1 一个都没有;版本 32 的步距是规范里的特例。</summary>
    private int[] GetAlignmentPatternPositions()
    {
        if (Version == 1)
        {
            return [];
        }
        int count = (Version / 7) + 2;
        int step = Version == 32 ? 26 : ((Version * 4) + (count * 2) + 1) / ((count * 2) - 2) * 2;
        int[] result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = Size - 7; i >= 1; i--, pos -= step)
        {
            result[i] = pos;
        }
        return result;
    }

    /// <summary>写两份格式信息(纠错等级 + 掩码号,带 BCH 纠错并异或固定掩码)。</summary>
    private void DrawFormatBits(int mask)
    {
        int data = (FormatBitsForEcc[(int)_ecc] << 3) | mask;
        int remainder = data;
        for (int i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * FormatGenerator);
        }
        int bits = (((data << 10) | remainder) ^ FormatMask) & 0x7FFF;

        // 第一份:左上角定位图形周围。
        for (int i = 0; i <= 5; i++)
        {
            SetFunctionModule(8, i, GetBit(bits, i));
        }
        SetFunctionModule(8, 7, GetBit(bits, 6));
        SetFunctionModule(8, 8, GetBit(bits, 7));
        SetFunctionModule(7, 8, GetBit(bits, 8));
        for (int i = 9; i < 15; i++)
        {
            SetFunctionModule(14 - i, 8, GetBit(bits, i));
        }

        // 第二份:右上与左下,给识读器一份冗余。
        for (int i = 0; i < 8; i++)
        {
            SetFunctionModule(Size - 1 - i, 8, GetBit(bits, i));
        }
        for (int i = 8; i < 15; i++)
        {
            SetFunctionModule(8, Size - 15 + i, GetBit(bits, i));
        }
        // 规范规定恒为深色的那一格。
        SetFunctionModule(8, Size - 8, true);
    }

    /// <summary>版本 7 起要在两个角上写版本号(6 位版本 + 12 位 BCH)。</summary>
    private void DrawVersionBits()
    {
        if (Version < 7)
        {
            return;
        }
        int remainder = Version;
        for (int i = 0; i < 12; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 11) * VersionGenerator);
        }
        int bits = (Version << 12) | remainder;
        for (int i = 0; i < 18; i++)
        {
            bool dark = GetBit(bits, i);
            int a = Size - 11 + (i % 3);
            int b = i / 3;
            SetFunctionModule(a, b, dark);
            SetFunctionModule(b, a, dark);
        }
    }

    /// <summary>把码字按规范的蛇形顺序铺进非功能格子:两列一组,自右向左,上下交替。</summary>
    private void DrawCodewords(byte[] codewords)
    {
        int bit = 0;
        int totalBits = codewords.Length * 8;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            // 第 6 列是时序图形,整列跳过(于是这一组变成第 5、4 两列)。
            if (right == 6)
            {
                right = 5;
            }
            for (int vert = 0; vert < Size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[(y * Size) + x] && bit < totalBits)
                    {
                        _modules[(y * Size) + x] = GetBit(codewords[bit >> 3], 7 - (bit & 7));
                        bit++;
                    }
                    // 剩下的余位保持浅色,规范允许。
                }
            }
        }
    }

    /// <summary>对所有非功能格子异或掩码图案。再调一次同样的掩码即可撤销。</summary>
    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (_isFunction[(y * Size) + x])
                {
                    continue;
                }
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => ((x / 3) + (y / 2)) % 2 == 0,
                    5 => (x * y % 2) + (x * y % 3) == 0,
                    6 => ((x * y % 2) + (x * y % 3)) % 2 == 0,
                    7 => (((x + y) % 2) + (x * y % 3)) % 2 == 0,
                    _ => throw new ArgumentOutOfRangeException(nameof(mask))
                };
                _modules[(y * Size) + x] ^= invert;
            }
        }
    }

    /// <summary>
    /// 规范的四条罚分规则,分数越低越好 —— 掩码就是按这个选出来的。
    /// </summary>
    /// <remarks>
    /// 规则的用意是让成品"看起来不规律":长条同色难以定位、2×2 同色块像噪点、
    /// 形似定位图形的序列会骗到识读器、明暗比例失衡会让阈值判断变难。
    /// </remarks>
    private int GetPenaltyScore()
    {
        int score = 0;
        score += PenaltyRuns();
        score += PenaltyBlocks();
        score += PenaltyFinderLike();
        score += PenaltyBalance();
        return score;
    }

    /// <summary>规则一:行或列上连续 5 个及以上同色,记 3 分,每多一个再加 1 分。</summary>
    private int PenaltyRuns()
    {
        int score = 0;
        for (int i = 0; i < Size; i++)
        {
            score += PenaltyRunsInLine(i, horizontal: true);
            score += PenaltyRunsInLine(i, horizontal: false);
        }
        return score;
    }

    private int PenaltyRunsInLine(int index, bool horizontal)
    {
        int score = 0;
        bool previous = horizontal ? this[0, index] : this[index, 0];
        int run = 1;
        for (int i = 1; i < Size; i++)
        {
            bool current = horizontal ? this[i, index] : this[index, i];
            if (current == previous)
            {
                run++;
                continue;
            }
            if (run >= 5)
            {
                score += run - 2;
            }
            previous = current;
            run = 1;
        }
        return score + (run >= 5 ? run - 2 : 0);
    }

    /// <summary>规则二:每个 2×2 的同色块记 3 分(重叠的块各记各的)。</summary>
    private int PenaltyBlocks()
    {
        int score = 0;
        for (int y = 0; y < Size - 1; y++)
        {
            for (int x = 0; x < Size - 1; x++)
            {
                bool corner = this[x, y];
                if (corner == this[x + 1, y] && corner == this[x, y + 1] && corner == this[x + 1, y + 1])
                {
                    score += 3;
                }
            }
        }
        return score;
    }

    /// <summary>
    /// 规则三:出现 <c>1:1:3:1:1</c> 且一侧带四个浅色模块的序列,每处 40 分 ——
    /// 那正是定位图形的比例,识读器会拿它找基准点,别处冒出来会把它带偏。
    /// </summary>
    private int PenaltyFinderLike()
    {
        const int pattern = 0b10111010000;
        const int reversed = 0b00001011101;
        const int window = 11;

        int score = 0;
        for (int i = 0; i < Size; i++)
        {
            int horizontal = 0;
            int vertical = 0;
            for (int j = 0; j < Size; j++)
            {
                horizontal = ((horizontal << 1) | (this[j, i] ? 1 : 0)) & ((1 << window) - 1);
                vertical = ((vertical << 1) | (this[i, j] ? 1 : 0)) & ((1 << window) - 1);
                if (j < window - 1)
                {
                    continue;
                }
                if (horizontal == pattern || horizontal == reversed)
                {
                    score += 40;
                }
                if (vertical == pattern || vertical == reversed)
                {
                    score += 40;
                }
            }
        }
        return score;
    }

    /// <summary>规则四:深色比例每偏离 50% 一个 5% 档位,加 10 分。</summary>
    private int PenaltyBalance()
    {
        int dark = 0;
        foreach (bool module in _modules)
        {
            if (module)
            {
                dark++;
            }
        }
        int total = _modules.Length;
        // 先乘 20 再整除总数,等价于"偏离量按 5% 向下取档",避免浮点。
        int k = ((Math.Abs((dark * 20) - (total * 10)) + total - 1) / total) - 1;
        return k * 10;
    }

    /// <summary>按位追加的小缓冲。二维码的数据量很小,不必为它做花哨的优化。</summary>
    private sealed class QrBitBuffer
    {
        private readonly List<byte> _bytes = [];

        public int Length { get; private set; }

        public void Append(int value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if ((Length & 7) == 0)
                {
                    _bytes.Add(0);
                }
                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(1 << (7 - (Length & 7)));
                }
                Length++;
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}

namespace VelaShell.Core.FileTransfer.Protocol;

/// <summary>
/// CRC-16/XMODEM 实现(多项式 0x1021,初值 0,不反转输入/输出,不做最终异或)。
/// ZMODEM 的十六进制帧头与 CRC16 二进制帧头/子包、以及 XMODEM/YMODEM 的数据块校验
/// 均使用此算法(名字里的 XMODEM 就是它的出处)。以查表法保证吞吐。
/// </summary>
public static class Crc16Xmodem
{
    private const ushort Polynomial = 0x1021;
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        ushort[] table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)(i << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ Polynomial)
                    : (ushort)(crc << 1);
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>基于初值 0 计算整段数据的 CRC-16/XMODEM。</summary>
    /// <param name="data">参与校验的字节序列。</param>
    /// <returns>16 位校验值。</returns>
    public static ushort Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>在已有 CRC 值上继续累加一段数据(用于分块喂入)。</summary>
    /// <param name="crc">上一次的 CRC 累加值(首次传 0)。</param>
    /// <param name="data">追加参与校验的字节序列。</param>
    /// <returns>更新后的 16 位校验值。</returns>
    public static ushort Update(ushort crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ Table[((crc >> 8) ^ b) & 0xFF]);
        }
        return crc;
    }

    /// <summary>在已有 CRC 值上累加单个字节。</summary>
    /// <param name="crc">上一次的 CRC 累加值。</param>
    /// <param name="b">追加参与校验的字节。</param>
    /// <returns>更新后的 16 位校验值。</returns>
    public static ushort Update(ushort crc, byte b) =>
        (ushort)((crc << 8) ^ Table[((crc >> 8) ^ b) & 0xFF]);

    // 注意:lrzsz 源码里的「补两个零字节增广」(updcrc(0, updcrc(0, crc)))是旧式 XMODEM
    // 算法(数据字节 XOR 进低位、查表只用旧高字节)的收尾步骤;它与本类使用的现代查表算法
    // (数据字节 XOR 进表索引)在数学上完全等价 —— 增广已经内建在 Compute/Update 里了。
    // 千万不要在此之上再补零:那是双重增广,产生的 CRC 与 lrzsz 线上值不符
    // (实测:lrzsz 对帧头 [06 00 00 00 00] 上链 0xCD85,即本算法的裸 Compute 值)。
}

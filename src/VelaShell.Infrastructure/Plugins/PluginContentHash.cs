using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// 算一个插件目录的内容指纹 —— 安装收据据此判断"装好之后有没有人动过它"。
/// </summary>
/// <remarks>
/// <para>
/// 从 <see cref="PluginManager" /> 拆出来的一簇(Q-01)。它是**篡改检测**的地基:
/// 指纹对不上的插件会被当作被动过而拒绝加载,所以这里的每一条规则都值得单独钉住。
/// </para>
/// <para>
/// <b>指纹怎么算的,以及为什么要这么算</b>:按相对路径排序后,逐个把
/// 「路径长度 + 路径 + 内容长度 + 内容」喂进 SHA-256。长度前缀不能省 —— 少了它,
/// <c>ab</c>+<c>c</c> 与 <c>a</c>+<c>bc</c> 会算出同一个指纹,攻击者可以借此挪动文件边界。
/// 路径分隔符一律归一成 <c>/</c>,否则同一份插件在 Windows 与 Linux 上指纹不同。
/// </para>
/// <para>
/// <b>符号链接一律拒绝</b>,不是跳过:一个指向 <c>/etc</c> 的链接会让指纹变成"外部内容的
/// 指纹",而那份外部内容随时会变;更糟的是它给了越界读写的口子。
/// </para>
/// </remarks>
public static class PluginContentHash
{
    /// <summary>停用标记文件名;它是宿主写的,不算插件内容。</summary>
    /// <remarks>
    /// 不排除它的话,用户在设置页停用一次插件就会改变指纹,下次启动即被判定为"被篡改"。
    /// 只排根目录下的那一个 —— 插件自己带的同名文件仍然算内容。
    /// </remarks>
    public const string DisabledMarkerName = ".disabled";

    /// <summary>
    /// 算目录的内容指纹。
    /// </summary>
    /// <param name="root">插件根目录。</param>
    /// <returns>小写十六进制的 SHA-256。</returns>
    /// <exception cref="InvalidDataException">根目录本身或其中任何一项是符号链接。</exception>
    public static string Compute(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullRoot = Path.GetFullPath(root);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Plugin root is a symbolic link.");
        }
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.TryPop(out string? directory))
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Plugin directory contains a symbolic link: {Path.GetRelativePath(fullRoot, child)}");
                }
                pending.Push(child);
            }
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (directory == fullRoot && Path.GetFileName(file).Equals(DisabledMarkerName, StringComparison.Ordinal))
                {
                    continue;
                }
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Plugin directory contains a symbolic link: {Path.GetRelativePath(fullRoot, file)}");
                }
                files.Add(file);
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> frame = stackalloc byte[8];

        // 读缓冲在循环外开一次并跨文件复用:原先每个文件都新开 64 KB,一个两百来个文件的
        // 插件光在这里就扔掉十几 MB 垃圾,而这条路径是安装/校验时的启动开销。
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (string file in files.OrderBy(f => Relative(fullRoot, f), StringComparer.Ordinal))
            {
                byte[] path = Encoding.UTF8.GetBytes(Relative(fullRoot, file));
                BinaryPrimitives.WriteInt32LittleEndian(frame, path.Length);
                hash.AppendData(frame[..4]);
                hash.AppendData(path);
                using FileStream stream = File.OpenRead(file);
                BinaryPrimitives.WriteInt64LittleEndian(frame, stream.Length);
                hash.AppendData(frame);
                int read;
                while ((read = stream.Read(buffer)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>相对路径,分隔符归一成 <c>/</c>(否则跨平台指纹不同)。</summary>
    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');
}

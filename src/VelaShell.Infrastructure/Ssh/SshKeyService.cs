using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 基于 ~/.ssh 目录的密钥管理:以 *.pub 公钥文件枚举密钥对,
/// 类型与 SHA256 指纹从公钥 blob 解析(与 OpenSSH `ssh-keygen -lf` 口径一致)。
/// </summary>
public sealed class SshKeyService(string? sshDirectory = null) : ISshKeyService
{
    private readonly string _sshDirectory = sshDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>枚举 ~/.ssh 目录下的密钥对,按公钥文件解析类型与指纹后返回。</summary>
    public Task<List<SshKeyInfo>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var keys = new List<SshKeyInfo>();
            if (!Directory.Exists(_sshDirectory))
            {
                return keys;
            }
            foreach (string pubFile in Directory.EnumerateFiles(_sshDirectory, "*.pub").OrderBy(f => f))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileNameWithoutExtension(pubFile);
                string privatePath = Path.Combine(_sshDirectory, name);
                SshKeyInfo? info = TryParsePublicKey(name, privatePath, pubFile);
                if (info is not null)
                {
                    keys.Add(info);
                }
            }
            return keys;
        }, cancellationToken);
    }

    /// <summary>
    /// 将外部私钥及其同名公钥复制到 <c>~/.ssh</c> 导入;目标同名已存在时返回 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>先验后抄,失败回滚。</b>旧实现是"能抄就抄":源私钥不存在时 <c>if (File.Exists(…))</c>
    /// 直接跳过复制,却照样返回一条 <c>Unknown</c> 条目 —— 界面显示"已导入 xxx",
    /// 而 <c>~/.ssh</c> 里什么都没多。挑中一个随便的文本文件同样一路成功。
    /// </para>
    /// <para>
    /// <b>没有 .pub 就明确拒绝。</b><see cref="ListKeysAsync" /> 是按 <c>*.pub</c> 枚举的,
    /// 只导私钥的话文件确实抄进去了,列表里却一条都看不到 —— 用户看到"导入成功"随后
    /// 密钥消失,只会以为程序把它弄丢了。与其如此,不如当场说清要连 <c>.pub</c> 一起选。
    /// </para>
    /// <para>
    /// <b>私钥权限。</b>生成路径一直会设 0600,导入路径以前不设 —— 而 OpenSSH 对
    /// 组/其他可读的私钥直接拒用(<c>UNPROTECTED PRIVATE KEY FILE</c>)。
    /// </para>
    /// </remarks>
    /// <param name="sourcePrivateKeyPath">源私钥路径(选中 <c>.pub</c> 时自动换成同名私钥)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入后的密钥;同名已存在时为 <see langword="null" />。</returns>
    /// <exception cref="FileNotFoundException">源私钥或其 <c>.pub</c> 不存在。</exception>
    /// <exception cref="InvalidDataException">源文件不是私钥,或公钥无法解析。</exception>
    public async Task<SshKeyInfo?> ImportKeyAsync(string sourcePrivateKeyPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_sshDirectory);
        string name = Path.GetFileName(sourcePrivateKeyPath);
        if (name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
            sourcePrivateKeyPath = sourcePrivateKeyPath[..^4];
        }
        string sourcePub = sourcePrivateKeyPath + ".pub";
        string targetPrivate = Path.Combine(_sshDirectory, name);
        string targetPub = targetPrivate + ".pub";
        if (File.Exists(targetPrivate) || File.Exists(targetPub))
        {
            return null;
        }

        // ——— 先验:任何一条不过就当场退出,此时 ~/.ssh 一个字节都没动过 ———
        if (!File.Exists(sourcePrivateKeyPath))
        {
            throw new FileNotFoundException(
                Strings.Format("KeySvc_ImportPrivateKeyMissing", sourcePrivateKeyPath), sourcePrivateKeyPath);
        }
        if (!File.Exists(sourcePub))
        {
            throw new FileNotFoundException(
                Strings.Format("KeySvc_ImportPublicKeyMissing", Path.GetFileName(sourcePub)), sourcePub);
        }
        if (!await LooksLikePrivateKeyAsync(sourcePrivateKeyPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(Strings.Format("KeySvc_ImportNotAPrivateKey", name));
        }

        // ——— 再抄:两份一起,任一失败就把已抄的清掉,不留半套 ———
        try
        {
            File.Copy(sourcePrivateKeyPath, targetPrivate);
            ApplyPrivateKeyPermissions(targetPrivate);
            File.Copy(sourcePub, targetPub);
        }
        catch
        {
            TryDelete(targetPrivate);
            TryDelete(targetPub);
            throw;
        }

        // ——— 最后按真实解析结果回报。解析不出来说明 .pub 不是公钥,一并回滚 ———
        List<SshKeyInfo> keys = await ListKeysAsync(cancellationToken).ConfigureAwait(false);
        if (keys.FirstOrDefault(k => k.Name == name) is { } imported)
        {
            return imported;
        }
        TryDelete(targetPrivate);
        TryDelete(targetPub);
        throw new InvalidDataException(Strings.Format("KeySvc_ImportBadPublicKey", Path.GetFileName(sourcePub)));
    }

    /// <summary>
    /// 粗看一眼是不是私钥:够用来挡住"选错文件"。
    /// </summary>
    /// <remarks>
    /// 只读首行,不做完整解析 —— 私钥格式有 OpenSSH、PKCS#1、PKCS#8 好几种,而且可能加密,
    /// 真解析要密码短语。这里只要求它有 PEM 头,足以把 README、id_rsa.pub、截图挡在门外。
    /// </remarks>
    private static async Task<bool> LooksLikePrivateKeyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(path);
            string? first = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return first is not null
                   && first.StartsWith("-----BEGIN", StringComparison.Ordinal)
                   && first.Contains("PRIVATE KEY", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>把私钥收成仅属主可读写。OpenSSH 对更宽的权限直接拒用。</summary>
    private static void ApplyPrivateKeyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 回滚是尽力而为:清不掉也不该把原始失败原因盖掉。
        }
    }

    /// <summary>在 ~/.ssh 目录生成指定名称的 RSA 密钥对(默认 4096 位),并写出私钥与 OpenSSH 格式公钥。</summary>
    public Task<SshKeyInfo> GenerateRsaKeyAsync(string name, int bits = 4096, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(Strings.Get("KeySvc_InvalidName"), nameof(name));
            }
            Directory.CreateDirectory(_sshDirectory);
            string privatePath = Path.Combine(_sshDirectory, name);
            string publicPath = privatePath + ".pub";
            if (File.Exists(privatePath) || File.Exists(publicPath))
            {
                throw new IOException(Strings.Format("KeySvc_AlreadyExists", name));
            }
            using var rsa = RSA.Create(bits);
            RSAParameters parameters = rsa.ExportParameters(true);
            string comment = $"velashell@{Environment.MachineName}";

            // 私钥必须写 OpenSSH 格式(-----BEGIN OPENSSH PRIVATE KEY-----)。
            // Tmds.Ssh 0.23 的私钥解析器只认 OpenSSH 格式,ExportRSAPrivateKeyPem() 产出的 PKCS#1
            // (-----BEGIN RSA PRIVATE KEY-----)与 PKCS#8 都会被判 "Unsupported format" 而当作
            // 无可用凭据【跳过】——认证遂以 "These methods were skipped: publickey" 失败,用户表现为
            // 用本应用生成的密钥怎么都登不上(排障线索:诊断第 4 步 no methods failed、skipped publickey)。
            File.WriteAllText(privatePath, OpenSshPrivateKey.SerializeRsa(parameters, comment));
            ApplyPrivateKeyPermissions(privatePath);
            byte[] blob = BuildRsaPublicBlob(parameters);
            string publicLine = $"ssh-rsa {Convert.ToBase64String(blob)} {comment}";
            File.WriteAllText(publicPath, publicLine + Environment.NewLine);
            return new SshKeyInfo(name, $"RSA {bits}", Fingerprint(blob), privatePath, publicLine);
        }, cancellationToken);
    }

    /// <summary>删除指定名称密钥对的私钥与公钥文件(存在则删除)。</summary>
    public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            string privatePath = Path.Combine(_sshDirectory, name);
            string publicPath = privatePath + ".pub";
            if (File.Exists(privatePath))
            {
                File.Delete(privatePath);
            }
            if (File.Exists(publicPath))
            {
                File.Delete(publicPath);
            }
        }, cancellationToken);
    }

    private static SshKeyInfo? TryParsePublicKey(string name, string privatePath, string pubFile)
    {
        try
        {
            string line = File.ReadAllText(pubFile).Trim();
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return null;
            }
            byte[] blob = Convert.FromBase64String(parts[1]);
            return new(name, DescribeType(parts[0], blob), Fingerprint(blob), privatePath, line);
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            return null;
        }
    }

    private static string Fingerprint(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    private static string DescribeType(string algorithm, byte[] blob)
    {
        return algorithm switch
        {
            "ssh-rsa" => $"RSA {TryGetRsaBits(blob)}",
            "ssh-ed25519" => "ED25519",
            "ecdsa-sha2-nistp256" => "ECDSA 256",
            "ecdsa-sha2-nistp384" => "ECDSA 384",
            "ecdsa-sha2-nistp521" => "ECDSA 521",
            "ssh-dss" => "DSA",
            _ => algorithm
        };
    }

    /// <summary>从 ssh-rsa 公钥 blob(string algo, mpint e, mpint n)读取模数位数。</summary>
    private static int TryGetRsaBits(byte[] blob)
    {
        try
        {
            int offset = 0;
            ReadChunk(blob, ref offset); // algorithm name
            ReadChunk(blob, ref offset); // exponent
            byte[] modulus = ReadChunk(blob, ref offset);
            int length = modulus.Length;
            if (length > 0 && modulus[0] == 0)
            {
                length--; // mpint 前导零
            }
            return length * 8;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private static byte[] ReadChunk(byte[] blob, ref int offset)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(offset, 4));
        offset += 4;
        byte[] chunk = blob.AsSpan(offset, length).ToArray();
        offset += length;
        return chunk;
    }

    /// <summary>构造 OpenSSH ssh-rsa 公钥 blob:string "ssh-rsa" ‖ mpint e ‖ mpint n。</summary>
    private static byte[] BuildRsaPublicBlob(RSAParameters parameters)
    {
        using var stream = new MemoryStream();
        WriteChunk(stream, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteChunk(stream, ToMpint(parameters.Exponent!));
        WriteChunk(stream, ToMpint(parameters.Modulus!));
        return stream.ToArray();
    }

    private static byte[] ToMpint(byte[] value)
    {
        // 最高位为 1 时补前导零,保持无符号语义。
        return value.Length > 0 && (value[0] & 0x80) != 0
                   ? [0, .. value]
                   : value;
    }

    private static void WriteChunk(MemoryStream stream, byte[] data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        stream.Write(lengthBytes);
        stream.Write(data);
    }
}

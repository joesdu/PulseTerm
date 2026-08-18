using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.PluginSdk.Packaging;

/// <summary>容器标志位。</summary>
[Flags]
public enum VpxFlags : ushort
{
    /// <summary>无。</summary>
    None = 0,

    /// <summary>载荷经掩码变换(zip 特征字节不可见);见 <see cref="VpxContainer" /> 的格式说明。</summary>
    Masked = 1,

    /// <summary>包尾带签名块。</summary>
    Signed = 2
}

/// <summary>签名校验结论。</summary>
public enum VpxSignatureState
{
    /// <summary>包未签名。</summary>
    Unsigned,

    /// <summary>签名有效,且公钥在信任集合内(未提供信任集合时,任何有效签名都算 <see cref="Trusted" />)。</summary>
    Trusted,

    /// <summary>签名有效,但公钥不在信任集合内(自签名包)。</summary>
    Untrusted,

    /// <summary>签名块损坏或验签失败(内容被改过)。</summary>
    Invalid
}

/// <summary>包尾签名块(JSON)。</summary>
public sealed record VpxSignatureBlock
{
    /// <summary>签名算法标识,当前恒为 <see cref="VpxContainer.SignatureAlgorithm" />。</summary>
    [JsonPropertyName("alg")]
    public required string Algorithm { get; init; }

    /// <summary>签名者公钥(X.509 SubjectPublicKeyInfo,Base64)。</summary>
    [JsonPropertyName("publicKey")]
    public required string PublicKey { get; init; }

    /// <summary>对 64 字节头部的签名(Base64;头部内含载荷长度与摘要,故等同于对全包签名)。</summary>
    [JsonPropertyName("signature")]
    public required string Signature { get; init; }
}

/// <summary>一个 <c>.vpx</c> 包的头部信息(不含载荷)。</summary>
public sealed record VpxPackageInfo
{
    /// <summary>容器格式版本。</summary>
    public required int FormatVersion { get; init; }

    /// <summary>标志位。</summary>
    public required VpxFlags Flags { get; init; }

    /// <summary>载荷(zip)字节数。</summary>
    public required long PayloadLength { get; init; }

    /// <summary>载荷 SHA-256(小写十六进制)。</summary>
    public required string PayloadSha256 { get; init; }

    /// <summary>签名块;未签名时为 <see langword="null" />。</summary>
    public VpxSignatureBlock? Signature { get; init; }

    /// <summary>原始头部字节(验签的被签数据)。</summary>
    internal byte[] HeaderBytes { get; init; } = [];
}

/// <summary>打包选项。</summary>
public sealed record VpxPackOptions
{
    /// <summary>是否对载荷做掩码变换(默认开:让通用解压工具认不出内嵌 zip)。</summary>
    public bool Mask { get; init; } = true;

    /// <summary>
    /// 签名私钥(可选,P-256)。给出即在包尾写签名块。调用方负责密钥的生命周期。
    /// </summary>
    public ECDsa? SigningKey { get; init; }

    /// <summary>掩码随机数;默认随机生成。仅用于让打包可复现的测试场景。</summary>
    public ulong? MaskNonce { get; init; }
}

/// <summary>
/// <c>.vpx</c> 插件包容器的读写。
/// <para>
/// 布局(小端;头部固定 64 字节):
/// </para>
/// <code>
/// 偏移  长度  内容
/// 0     4    魔数 56 50 58 1A("VPX" + 0x1A)
/// 4     2    容器格式版本(当前 1)
/// 6     2    标志位(见 VpxFlags)
/// 8     8    载荷字节数
/// 16    32   载荷 SHA-256
/// 48    8    掩码随机数
/// 56    4    头部 CRC32(前 56 字节)
/// 60    4    保留(0)
/// 64    N    载荷:zip 字节流(Masked 时经掩码变换)
/// 64+N  4+M  可选签名块:int32 长度 + UTF-8 JSON(见 VpxSignatureBlock)
/// </code>
/// <para>
/// **魔数与掩码是格式标识与防手滑,不是安全边界**:插件是本机可执行代码,任何"解密"
/// 所需的信息都必然在客户端。真正的完整性与来源保证来自 SHA-256 与包尾的 ECDSA 签名 ——
/// 前者挡住损坏与截断,后者挡住篡改与冒名。
/// </para>
/// <para>
/// 0x1A 那一字节是 DOS 的文件结束符,沿用 PNG 的老办法:用 <c>type</c> / <c>cat</c>
/// 误看包体时会在此停住,不至于刷一屏乱码。
/// </para>
/// </summary>
public static class VpxContainer
{
    /// <summary>头部固定长度。</summary>
    public const int HeaderSize = 64;

    /// <summary>当前容器格式版本。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>签名算法标识。</summary>
    public const string SignatureAlgorithm = "ECDSA-P256-SHA256";

    /// <summary>包扩展名。</summary>
    public const string FileExtension = ".vpx";

    /// <summary>单个包允许的最大载荷(512 MB):挡住损坏头部里的天文数字长度。</summary>
    public const long MaxPayloadLength = 512L * 1024 * 1024;

    private static ReadOnlySpan<byte> MagicBytes => [0x56, 0x50, 0x58, 0x1A];

    /// <summary>文件是否为 <c>.vpx</c> 容器(只嗅探魔数,不校验完整性)。</summary>
    public static bool IsVpx(string path) => StartsWith(path, MagicBytes);

    /// <summary>
    /// 把一个目录(须含根级 <c>plugin.json</c>)打成 <c>.vpx</c> 包。
    /// </summary>
    /// <param name="sourceDirectory">插件产物目录。</param>
    /// <param name="vpxPath">输出包路径(已存在则覆盖)。</param>
    /// <param name="options">打包选项。</param>
    /// <exception cref="VpxFormatException">源目录缺少 <c>plugin.json</c>。</exception>
    public static void Pack(string sourceDirectory, string vpxPath, VpxPackOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDirectory);
        ArgumentException.ThrowIfNullOrEmpty(vpxPath);
        if (!File.Exists(Path.Combine(sourceDirectory, PluginManifestReader.FileName)))
        {
            throw new VpxFormatException(
                $"'{sourceDirectory}' has no {PluginManifestReader.FileName} at its root; a .vpx package must carry the manifest.");
        }
        // 先压到临时 zip 再封装:插件目录可能有几十兆,不值得整个进内存。
        string tempZip = Path.Combine(Path.GetTempPath(), $"velashell-pack-{Guid.NewGuid():N}.zip");
        try
        {
            ZipFile.CreateFromDirectory(sourceDirectory, tempZip, CompressionLevel.Optimal, includeBaseDirectory: false);
            string? parent = Path.GetDirectoryName(Path.GetFullPath(vpxPath));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            using FileStream payload = File.OpenRead(tempZip);
            using FileStream destination = File.Create(vpxPath);
            Write(destination, payload, options);
        }
        finally
        {
            TryDelete(tempZip);
        }
    }

    /// <summary>把一段 zip 载荷封进容器写入目标流。</summary>
    /// <param name="destination">目标流(需可写)。</param>
    /// <param name="zipPayload">zip 载荷(需可读;从当前位置读到末尾)。</param>
    /// <param name="options">打包选项。</param>
    public static void Write(Stream destination, Stream zipPayload, VpxPackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(zipPayload);
        options ??= new();

        byte[] payload = ReadAllBytes(zipPayload);
        if (payload.LongLength > MaxPayloadLength)
        {
            throw new VpxFormatException(
                $"Payload is {payload.LongLength} bytes, which exceeds the {MaxPayloadLength}-byte limit for a .vpx package.");
        }
        byte[] digest = SHA256.HashData(payload);

        VpxFlags flags = VpxFlags.None;
        ulong nonce = 0;
        if (options.Mask)
        {
            flags |= VpxFlags.Masked;
            nonce = options.MaskNonce ?? BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
            ApplyMask(payload, 0, nonce);
        }
        if (options.SigningKey is not null)
        {
            flags |= VpxFlags.Signed;
        }

        byte[] header = new byte[HeaderSize];
        MagicBytes.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), CurrentFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)flags);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), payload.LongLength);
        digest.CopyTo(header.AsSpan(16));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), nonce);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), Crc32(header.AsSpan(0, 56)));

        destination.Write(header);
        destination.Write(payload);

        if (options.SigningKey is { } key)
        {
            var block = new VpxSignatureBlock
            {
                Algorithm = SignatureAlgorithm,
                PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                Signature = Convert.ToBase64String(key.SignData(header, HashAlgorithmName.SHA256))
            };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(block, VpxJsonContext.Default.VpxSignatureBlock);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, json.Length);
            destination.Write(length);
            destination.Write(json);
        }
        destination.Flush();
    }

    /// <summary>只读取头部与签名块,不校验载荷摘要(供 UI 展示与签名核对)。</summary>
    /// <exception cref="VpxFormatException">不是 <c>.vpx</c> 包或头部损坏。</exception>
    public static VpxPackageInfo ReadInfo(string vpxPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(vpxPath);
        using FileStream stream = File.OpenRead(vpxPath);
        return ReadHeader(stream, vpxPath, out _);
    }

    /// <summary>
    /// 打开包内载荷:校验魔数、头部 CRC、长度与 SHA-256,通过后返回**已还原、可定位**的
    /// zip 字节流(位置 0)。调用方负责释放(释放即关闭底层文件)。
    /// </summary>
    /// <param name="vpxPath">包路径。</param>
    /// <exception cref="VpxFormatException">格式非法、损坏或摘要不符。</exception>
    public static Stream OpenPayload(string vpxPath) => OpenPayload(vpxPath, out _);

    /// <inheritdoc cref="OpenPayload(string)" />
    /// <param name="vpxPath">包路径。</param>
    /// <param name="info">读到的头部信息。</param>
    public static Stream OpenPayload(string vpxPath, out VpxPackageInfo info)
    {
        ArgumentException.ThrowIfNullOrEmpty(vpxPath);
        FileStream file = File.OpenRead(vpxPath);
        try
        {
            info = ReadHeader(file, vpxPath, out ulong nonce);
            var payload = new VpxPayloadStream(file, HeaderSize, info.PayloadLength, nonce,
                info.Flags.HasFlag(VpxFlags.Masked));
            VerifyDigest(payload, info, vpxPath);
            return payload;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 校验包尾签名。<paramref name="trustedPublicKeys" /> 为 Base64 的 SPKI 公钥集合;
    /// 传空集合表示"只验签名本身有效性,不判来源",此时有效签名返回 <see cref="VpxSignatureState.Trusted" />。
    /// </summary>
    /// <param name="info">包头信息(须来自 <see cref="ReadInfo" /> 或 <see cref="OpenPayload(string, out VpxPackageInfo)" />)。</param>
    /// <param name="trustedPublicKeys">受信公钥集合(Base64 SPKI)。</param>
    public static VpxSignatureState VerifySignature(VpxPackageInfo info, IReadOnlyCollection<string>? trustedPublicKeys = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Signature is not { } block)
        {
            return VpxSignatureState.Unsigned;
        }
        if (!string.Equals(block.Algorithm, SignatureAlgorithm, StringComparison.Ordinal))
        {
            return VpxSignatureState.Invalid;
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(block.PublicKey), out _);
            if (!key.VerifyData(info.HeaderBytes, Convert.FromBase64String(block.Signature), HashAlgorithmName.SHA256))
            {
                return VpxSignatureState.Invalid;
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return VpxSignatureState.Invalid;
        }
        return trustedPublicKeys is not { Count: > 0 }
               || trustedPublicKeys.Contains(block.PublicKey, StringComparer.Ordinal)
            ? VpxSignatureState.Trusted
            : VpxSignatureState.Untrusted;
    }

    private static bool StartsWith(string path, ReadOnlySpan<byte> prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                   && head.SequenceEqual(prefix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static VpxPackageInfo ReadHeader(FileStream stream, string path, out ulong nonce)
    {
        byte[] header = new byte[HeaderSize];
        if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) != HeaderSize)
        {
            throw new VpxFormatException(Describe(path, "the file is shorter than the 64-byte package header."));
        }
        if (!header.AsSpan(0, 4).SequenceEqual(MagicBytes))
        {
            string hint = header is [0x50, 0x4B, 0x03, 0x04, ..]
                ? "this is a plain zip archive - repack it with `vela-plugin pack` (renaming a .zip to .vpx no longer works)."
                : "the file does not start with the VPX magic bytes.";
            throw new VpxFormatException(Describe(path, hint));
        }
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(56));
        if (Crc32(header.AsSpan(0, 56)) != expectedCrc)
        {
            throw new VpxFormatException(Describe(path, "the package header is corrupt (header checksum mismatch)."));
        }
        int formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (formatVersion is < 1 or > CurrentFormatVersion)
        {
            throw new VpxFormatException(Describe(path,
                $"container format version {formatVersion} is newer than this host supports (up to {CurrentFormatVersion}). Update VelaShell."));
        }
        var flags = (VpxFlags)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        if (payloadLength is < 0 or > MaxPayloadLength)
        {
            throw new VpxFormatException(Describe(path, $"declared payload length {payloadLength} is out of range."));
        }
        if (stream.Length < HeaderSize + payloadLength)
        {
            throw new VpxFormatException(Describe(path, "the file is truncated (payload shorter than the header declares)."));
        }
        nonce = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48));

        VpxSignatureBlock? signature = null;
        if (flags.HasFlag(VpxFlags.Signed))
        {
            signature = ReadSignature(stream, HeaderSize + payloadLength, path);
        }
        return new()
        {
            FormatVersion = formatVersion,
            Flags = flags,
            PayloadLength = payloadLength,
            PayloadSha256 = Convert.ToHexStringLower(header.AsSpan(16, 32)),
            Signature = signature,
            HeaderBytes = header
        };
    }

    private static VpxSignatureBlock ReadSignature(FileStream stream, long offset, string path)
    {
        long savedPosition = stream.Position;
        try
        {
            stream.Position = offset;
            Span<byte> lengthBytes = stackalloc byte[4];
            if (stream.ReadAtLeast(lengthBytes, 4, throwOnEndOfStream: false) != 4)
            {
                throw new VpxFormatException(Describe(path, "the package claims to be signed but carries no signature block."));
            }
            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is <= 0 or > 8192 || stream.Length - offset - 4 < length)
            {
                throw new VpxFormatException(Describe(path, "the signature block is malformed."));
            }
            byte[] json = new byte[length];
            stream.ReadExactly(json);
            try
            {
                return JsonSerializer.Deserialize(json, VpxJsonContext.Default.VpxSignatureBlock)
                       ?? throw new VpxFormatException(Describe(path, "the signature block is empty."));
            }
            catch (JsonException ex)
            {
                throw new VpxFormatException(Describe(path, "the signature block is not valid JSON."), ex);
            }
        }
        finally
        {
            stream.Position = savedPosition;
        }
    }

    private static void VerifyDigest(Stream payload, VpxPackageInfo info, string path)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int read;
        while ((read = payload.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, read);
        }
        string actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (!string.Equals(actual, info.PayloadSha256, StringComparison.Ordinal))
        {
            throw new VpxFormatException(Describe(path,
                "the payload does not match the digest in the header - the package is corrupt or was tampered with."));
        }
        payload.Position = 0;
    }

    private static string Describe(string path, string reason) =>
        $"'{Path.GetFileName(path)}' is not a valid VelaShell plugin package: {reason}";

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 掩码变换:按 32 字节分块,与 <c>SHA-256(nonce ‖ 块号)</c> 逐字节异或。
    /// 自反(同一函数既掩码又还原),且可随机定位 —— 这正是载荷流能 Seek 的原因。
    /// </summary>
    internal static void ApplyMask(Span<byte> data, long absoluteOffset, ulong nonce)
    {
        Span<byte> seed = stackalloc byte[16];
        Span<byte> keystream = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(seed, nonce);
        long block = -1;
        for (int i = 0; i < data.Length; i++)
        {
            long position = absoluteOffset + i;
            long currentBlock = position / 32;
            if (currentBlock != block)
            {
                block = currentBlock;
                BinaryPrimitives.WriteInt64LittleEndian(seed[8..], currentBlock);
                SHA256.HashData(seed, keystream);
            }
            data[i] ^= keystream[(int)(position % 32)];
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            }
        }
        return ~crc;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件删不掉不该让打包失败。
        }
    }

    /// <summary>
    /// 容器内载荷的只读视图:把宿主文件流的 [offset, offset+length) 窗口暴露成独立的可定位流,
    /// 并按需还原掩码。<see cref="ZipArchive" /> 读模式要求可定位,故不能用纯转发式包装。
    /// </summary>
    private sealed class VpxPayloadStream(FileStream inner, long offset, long length, ulong nonce, bool masked) : Stream
    {
        private long _position;

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => true;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => length;

        /// <inheritdoc />
        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        /// <inheritdoc />
        public override int Read(Span<byte> buffer)
        {
            long remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }
            Span<byte> window = buffer[..(int)Math.Min(buffer.Length, remaining)];
            inner.Position = offset + _position;
            int read = inner.ReadAtLeast(window, window.Length, throwOnEndOfStream: false);
            if (masked && read > 0)
            {
                ApplyMask(window[..read], _position, nonce);
            }
            _position += read;
            return read;
        }

        /// <inheritdoc />
        public override long Seek(long target, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => target,
                SeekOrigin.Current => _position + target,
                SeekOrigin.End => length + target,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>签名块的源生成序列化上下文(容器读写不走反射)。</summary>
[JsonSerializable(typeof(VpxSignatureBlock))]
internal sealed partial class VpxJsonContext : JsonSerializerContext;

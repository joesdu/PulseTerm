namespace VelaShell.Plugin.S3;

/// <summary>
/// POSIX 风格路径 ↔ 「桶 + 键」的双向翻译。整个 S3 后端都架在这层映射上:
/// 它错一点,文件浏览器就会把对象放进错误的目录,或者把删除打到错误的前缀上。
/// <para>
/// 约定的三种层级:
/// </para>
/// <list type="table">
///   <item><term><c>/</c></term><description>根 = 桶列表(每个桶显示为一个目录)</description></item>
///   <item><term><c>/bucket</c></term><description>桶根 = 以 <c>""</c> 为前缀、<c>/</c> 为分隔符列举</description></item>
///   <item><term><c>/bucket/a/b</c></term><description>键 <c>a/b</c>,或前缀 <c>a/b/</c>(取决于它是对象还是"目录")</description></item>
/// </list>
/// </summary>
/// <param name="Bucket">桶名;根为空串。</param>
/// <param name="Key">对象键(不以 <c>/</c> 开头);桶根为空串。</param>
public readonly record struct S3ObjectPath(string Bucket, string Key)
{
    /// <summary>根路径(桶列表)。</summary>
    public static S3ObjectPath Root => new(string.Empty, string.Empty);

    /// <summary>是否为根(桶列表)。</summary>
    public bool IsRoot => Bucket.Length == 0;

    /// <summary>是否为某个桶的根。</summary>
    public bool IsBucketRoot => Bucket.Length > 0 && Key.Length == 0;

    /// <summary>
    /// 展示名:根是 <c>/</c>,桶根是桶名,其余是键的最后一段
    /// (键以 <c>/</c> 结尾的目录占位符也取最后一段实名)。
    /// </summary>
    public string Name
    {
        get
        {
            if (IsRoot)
            {
                return "/";
            }
            if (Key.Length == 0)
            {
                return Bucket;
            }
            string trimmed = Key.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        }
    }

    /// <summary>
    /// 作为列举前缀使用的形式:非空键补一个 <c>/</c>,桶根/根为空串(列举整个桶)。
    /// <para>
    /// 补斜杠这一步不能省:列举 <c>tree</c> 会连 <c>treasure.txt</c> 一起匹配到 ——
    /// 删除时那就是误删。
    /// </para>
    /// </summary>
    public string Prefix => Key.Length == 0 ? string.Empty : Key.EndsWith('/') ? Key : Key + "/";

    /// <summary>
    /// 解析一条路径。空/null/纯分隔符一律是根(绝不能变成一个空桶名);
    /// 反斜杠按分隔符处理(Windows 拖放过来的路径),连续分隔符折叠,末尾斜杠吃掉。
    /// </summary>
    /// <param name="path">路径。</param>
    /// <returns>解析结果。</returns>
    public static S3ObjectPath Parse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Root;
        }
        string[] segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 0
            ? Root
            : new(segments[0], string.Join('/', segments.Skip(1)));
    }

    /// <summary>父路径;根的父仍是根(不越界)。</summary>
    /// <returns>父路径。</returns>
    public S3ObjectPath Parent()
    {
        if (IsRoot)
        {
            return Root;
        }
        if (Key.Length == 0)
        {
            return Root;
        }
        string trimmed = Key.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? new(Bucket, string.Empty) : new(Bucket, trimmed[..slash]);
    }

    /// <summary>追加一段相对路径;在根上追加时第一段成为桶名。空串是空操作。</summary>
    /// <param name="relative">相对路径。</param>
    /// <returns>追加后的路径。</returns>
    public S3ObjectPath Append(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return this;
        }
        return Parse(ToString() + "/" + relative);
    }

    /// <summary>规范形式:<c>/</c>、<c>/bucket</c> 或 <c>/bucket/key</c>(不带末尾斜杠)。</summary>
    /// <returns>规范路径。</returns>
    public override string ToString()
    {
        if (IsRoot)
        {
            return "/";
        }
        string key = Key.Trim('/');
        return key.Length == 0 ? "/" + Bucket : "/" + Bucket + "/" + key;
    }

    /// <summary>
    /// 桶名是否合法(长度 3–63、小写字母数字与 <c>.</c><c>-</c>、首尾为字母数字、
    /// 不得形如 IPv4 地址)。
    /// </summary>
    /// <param name="bucket">桶名。</param>
    /// <returns>是否合法。</returns>
    public static bool IsValidBucketName(string? bucket)
    {
        if (bucket is not { Length: >= 3 and <= 63 })
        {
            return false;
        }
        if (!char.IsAsciiLetterLower(bucket[0]) && !char.IsAsciiDigit(bucket[0]))
        {
            return false;
        }
        char last = bucket[^1];
        if (!char.IsAsciiLetterLower(last) && !char.IsAsciiDigit(last))
        {
            return false;
        }
        foreach (char c in bucket)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('.' or '-'))
            {
                return false;
            }
        }
        // 形如 IPv4 的桶名会与路径式寻址下的"直接写 IP"混淆,协议明令禁止。
        return !System.Net.IPAddress.TryParse(bucket, out _);
    }

    /// <summary>
    /// 该桶名能否安全地放进主机名(虚拟主机式寻址)。含点的桶名不行:
    /// <c>*.s3.amazonaws.com</c> 这类通配证书只覆盖一级标签,走虚拟主机式会让 TLS 校验失败。
    /// </summary>
    /// <param name="bucket">桶名。</param>
    /// <returns>是否可用于虚拟主机式。</returns>
    public static bool IsVirtualHostSafe(string? bucket) =>
        IsValidBucketName(bucket) && !bucket!.Contains('.', StringComparison.Ordinal);
}

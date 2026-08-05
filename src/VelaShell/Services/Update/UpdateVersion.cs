namespace VelaShell.Services.Update;

/// <summary>
/// 语义化版本(SemVer 2.0 子集):major.minor.patch 加可选预发布后缀(如 0.2.0-beta.1)。
/// 比较规则:数字段逐位比较;带预发布后缀的版本小于同数字的正式版;
/// 预发布标识按点分段比较,纯数字段按数值、其余按序数字符串。构建元数据(+xxx)忽略。
/// </summary>
public readonly struct UpdateVersion : IComparable<UpdateVersion>, IEquatable<UpdateVersion>
{
    /// <summary>主版本号。</summary>
    public int Major { get; }

    /// <summary>次版本号。</summary>
    public int Minor { get; }

    /// <summary>修订号。</summary>
    public int Patch { get; }

    /// <summary>预发布后缀(不含引导连字符),正式版为空字符串。</summary>
    public string PreRelease { get; }

    private UpdateVersion(int major, int minor, int patch, string preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    /// <summary>
    /// 解析版本字符串;允许前缀 v、第四位数字段(如程序集版本 0.1.0.0,忽略第四位)、
    /// 构建元数据后缀。解析失败返回 false。
    /// </summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        string s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }
        // 构建元数据不参与优先级比较,直接截掉。
        int plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }
        string pre = string.Empty;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0)
            {
                return false;
            }
        }
        string[] parts = s.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }
        Span<int> nums = stackalloc int[3];
        for (int i = 0; i < 3; i++)
        {
            if (i < parts.Length)
            {
                if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0)
                {
                    return false;
                }
            }
        }
        version = new(nums[0], nums[1], nums[2], pre);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(UpdateVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }
        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }
        c = Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>SemVer 预发布比较:空(正式版)最大;逐段比较,数字段按数值且小于任何非数字段。</summary>
    private static int ComparePreRelease(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }
        if (left.Length == 0)
        {
            return 1;
        }
        if (right.Length == 0)
        {
            return -1;
        }
        string[] a = left.Split('.');
        string[] b = right.Split('.');
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            bool aNum = int.TryParse(a[i], out int ai);
            bool bNum = int.TryParse(b[i], out int bi);
            int c = (aNum, bNum) switch
            {
                (true, true) => ai.CompareTo(bi),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i])
            };
            if (c != 0)
            {
                return c;
            }
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <inheritdoc />
    public bool Equals(UpdateVersion other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UpdateVersion v && Equals(v);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    /// <inheritdoc />
    public override string ToString() =>
        PreRelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    /// <summary>大于比较。</summary>
    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    /// <summary>小于比较。</summary>
    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;

    /// <summary>相等比较。</summary>
    public static bool operator ==(UpdateVersion left, UpdateVersion right) => left.Equals(right);

    /// <summary>不等比较。</summary>
    public static bool operator !=(UpdateVersion left, UpdateVersion right) => !left.Equals(right);
}

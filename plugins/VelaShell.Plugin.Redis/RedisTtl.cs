using System.Globalization;
using System.Text;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// TTL 的输入解析与显示。
/// <para>
/// 三种写法都要接:纯秒数(<c>900</c>)、带单位的时长(<c>15m</c> / <c>2h30m</c> / <c>7d</c>)、
/// 绝对时间点(<c>2026-08-20 12:00</c>)。**这不是花活** —— 运维脑子里想的是"再放半小时"
/// 或"活到明天中午",逼他先换算成秒是把机器的口径强加给人。
/// </para>
/// </summary>
public static class RedisTtl
{
    /// <summary>
    /// 解析一段 TTL 输入。
    /// </summary>
    /// <param name="text">用户输入。</param>
    /// <param name="now">当前时间(绝对时间点写法要拿它算差值;传入而不是取 <c>DateTime.Now</c> 以便单测)。</param>
    /// <param name="ttl">解析出的存活时长。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryParse(string? text, DateTimeOffset now, out TimeSpan ttl)
    {
        ttl = default;
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }
        // 纯数字 = 秒。放在最前面:它是 redis-cli 的口径,也是复制粘贴最常见的形式。
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
        {
            if (seconds <= 0)
            {
                return false;
            }
            ttl = TimeSpan.FromSeconds(seconds);
            return true;
        }
        if (TryParseDuration(trimmed, out ttl))
        {
            return true;
        }
        // 绝对时间点:按本地时间解析(用户填的就是他表上的时间),再折算成剩余时长。
        if (DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTime absolute)
            || DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out absolute))
        {
            TimeSpan remaining = new DateTimeOffset(absolute) - now;
            if (remaining <= TimeSpan.Zero)
            {
                // 过去的时间点不当成"立刻过期":那等于用一个看着像笔误的输入删掉一个键。
                return false;
            }
            ttl = remaining;
            return true;
        }
        return false;
    }

    /// <summary>带单位的时长:<c>7d</c> / <c>2h30m</c> / <c>90s</c>,可连写、大小写不敏感。</summary>
    private static bool TryParseDuration(string text, out TimeSpan ttl)
    {
        ttl = TimeSpan.Zero;
        long value = 0;
        bool sawDigit = false;
        bool sawUnit = false;
        foreach (char c in text)
        {
            if (char.IsDigit(c))
            {
                value = (value * 10) + (c - '0');
                sawDigit = true;
                continue;
            }
            if (!sawDigit)
            {
                return false;
            }
            TimeSpan unit = char.ToLowerInvariant(c) switch
            {
                'd' => TimeSpan.FromDays(value),
                'h' => TimeSpan.FromHours(value),
                'm' => TimeSpan.FromMinutes(value),
                's' => TimeSpan.FromSeconds(value),
                _ => TimeSpan.Zero
            };
            if (unit == TimeSpan.Zero)
            {
                return false;
            }
            ttl += unit;
            value = 0;
            sawDigit = false;
            sawUnit = true;
        }
        // 结尾还有没消费掉的数字(如 "2h30")= 输入不完整,不猜它的单位。
        return sawUnit && !sawDigit && ttl > TimeSpan.Zero;
    }

    /// <summary>把时长渲染成人能读的形式(<c>2 天 3 小时</c> / <c>29:58</c>)。</summary>
    /// <param name="ttl">时长。</param>
    /// <returns>显示文本。</returns>
    public static string Describe(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return "0";
        }
        if (ttl.TotalDays >= 1)
        {
            var builder = new StringBuilder();
            builder.Append(CultureInfo.CurrentCulture, $"{(int)ttl.TotalDays}d");
            if (ttl.Hours > 0)
            {
                builder.Append(CultureInfo.CurrentCulture, $" {ttl.Hours}h");
            }
            return builder.ToString();
        }
        return ttl.TotalHours >= 1
            ? $"{(int)ttl.TotalHours}:{ttl.Minutes:00}:{ttl.Seconds:00}"
            : $"{ttl.Minutes:00}:{ttl.Seconds:00}";
    }
}

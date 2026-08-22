using System.Collections.Concurrent;
using System.Net;
using MaxMind.Db;
using VelaShell.Core.Diagnostics;

namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>
/// 基于本地 MMDB 文件的离线 IP 归属地查询。默认找 <c>~/.velashell/geoip/</c> 下的
/// 第一个 .mmdb;设置里可以指定绝对路径。文件缺失时整个功能静默降级(<see cref="IsAvailable" />
/// 为 false),追踪照常工作,只是没有地图落点。
/// </summary>
/// <remarks>
/// 只读格式,不绑定数据厂商:配套推荐 DB-IP Lite City(CC BY 4.0,署名即可商用)。
/// </remarks>
public sealed class MmdbIpGeolocationService : IIpGeolocationService, IDisposable
{
    private readonly ConcurrentDictionary<IPAddress, IpLocation?> _cache = new();
    private readonly Lock _gate = new();
    private Reader? _reader;
    private bool _disposed;

    /// <summary>按给定路径打开数据库;路径为空则在默认目录里找第一个 .mmdb。</summary>
    /// <param name="configuredPath">已记住的绝对路径;为空表示使用默认目录。</param>
    /// <param name="defaultDirectory">默认存放目录。</param>
    public MmdbIpGeolocationService(string? configuredPath, string defaultDirectory)
    {
        if (ResolvePath(configuredPath, defaultDirectory) is { } path)
        {
            TryLoad(path);
        }
    }

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _reader is not null;
            }
        }
    }

    /// <inheritdoc />
    public string? DatabaseDescription { get; private set; }

    /// <inheritdoc />
    public bool TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }
        try
        {
            // Memory 模式把整库读进内存:城市库上百 MB,但查询会被每跳每轮反复调用,
            // 走文件 IO 会拖慢界面。
            Reader reader = new(path, FileAccessMode.Memory);
            lock (_gate)
            {
                _reader?.Dispose();
                _reader = reader;
                DatabaseDescription = $"{Path.GetFileName(path)} · {reader.Metadata.DatabaseType}";
            }
            _cache.Clear(); // 换库后旧结果作废
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDatabaseException or UnauthorizedAccessException)
        {
            // 文件损坏、格式不对或被占用:保留原有库,让调用方给出提示。
            return false;
        }
    }

    /// <inheritdoc />
    public IpLocation? Lookup(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_disposed || IsPrivate(address) || !IsAvailable)
        {
            return null;
        }
        return _cache.GetOrAdd(address, LookupCore);
    }

    /// <summary>释放内存映射的数据库。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_gate)
        {
            _reader?.Dispose();
            _reader = null;
        }
    }

    private IpLocation? LookupCore(IPAddress address)
    {
        try
        {
            // 直接读通用节点而不是绑定某个厂商的强类型模型:DB-IP、GeoLite2、IPLocate
            // 产出的 mmdb 字段布局一致(city/country/location),但强类型模型分属各自的包。
            MmdbRecord? record;
            lock (_gate)
            {
                record = _reader?.Find<MmdbRecord>(address);
            }
            if (record?.Location is not { Latitude: not null, Longitude: not null } location)
            {
                return null;
            }
            return new(
                location.Latitude.Value,
                location.Longitude.Value,
                Pick(record.City?.Names),
                Pick(record.Country?.Names),
                record.Country?.IsoCode
            );
        }
        catch (Exception ex) when (ex is InvalidDatabaseException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>取本地化名称:优先当前语言,退回英文,再退回任意一个。</summary>
    private static string? Pick(IReadOnlyDictionary<string, string>? names)
    {
        if (names is null || names.Count == 0)
        {
            return null;
        }
        string language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        foreach (string key in new[] { language, $"{language}-CN", "en" })
        {
            if (names.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return names.Values.FirstOrDefault();
    }

    private static string? ResolvePath(string? configuredPath, string defaultDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }
        try
        {
            return Directory.Exists(defaultDirectory)
                       ? Directory.EnumerateFiles(defaultDirectory, "*.mmdb").FirstOrDefault()
                       : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>私有/环回地址不查库:内网跳本来就没有公网归属地。</summary>
    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 or 127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] is >= 16 and <= 31 => true,
            192 when bytes[1] == 168 => true,
            100 when bytes[1] is >= 64 and <= 127 => true,
            _ => false
        };
    }

    // MMDB 记录的最小映射:只取落点与名称,不引入厂商专有字段。
    [method: Constructor]
    // MMDB 记录的最小映射:只取落点与名称,不引入厂商专有字段。
    private sealed class MmdbRecord(
        [MapKey("city")] MmdbNamed? city,
        [MapKey("country")] MmdbCountry? country,
        [MapKey("location")] MmdbLocation? location
        )
    {
        public MmdbNamed? City { get; } = city;

        public MmdbCountry? Country { get; } = country;

        public MmdbLocation? Location { get; } = location;
    }

    [method: Constructor]
    private class MmdbNamed([MapKey("names")] IReadOnlyDictionary<string, string>? names)
    {
        public IReadOnlyDictionary<string, string>? Names { get; } = names;
    }

    [method: Constructor]
    private sealed class MmdbCountry(
        [MapKey("names")] IReadOnlyDictionary<string, string>? names,
        [MapKey("iso_code")] string? isoCode
        ) : MmdbNamed(names)
    {
        public string? IsoCode { get; } = isoCode;
    }

    [method: Constructor]
    private sealed class MmdbLocation(
        [MapKey("latitude")] double? latitude,
        [MapKey("longitude")] double? longitude
        )
    {
        public double? Latitude { get; } = latitude;

        public double? Longitude { get; } = longitude;
    }
}

using System.Net;

namespace VelaShell.Core.Diagnostics;

/// <summary>一个 IP 的推断地理位置。</summary>
/// <param name="Latitude">纬度。</param>
/// <param name="Longitude">经度。</param>
/// <param name="City">城市名(可能为空)。</param>
/// <param name="Country">国家/地区名(可能为空)。</param>
/// <param name="CountryCode">两位国家代码(可能为空)。</param>
public sealed record IpLocation(
    double Latitude,
    double Longitude,
    string? City,
    string? Country,
    string? CountryCode
)
{
    /// <summary>"国家/城市" 形式的显示文本;两者都缺时为空串。</summary>
    public string Display =>
        (Country, City) switch
        {
            ({ Length: > 0 } country, { Length: > 0 } city) => $"{country}/{city}",
            ({ Length: > 0 } country, _) => country,
            (_, { Length: > 0 } city) => city,
            _ => string.Empty
        };
}

/// <summary>
/// 离线 IP 归属地查询。数据来自本地 MMDB 文件,不产生任何网络请求 ——
/// 链路上每一跳的 IP 都属于用户的网络路径,不该被发往第三方。
/// </summary>
/// <remarks>
/// **准确度必须如实对待**:这类库依据 IP 段的 whois 登记信息建库,而骨干路由器的段登记的是
/// 运营商注册地址而非设备实际位置(CAIDA/IMC 对路由器定位的专门研究结论)。城市级命中率在
/// 独立测试中普遍不到五成。因此中间跳的位置只能当作"推断",界面上要与起点/终点区别呈现。
/// </remarks>
public interface IIpGeolocationService
{
    /// <summary>数据库是否已就绪(文件存在且可读)。</summary>
    bool IsAvailable { get; }

    /// <summary>当前加载的数据库描述(供设置页显示),未加载时为 null。</summary>
    string? DatabaseDescription { get; }

    /// <summary>查询一个 IP 的位置;数据库不可用、地址私有或库中无记录时返回 null。</summary>
    /// <param name="address">要查询的地址。</param>
    IpLocation? Lookup(IPAddress address);

    /// <summary>
    /// 运行时换库。用户在追踪窗口里选了新文件时调用,成功后立即对所有窗口生效。
    /// </summary>
    /// <param name="path">.mmdb 文件的绝对路径。</param>
    /// <returns>加载成功返回 true;文件不存在或格式不对返回 false,原有库保持不变。</returns>
    bool TryLoad(string path);
}

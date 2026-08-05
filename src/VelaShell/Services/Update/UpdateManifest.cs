using System.Runtime.InteropServices;
using System.Text.Json;

namespace VelaShell.Services.Update;

/// <summary>更新清单中单个产物的描述:文件名、SHA-256(十六进制小写)与字节大小。</summary>
public sealed record UpdateAsset(string Name, string Sha256, long Size);

/// <summary>
/// 发布产物清单(CI 随每个 Release 上传的 latest.json):版本号、Release 标签,
/// 以及按 RID(win-x64 / osx-arm64 / …)索引的产物表。应用据此选择与自身平台
/// 匹配的压缩包并校验完整性。
/// </summary>
public sealed class UpdateManifest
{
    private UpdateManifest(string version, string tag, IReadOnlyDictionary<string, UpdateAsset> assets)
    {
        Version = version;
        Tag = tag;
        Assets = assets;
    }

    /// <summary>清单声明的版本号(不含 v 前缀,可带预发布后缀)。</summary>
    public string Version { get; }

    /// <summary>产物所在 Release 的 git 标签(下载 URL 用它定位,避免检查与下载之间发新版造成 404)。</summary>
    public string Tag { get; }

    /// <summary>按 RID 索引的产物表。</summary>
    public IReadOnlyDictionary<string, UpdateAsset> Assets { get; }

    /// <summary>取当前进程平台对应的产物;清单里没有该 RID 时返回 null。</summary>
    public UpdateAsset? AssetForCurrentPlatform() =>
        CurrentRid() is { } rid && Assets.TryGetValue(rid, out UpdateAsset? asset) ? asset : null;

    /// <summary>
    /// 当前进程的 RID(与 CI 产物命名一致);不受支持的平台/架构返回 null。
    /// 按进程架构而非 OS 架构解析:x64 版本跑在 arm64 Windows 的仿真层上时继续走 x64 轨。
    /// </summary>
    public static string? CurrentRid()
    {
        string? os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : null;
        string? arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };
        return os != null && arch != null ? $"{os}-{arch}" : null;
    }

    /// <summary>
    /// 解析 latest.json;结构不合法时抛 <see cref="JsonException" /> 或 <see cref="FormatException" />。
    /// 用 JsonDocument 手工取值而非反射序列化:字段极少,且不给单文件裁剪/AOT 留隐患。
    /// </summary>
    public static UpdateManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string version = root.GetProperty("version").GetString()
            ?? throw new FormatException("latest.json: version missing");
        string tag = root.GetProperty("tag").GetString()
            ?? throw new FormatException("latest.json: tag missing");
        Dictionary<string, UpdateAsset> assets = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (JsonProperty entry in root.GetProperty("assets").EnumerateObject())
        {
            JsonElement v = entry.Value;
            string name = v.GetProperty("name").GetString()
                ?? throw new FormatException($"latest.json: assets.{entry.Name}.name missing");
            string sha = v.GetProperty("sha256").GetString()
                ?? throw new FormatException($"latest.json: assets.{entry.Name}.sha256 missing");
            long size = v.TryGetProperty("size", out JsonElement sizeEl) ? sizeEl.GetInt64() : 0;
            assets[entry.Name] = new(name, sha, size);
        }
        if (assets.Count == 0)
        {
            throw new FormatException("latest.json: assets empty");
        }
        return new(version, tag, assets);
    }
}

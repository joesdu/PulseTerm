using System.Text.Json;
using System.Text.RegularExpressions;
using VelaShell.PluginSdk.Manifest;

namespace VelaShell.PluginSdk;

/// <summary>
/// <c>plugin.json</c> 的解析与校验。宿主装载与打包工具执行同一套规则,
/// 任何拒绝都给出可读原因(<see cref="PluginManifestException" />)。
/// </summary>
public static partial class PluginManifestReader
{
    /// <summary>清单文件名。</summary>
    public const string FileName = "plugin.json";

    [GeneratedRegex("^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    /// <summary>从 JSON 文本解析并校验清单。</summary>
    /// <exception cref="PluginManifestException">JSON 非法或校验失败。</exception>
    public static PluginManifest Parse(string json)
    {
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(json, PluginManifestJsonContext.Default.PluginManifest);
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException($"plugin.json is not valid JSON: {ex.Message}", ex);
        }
        if (manifest is null)
        {
            throw new PluginManifestException("plugin.json is empty.");
        }
        Validate(manifest);
        return manifest;
    }

    /// <summary>从文件加载并校验清单。</summary>
    /// <exception cref="PluginManifestException">文件缺失、JSON 非法或校验失败。</exception>
    public static PluginManifest Load(string filePath)
    {
        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PluginManifestException($"Cannot read manifest '{filePath}': {ex.Message}", ex);
        }
        return Parse(json);
    }

    /// <summary>校验清单字段;失败抛出带可读原因的异常。</summary>
    public static void Validate(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Length > 64 || !IdPattern().IsMatch(manifest.Id))
        {
            throw new PluginManifestException(
                $"Invalid plugin id '{manifest.Id}': must be lowercase [a-z0-9.-], start/end with alphanumeric, and be at most 64 chars (e.g. \"acme.my-plugin\").");
        }
        if (string.IsNullOrWhiteSpace(manifest.Version) || !VersionPattern().IsMatch(manifest.Version))
        {
            throw new PluginManifestException(
                $"Invalid version '{manifest.Version}': expected semver-style like \"1.2.0\" or \"1.2.0-beta.1\".");
        }
        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            throw new PluginManifestException("displayName must not be empty.");
        }
        ValidateEntry(manifest.Entry);
        if (manifest.ApiLevel < 1)
        {
            throw new PluginManifestException($"Invalid apiLevel {manifest.ApiLevel}: must be >= 1.");
        }
        if (manifest.MinHostVersion is { } minHost && !VersionPattern().IsMatch(minHost))
        {
            throw new PluginManifestException(
                $"Invalid minHostVersion '{minHost}': expected semver-style like \"0.1.0\".");
        }
        ValidateActivation(manifest);
    }

    /// <summary>激活事件与贡献点校验:未知事件与越界命名一律拒绝(拼写错误不许静默失效)。</summary>
    private static void ValidateActivation(PluginManifest manifest)
    {
        string prefix = manifest.Id + ".";
        foreach (string activationEvent in manifest.ActivationEvents ?? [])
        {
            if (activationEvent.Equals("onStartup", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (activationEvent.StartsWith("onCommand:", StringComparison.OrdinalIgnoreCase))
            {
                string commandId = activationEvent["onCommand:".Length..];
                if (!commandId.StartsWith(prefix, StringComparison.Ordinal))
                {
                    throw new PluginManifestException(
                        $"Invalid activation event '{activationEvent}': command id must start with '{prefix}'.");
                }
                if ((manifest.Contributes?.Commands ?? []).All(c => !c.Id.Equals(commandId, StringComparison.Ordinal)))
                {
                    throw new PluginManifestException(
                        $"Activation event '{activationEvent}' has no matching entry in contributes.commands " +
                        "(the placeholder command must be declared so it exists before activation).");
                }
                continue;
            }
            throw new PluginManifestException(
                $"Unknown activation event '{activationEvent}': supported are \"onStartup\" and \"onCommand:<commandId>\".");
        }
        foreach (CommandContribution command in manifest.Contributes?.Commands ?? [])
        {
            if (string.IsNullOrWhiteSpace(command.Id) || !command.Id.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new PluginManifestException(
                    $"Invalid contributed command id '{command.Id}': must start with '{prefix}'.");
            }
            if (string.IsNullOrWhiteSpace(command.Title))
            {
                throw new PluginManifestException($"Contributed command '{command.Id}' must have a non-empty title.");
            }
        }
    }

    private static void ValidateEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginManifestException($"Invalid entry '{entry}': must point to a .dll inside the plugin directory.");
        }
        // 入口必须留在插件目录内:拒绝绝对路径与任何 ".." 段(zip-slip / 目录逃逸防护)。
        if (Path.IsPathRooted(entry)
            || entry.Split('/', '\\').Any(segment => segment is ".." or ""))
        {
            throw new PluginManifestException($"Invalid entry '{entry}': absolute paths and '..' segments are not allowed.");
        }
    }
}

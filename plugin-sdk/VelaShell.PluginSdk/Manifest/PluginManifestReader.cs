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
        if (manifest.MinSdkVersion is { } minSdk && !VersionPattern().IsMatch(minSdk))
        {
            throw new PluginManifestException(
                $"Invalid minSdkVersion '{minSdk}': expected semver-style like \"1.1.0\".");
        }
        ValidateDisplayText(manifest.Author, "author");
        ValidateDisplayText(manifest.Publisher, "publisher");
        ValidateActivation(manifest);
    }

    /// <summary>
    /// 纯展示字段(author / publisher)的校验:限长并拒绝控制字符 —— 这些串会原样进插件管理页,
    /// 换行与回车能把一行卡片撑成一屏,回退符还能伪造出别的插件名。
    /// </summary>
    private static void ValidateDisplayText(string? value, string field)
    {
        if (value is null)
        {
            return;
        }
        if (value.Length > 128)
        {
            throw new PluginManifestException($"Invalid {field}: must be at most 128 characters.");
        }
        if (value.Any(char.IsControl))
        {
            throw new PluginManifestException($"Invalid {field}: control characters are not allowed.");
        }
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
            if (activationEvent.StartsWith("onProtocol:", StringComparison.OrdinalIgnoreCase))
            {
                string protocolId = activationEvent["onProtocol:".Length..];
                if ((manifest.Contributes?.Protocols ?? []).All(p => !p.Id.Equals(protocolId, StringComparison.Ordinal)))
                {
                    throw new PluginManifestException(
                        $"Activation event '{activationEvent}' has no matching entry in contributes.protocols " +
                        "(the protocol tab must be declared so it exists before activation).");
                }
                continue;
            }
            if (activationEvent.StartsWith("onWorkspace:", StringComparison.OrdinalIgnoreCase))
            {
                string workspaceId = activationEvent["onWorkspace:".Length..];
                if ((manifest.Contributes?.Workspaces ?? []).All(w => !w.Id.Equals(workspaceId, StringComparison.Ordinal)))
                {
                    throw new PluginManifestException(
                        $"Activation event '{activationEvent}' has no matching entry in contributes.workspaces " +
                        "(the connection tab must be declared so it exists before activation).");
                }
                continue;
            }
            throw new PluginManifestException(
                $"Unknown activation event '{activationEvent}': supported are \"onStartup\", \"onCommand:<commandId>\", " +
                "\"onProtocol:<protocolId>\" and \"onWorkspace:<workspaceId>\".");
        }
        ValidateProtocols(manifest, prefix);
        ValidateWorkspaces(manifest, prefix);
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

    /// <summary>
    /// 协议 id 是否合法:必须等于插件 id,或以 <c>&lt;插件id&gt;.</c> 为前缀,且整条 id 都要过
    /// 与插件 id 相同的字符集规则(全小写 <c>[a-z0-9.-]</c>、首尾字母数字、≤128 字符)。
    /// <para>
    /// **强制全小写是为了消灭大小写歧义**:这个 id 会落进用户的会话配置,而宿主的注册表
    /// 按不区分大小写查、界面按区分大小写比 —— 只要允许大写存在,
    /// <c>Foo.Bar</c> 与 <c>foo.bar</c> 就会在不同环节被判成"同一个"和"不是同一个"。
    /// 清单校验与运行期注册共用这一个判定,免得两边各写一套。
    /// </para>
    /// </summary>
    /// <param name="protocolId">待校验的协议 id。</param>
    /// <param name="pluginId">拥有它的插件 id。</param>
    /// <returns>是否合法。</returns>
    public static bool IsValidProtocolId(string? protocolId, string pluginId) =>
        !string.IsNullOrWhiteSpace(protocolId)
        && protocolId.Length <= 128
        && IdPattern().IsMatch(protocolId)
        && (protocolId.Equals(pluginId, StringComparison.Ordinal)
            || protocolId.StartsWith(pluginId + ".", StringComparison.Ordinal));

    /// <summary>
    /// 协议贡献校验。除了常规的 id / 名称 / 端口,这里还挡住一条容易踩空的组合:
    /// **协议 + 隔离进程**。协议是宿主反向调用插件的高频通道(列目录、流式读、传输进度),
    /// 而隔离模式的 RPC 只承载插件→宿主的请求方向 —— 让这种清单装上去,
    /// 表现会是"协议页签在、点了连不上",远不如在发现期就给出明确原因。
    /// </summary>
    private static void ValidateProtocols(PluginManifest manifest, string prefix)
    {
        ProtocolContribution[] protocols = manifest.Contributes?.Protocols ?? [];
        if (protocols.Length == 0)
        {
            return;
        }
        if (manifest.HostMode == PluginHostMode.Isolated)
        {
            throw new PluginManifestException(
                "contributes.protocols requires hostMode \"inProcess\": remote file protocols are called back by " +
                "the host (directory listings, streaming reads, transfer progress), which the isolated-process RPC " +
                "does not carry. Remove \"hostMode\": \"isolated\" or drop the protocol contribution.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProtocolContribution protocol in protocols)
        {
            if (!IsValidProtocolId(protocol.Id, manifest.Id))
            {
                throw new PluginManifestException(
                    $"Invalid contributed protocol id '{protocol.Id}': must be lowercase [a-z0-9.-], at most 128 chars, " +
                    $"and be '{manifest.Id}' or start with '{prefix}'.");
            }
            if (!seen.Add(protocol.Id))
            {
                throw new PluginManifestException($"Duplicate contributed protocol id '{protocol.Id}'.");
            }
            if (string.IsNullOrWhiteSpace(protocol.DisplayName))
            {
                throw new PluginManifestException($"Contributed protocol '{protocol.Id}' must have a non-empty displayName.");
            }
            if (protocol.DefaultPort is < 1 or > 65535)
            {
                throw new PluginManifestException(
                    $"Contributed protocol '{protocol.Id}' has an out-of-range defaultPort {protocol.DefaultPort}.");
            }
        }
    }

    /// <summary>
    /// 工作台贡献校验。规则与协议贡献同源(id 前缀/字符集、名称、端口、不得与隔离进程共存),
    /// 另加一条:**同一份清单里工作台 id 不得与协议 id 相撞** ——
    /// 两者在连接配置页上是同一排页签,也落进同一个 <c>PluginProtocolId</c> 字段,
    /// 撞了就会出现"同一个 id 既是文件协议又是工作台"的歧义。
    /// <para>
    /// 隔离进程被拒的理由与协议不同但同样根本:宿主要向插件索取一个 Avalonia 控件挂进停靠区,
    /// 而原生控件无法跨进程嵌入(蓝图 08 已弃用 HWND 收养)。
    /// </para>
    /// </summary>
    private static void ValidateWorkspaces(PluginManifest manifest, string prefix)
    {
        WorkspaceContribution[] workspaces = manifest.Contributes?.Workspaces ?? [];
        if (workspaces.Length == 0)
        {
            return;
        }
        if (manifest.HostMode == PluginHostMode.Isolated)
        {
            throw new PluginManifestException(
                "contributes.workspaces requires hostMode \"inProcess\": the host asks the plugin for an Avalonia " +
                "control to host as a dock document, and native controls cannot be embedded across processes. " +
                "Remove \"hostMode\": \"isolated\" or drop the workspace contribution.");
        }
        var seen = new HashSet<string>(
            (manifest.Contributes?.Protocols ?? []).Select(static p => p.Id), StringComparer.Ordinal);
        foreach (WorkspaceContribution workspace in workspaces)
        {
            if (!IsValidProtocolId(workspace.Id, manifest.Id))
            {
                throw new PluginManifestException(
                    $"Invalid contributed workspace id '{workspace.Id}': must be lowercase [a-z0-9.-], at most 128 chars, " +
                    $"and be '{manifest.Id}' or start with '{prefix}'.");
            }
            if (!seen.Add(workspace.Id))
            {
                throw new PluginManifestException(
                    $"Duplicate contributed connection id '{workspace.Id}': workspace ids must not collide with each " +
                    "other or with contributed protocol ids (they share one tab strip and one stored id).");
            }
            if (string.IsNullOrWhiteSpace(workspace.DisplayName))
            {
                throw new PluginManifestException($"Contributed workspace '{workspace.Id}' must have a non-empty displayName.");
            }
            if (workspace.DefaultPort is < 1 or > 65535)
            {
                throw new PluginManifestException(
                    $"Contributed workspace '{workspace.Id}' has an out-of-range defaultPort {workspace.DefaultPort}.");
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

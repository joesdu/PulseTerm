using System.Text.Json.Serialization;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>MCP 服务器的连接方式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpTransportType
{
    /// <summary>本地进程,标准输入输出通信(npx / uvx / 任意可执行文件)。</summary>
    Stdio,

    /// <summary>远端 HTTP 端点(Streamable HTTP / SSE,自动探测)。</summary>
    Http
}

/// <summary>
/// 一个用户自定义的 MCP 服务器配置。文本字段保持"用户所见即所存"
/// (参数一行命令行、环境变量与请求头每行一条),解析由
/// <see cref="McpConfigParser" /> 在连接时完成。
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>稳定 id(创建时生成)。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名称;同时作为工具名前缀(会被清洗为 [A-Za-z0-9_-])。</summary>
    public string Name { get; set; } = "";

    /// <summary>是否启用(Agent 模式下参与连接)。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>连接方式。</summary>
    public McpTransportType Transport { get; set; } = McpTransportType.Stdio;

    /// <summary>Stdio:可执行文件或命令(如 npx、uvx、python)。</summary>
    public string Command { get; set; } = "";

    /// <summary>Stdio:命令行参数(单行,支持双引号包裹含空格片段)。</summary>
    public string Arguments { get; set; } = "";

    /// <summary>Stdio:工作目录(空 = 继承宿主)。</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Stdio:附加环境变量,每行一条 KEY=VALUE。</summary>
    public string EnvironmentVariables { get; set; } = "";

    /// <summary>Http:服务器端点 URL。</summary>
    public string Url { get; set; } = "";

    /// <summary>Http:附加请求头,每行一条 Name: Value(鉴权令牌等)。</summary>
    public string Headers { get; set; } = "";

    /// <summary>
    /// 不想暴露给模型的工具名,每行一条(大小写不敏感)。写服务器上的<b>原始</b>工具名,
    /// 不带插件加的服务器前缀 —— "测试"按钮列出来的就是这些名字。
    /// </summary>
    /// <remarks>有些 MCP 服务器一口气给几十个工具,全塞进去既占上下文又容易被误调。</remarks>
    public string DisabledTools { get; set; } = "";

    /// <summary>
    /// 上次连上时这台服务器提供的工具(名称 + 一句说明)。
    /// 缓存下来是为了让"配置工具"窗口不必每次都拉起进程/连网就能列出勾选项;
    /// 点该窗口的"更新工具库"才会真的重连刷新。
    /// </summary>
    public List<McpToolInfo> KnownTools { get; set; } = [];

    /// <summary>上次刷新工具库的时刻(UTC);为 null 表示从没刷过。</summary>
    public DateTimeOffset? ToolsRefreshedAt { get; set; }
}

/// <summary>MCP 服务器提供的一个工具(缓存用)。</summary>
public sealed class McpToolInfo
{
    /// <summary>服务器上的原始工具名。</summary>
    public string Name { get; set; } = "";

    /// <summary>一句话说明(取自 MCP 的 description,过长已截断)。</summary>
    public string Description { get; set; } = "";

    /// <summary>MCP 的 <c>readOnlyHint</c> 注解:只读工具不走审批。</summary>
    public bool ReadOnly { get; set; }
}

/// <summary>把配置里的用户文本解析为连接参数。</summary>
public static class McpConfigParser
{
    /// <summary>
    /// 按 shell 习惯拆分单行参数:空白分隔,双引号包裹的片段保留内部空白,
    /// <c>""</c> 转义为字面引号。不做变量展开。
    /// </summary>
    public static List<string> SplitArguments(string? arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return result;
        }
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        bool hasToken = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < arguments.Length && arguments[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken)
        {
            result.Add(current.ToString());
        }
        return result;
    }

    /// <summary>解析每行一条的 <c>KEY=VALUE</c>(空行与无 <c>=</c> 的行忽略,VALUE 可含 <c>=</c>)。</summary>
    public static Dictionary<string, string?> ParseEnvironmentLines(string? text)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in (text ?? "").Split('\n'))
        {
            string line = rawLine.Trim();
            int eq = line.IndexOf('=');
            if (line.Length == 0 || eq <= 0)
            {
                continue;
            }
            result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    /// <summary>解析每行一条的 <c>Name: Value</c> 请求头(空行与无 <c>:</c> 的行忽略)。</summary>
    public static Dictionary<string, string> ParseHeaderLines(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in (text ?? "").Split('\n'))
        {
            string line = rawLine.Trim();
            int colon = line.IndexOf(':');
            if (line.Length == 0 || colon <= 0)
            {
                continue;
            }
            result[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return result;
    }

    /// <summary>
    /// 把服务器名清洗为可作工具名前缀的形式(模型侧工具名普遍要求
    /// <c>[A-Za-z0-9_-]</c>):非法字符折叠为 <c>_</c>,空名回落 <c>mcp</c>。
    /// </summary>
    public static string SanitizeToolPrefix(string? name)
    {
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "mcp";
        }
        var sb = new System.Text.StringBuilder(trimmed.Length);
        bool lastWasUnderscore = false;
        foreach (char c in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            {
                sb.Append(c);
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                sb.Append('_');
                lastWasUnderscore = true;
            }
        }
        string result = sb.ToString().Trim('_');
        if (result.Length == 0)
        {
            return "mcp";
        }
        return result.Length > 24 ? result[..24] : result;
    }
}

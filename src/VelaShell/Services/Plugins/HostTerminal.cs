using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Terminal;
using VelaShell.Terminal;

namespace VelaShell.Services.Plugins;

/// <summary>会话 id → 终端仿真器 + 人类可读标签的解析器(由主窗口视图模型提供)。</summary>
public interface ITerminalResolver
{
    /// <summary>取会话的仿真器;不存在返回 null。</summary>
    (ITerminalEmulator Emulator, string Label)? Resolve(Guid sessionId);
}

/// <summary>
/// 插件终端能力(<see cref="ITerminalApi" />)的宿主实现:
/// 读取/搜索走仿真器缓冲区快照(UI 线程取行);回写经授权闸弹窗、批准后
/// <see cref="ITerminalEmulator.WriteTextInput" />(即宿主既有的"如同用户键入"路径,
/// 内部串行化,绝不直写 SSH 流)。每插件一个实例(带 pluginId)。
/// </summary>
internal sealed class HostTerminal(string pluginId, IPluginLogger log,
    Func<ITerminalResolver?> resolver, PluginPermissionGate gate) : ITerminalApi
{
    private const int MaxWriteBytes = 4096;

    public Task<string> GetOutputAsync(string sessionId, int maxLines = 1000, CancellationToken cancellationToken = default)
    {
        (ITerminalEmulator emulator, string _) = ResolveOrThrow(sessionId);
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            int total = emulator.TotalLines;
            int take = Math.Clamp(maxLines, 1, total);
            var sb = new StringBuilder();
            for (int row = total - take; row < total; row++)
            {
                sb.Append(emulator.GetBufferLine(row).TrimEnd()).Append('\n');
            }
            return sb.ToString();
        }).GetTask();
    }

    public Task<IReadOnlyList<TerminalMatch>> SearchOutputAsync(string sessionId, string pattern, bool isRegex = false,
        int maxMatches = 100, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        (ITerminalEmulator emulator, string _) = ResolveOrThrow(sessionId);
        Regex? regex = null;
        if (isRegex)
        {
            try
            {
                regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid regular expression: {ex.Message}", nameof(pattern));
            }
        }
        return Dispatcher.UIThread.InvokeAsync<IReadOnlyList<TerminalMatch>>(() =>
        {
            List<TerminalMatch> matches = [];
            int total = emulator.TotalLines;
            for (int row = 0; row < total && matches.Count < maxMatches; row++)
            {
                string line = emulator.GetBufferLine(row).TrimEnd();
                bool hit = regex is not null
                    ? regex.IsMatch(line)
                    : line.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                if (hit)
                {
                    matches.Add(new(row, line));
                }
            }
            return matches;
        }).GetTask();
    }

    public async Task WriteAsync(string sessionId, string input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Encoding.UTF8.GetByteCount(input) > MaxWriteBytes)
        {
            throw new ArgumentException($"Terminal input exceeds {MaxWriteBytes} bytes.", nameof(input));
        }
        (ITerminalEmulator emulator, string label) = ResolveOrThrow(sessionId);

        // 授权闸:始终允许 → 直接放行;否则弹窗给用户四选一。
        bool allowed = await gate.CheckTerminalWriteAsync(pluginId, label, Preview(input), cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            log.Warn($"Terminal write to {label} denied by user.");
            throw new PluginPermissionDeniedException($"User denied terminal write for plugin '{pluginId}'.");
        }
        await Dispatcher.UIThread.InvokeAsync(() => emulator.WriteTextInput(input)).GetTask().ConfigureAwait(false);
    }

    private (ITerminalEmulator Emulator, string Label) ResolveOrThrow(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out Guid id)
            || resolver() is not { } r
            || r.Resolve(id) is not { } hit)
        {
            throw new PluginSessionNotFoundException(sessionId);
        }
        return hit;
    }

    /// <summary>回写预览:单行、截断,供授权对话框展示(不泄漏过长内容)。</summary>
    private static string Preview(string input)
    {
        string oneLine = input.ReplaceLineEndings("⏎");
        return oneLine.Length > 200 ? oneLine[..200] + "…" : oneLine;
    }
}

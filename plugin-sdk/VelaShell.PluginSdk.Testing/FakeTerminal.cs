using System.Text.RegularExpressions;
using VelaShell.PluginSdk.Terminal;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="ITerminalApi" /> 的测试替身:每会话一段可预置的输出行;
/// 回写默认允许并记录,可切 <see cref="DenyWrites" /> 模拟用户拒绝。
/// </summary>
public sealed class FakeTerminal : ITerminalApi
{
    /// <summary>每会话的输出行(测试直接预置)。</summary>
    public Dictionary<string, List<string>> Output { get; } = new(StringComparer.Ordinal);

    /// <summary>已回写记录:(sessionId, input)。</summary>
    public List<(string SessionId, string Input)> Writes { get; } = [];

    /// <summary>为 true 时 <see cref="WriteAsync" /> 抛 <see cref="PluginPermissionDeniedException" />。</summary>
    public bool DenyWrites { get; set; }

    private List<string> Lines(string sessionId) =>
        Output.TryGetValue(sessionId, out List<string>? lines) ? lines : Output[sessionId] = [];

    /// <inheritdoc />
    public Task<string> GetOutputAsync(string sessionId, int maxLines = 1000, CancellationToken cancellationToken = default)
    {
        List<string> lines = Lines(sessionId);
        return Task.FromResult(string.Join('\n', lines.TakeLast(maxLines)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TerminalMatch>> SearchOutputAsync(string sessionId, string pattern, bool isRegex = false,
        int maxMatches = 100, CancellationToken cancellationToken = default)
    {
        List<TerminalMatch> matches = [];
        List<string> lines = Lines(sessionId);
        for (int i = 0; i < lines.Count && matches.Count < maxMatches; i++)
        {
            bool hit = isRegex
                ? Regex.IsMatch(lines[i], pattern, RegexOptions.None, TimeSpan.FromSeconds(1))
                : lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (hit)
            {
                matches.Add(new(i, lines[i]));
            }
        }
        return Task.FromResult<IReadOnlyList<TerminalMatch>>(matches);
    }

    /// <inheritdoc />
    public Task WriteAsync(string sessionId, string input, CancellationToken cancellationToken = default)
    {
        if (DenyWrites)
        {
            return Task.FromException(new PluginPermissionDeniedException("User denied terminal write."));
        }
        Writes.Add((sessionId, input));
        return Task.CompletedTask;
    }
}

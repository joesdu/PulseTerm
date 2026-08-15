using VelaShell.PluginSdk.Clipboard;
using VelaShell.PluginSdk.Secrets;

namespace VelaShell.PluginSdk.Testing;

/// <summary><see cref="ISecretsApi" /> 的内存替身(不加密——测试无此必要)。</summary>
public sealed class FakeSecrets : ISecretsApi
{
    /// <summary>当前机密表;测试可直接预置/断言。</summary>
    public Dictionary<string, string> Values { get; } = [with(StringComparer.Ordinal)];

    /// <inheritdoc />
    public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(Values.GetValueOrDefault(name));

    /// <inheritdoc />
    public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        Values[name] = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(Values.Remove(name));
}

/// <summary><see cref="IClipboardApi" /> 的内存替身。</summary>
public sealed class FakeClipboard : IClipboardApi
{
    /// <summary>当前剪贴板文本;测试可直接预置/断言。</summary>
    public string? Text { get; set; }

    /// <inheritdoc />
    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Text);

    /// <inheritdoc />
    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        Text = text;
        return Task.CompletedTask;
    }
}

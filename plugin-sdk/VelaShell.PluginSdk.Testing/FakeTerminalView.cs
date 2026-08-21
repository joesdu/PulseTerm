using System.Text;
using VelaShell.PluginSdk.TerminalView;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// 测试用的终端视图:不渲染,只把喂进来的字节按 UTF-8 累起来,
/// 并记下插件回写了什么、要求过什么尺寸。
/// <para>
/// <see cref="IPluginTerminalView.Control" /> 交出一个占位对象 —— headless 测试里
/// 没有可视树,而被测的通常是"插件把哪些字节送去了远端",不是像素。
/// </para>
/// </summary>
public sealed class FakeTerminalView : IPluginTerminalView
{
    private readonly StringBuilder _fed = new();
    private readonly List<byte[]> _sent = [];

    /// <inheritdoc />
    public object Control { get; } = new();

    /// <inheritdoc />
    public int Columns { get; private set; } = 80;

    /// <inheritdoc />
    public int Rows { get; private set; } = 24;

    /// <summary>至今喂进终端的全部字节(按 UTF-8 解码)。</summary>
    public string Fed => _fed.ToString();

    /// <summary>插件经 <see cref="AttachAsync" /> 写回流里的那些块。</summary>
    public IReadOnlyList<byte[]> Sent => _sent;

    /// <summary>被清过几次屏。</summary>
    public int ClearCount { get; private set; }

    /// <summary>释放过没有。</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public void Feed(ReadOnlySpan<byte> data) => _fed.Append(Encoding.UTF8.GetString(data));

    /// <inheritdoc />
    public void Write(string text) => _fed.Append(text);

    /// <inheritdoc />
    public void Clear()
    {
        ClearCount++;
        _fed.Clear();
    }

    /// <inheritdoc />
    public string GetText(int maxLines = 1000)
    {
        string[] lines = _fed.ToString().Split('\n');
        int take = Math.Clamp(maxLines, 1, lines.Length);
        return string.Join('\n', lines[^take..]);
    }

    /// <inheritdoc />
    public void Resize(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        Resized?.Invoke(columns, rows);
    }

    /// <inheritdoc />
    public event Action<byte[]>? UserInput;

    /// <inheritdoc />
    public event Action<int, int>? Resized;

    /// <summary>模拟用户敲了一段字。接了流的话会被写进流里。</summary>
    public void SimulateUserInput(string text) => UserInput?.Invoke(Encoding.UTF8.GetBytes(text));

    /// <inheritdoc />
    public async Task AttachAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        void OnInput(byte[] bytes)
        {
            _sent.Add(bytes);
            stream.Write(bytes);
        }

        UserInput += OnInput;
        try
        {
            byte[] buffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }
                Feed(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            UserInput -= OnInput;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}

/// <summary>测试用的终端视图能力:每次 <c>Create</c> 交出一个新的 <see cref="FakeTerminalView" /> 并记下来。</summary>
public sealed class FakeTerminalViewApi : ITerminalViewApi
{
    private readonly List<FakeTerminalView> _created = [];

    /// <summary>能不能用。置 <see langword="false" /> 可以测插件在老宿主上的退化路径。</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>建过的全部视图。</summary>
    public IReadOnlyList<FakeTerminalView> Created => _created;

    /// <summary>最近一次建的视图。</summary>
    public FakeTerminalView? Last => _created.Count > 0 ? _created[^1] : null;

    /// <summary>建视图时收到的选项。</summary>
    public TerminalViewOptions? LastOptions { get; private set; }

    /// <inheritdoc />
    public IPluginTerminalView Create(TerminalViewOptions? options = null)
    {
        if (!IsAvailable)
        {
            throw new NotSupportedException("This host does not provide terminal views.");
        }
        LastOptions = options;
        var view = new FakeTerminalView();
        _created.Add(view);
        return view;
    }
}

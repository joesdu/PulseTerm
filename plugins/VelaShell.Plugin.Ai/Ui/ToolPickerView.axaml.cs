using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// "配置工具":上半是 MCP 服务器的增删改查,下半按来源分组(内置 / 每台 MCP 服务器)
/// 列出全部工具逐个勾选;每台 MCP 服务器都可以"更新工具库" —— 连上去把它现在提供的工具重新拉一遍。
/// </summary>
/// <remarks>
/// 勾选状态<b>存的是"没勾的那些"</b>(<see cref="McpServerConfig.DisabledTools" /> /
/// <see cref="AiSettings.DisabledBuiltinTools" />):服务器以后新增了工具,默认就是可用的,
/// 而不是因为"不在已保存的白名单里"被静默屏蔽掉。
/// </remarks>
public sealed class ToolPickerView : UserControl
{
    private readonly IPluginContext _context;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Func<Task> _persist;
    private readonly StackPanel _groups = new() { Spacing = 10 };
    private readonly TextBlock _status = new() { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap };

    /// <param name="context">插件上下文(只用来记日志)。</param>
    /// <param name="settings">面板共享的设置实例;勾选直接改它。</param>
    /// <param name="loc">多语言文案。</param>
    /// <param name="persist">把设置落盘(勾一下就存一次,没有"保存"按钮)。</param>
    public ToolPickerView(IPluginContext context, AiSettings settings, Loc loc, Func<Task> persist)
    {
        _context = context;
        _settings = settings;
        _loc = loc;
        _persist = persist;

        // MCP 服务器在上、它们的工具勾选在下:加完一台服务器,要挑的工具就在正下方,
        // 不必再去别的窗口,加/存/测试都会立刻重建下半页。
        var servers = new McpServersView(context, settings, loc, persist);
        servers.ServersChanged += Rebuild;

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Section(loc["McpServers"], servers));
        content.Children.Add(Section(loc["ConfigureTools"], _groups));

        var root = new DockPanel();
        _status.Margin = new Avalonia.Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            // 覆盖式滚动条会压住右缘的输入框,留白放在内容上而不是 ScrollViewer.Padding
            Content = new Border { Padding = new Avalonia.Thickness(0, 0, 14, 0), Child = content }
        });
        Content = root;
        Rebuild();
    }

    /// <summary>一节:小标题 + 内容,两节之间用它拉开层次。</summary>
    private static Control Section(string title, Control body)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13
        });
        stack.Children.Add(body);
        return stack;
    }

    /// <summary>整页重建(刷新工具库之后也走这里)。</summary>
    private void Rebuild()
    {
        _groups.Children.Clear();
        _groups.Children.Add(BuildBuiltinGroup());
        foreach (McpServerConfig server in _settings.McpServers)
        {
            _groups.Children.Add(BuildMcpGroup(server));
        }
        _status.Text = _loc.F("ToolsSelected", CountEnabled());
    }

    private Border BuildBuiltinGroup()
    {
        HashSet<string> disabled = Lines(_settings.DisabledBuiltinTools);
        var rows = new StackPanel { Spacing = 2 };
        foreach ((string name, string description, bool readOnly) in AgentToolbox.Catalog)
        {
            rows.Children.Add(BuildRow(name, description, readOnly, !disabled.Contains(name), enabled =>
            {
                Toggle(disabled, name, enabled);
                _settings.DisabledBuiltinTools = string.Join('\n', disabled);
                AfterChange();
            }));
        }
        return BuildGroup(_loc["ToolsBuiltin"], rows, header: null);
    }

    private Border BuildMcpGroup(McpServerConfig server)
    {
        HashSet<string> disabled = Lines(server.DisabledTools);
        var rows = new StackPanel { Spacing = 2 };
        if (server.KnownTools.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Classes = { "dim" },
                Text = _loc["ToolsNotLoaded"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(20, 2, 0, 2)
            });
        }
        foreach (McpToolInfo tool in server.KnownTools)
        {
            rows.Children.Add(BuildRow(tool.Name, tool.Description, tool.ReadOnly, !disabled.Contains(tool.Name),
                enabled =>
                {
                    Toggle(disabled, tool.Name, enabled);
                    server.DisabledTools = string.Join('\n', disabled);
                    AfterChange();
                }));
        }

        // 每台服务器一枚"更新工具库":连上去把它现在提供的工具重新列一遍
        var refresh = new Button
        {
            Content = _loc["RefreshTools"],
            Height = 24,
            Padding = new Avalonia.Thickness(10, 0),
            FontSize = 11
        };
        refresh.Click += async (_, _) => await RefreshAsync(server, refresh);

        string title = string.IsNullOrWhiteSpace(server.Name) ? "(unnamed MCP)" : server.Name;
        if (server.ToolsRefreshedAt is { } at)
        {
            title += $"　{at.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
        if (!server.Enabled)
        {
            title += "　⏸";
        }
        return BuildGroup(title, rows, refresh);
    }

    private static Border BuildGroup(string title, Control rows, Control? header)
    {
        var head = new Grid { ColumnDefinitions = [with("*,Auto")] };
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (header is not null)
        {
            Grid.SetColumn(header, 1);
            head.Children.Add(header);
        }
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(head);
        stack.Children.Add(rows);
        return new Border { Classes = { "toolGroup" }, Child = stack };
    }

    /// <summary>一行:勾选框 + 工具名 + 说明,只读工具额外标一下(它们不走审批)。</summary>
    private CheckBox BuildRow(string name, string description, bool readOnly, bool enabled, Action<bool> onChanged)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(new TextBlock { Classes = { "toolName" }, Text = name });
        if (readOnly)
        {
            label.Children.Add(new TextBlock { Classes = { "dim" }, Text = _loc["ToolReadOnly"] });
        }
        if (description.Length > 0)
        {
            label.Children.Add(new TextBlock
            {
                Classes = { "dim" },
                Text = description,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 420
            });
        }
        var check = new CheckBox { Content = label, IsChecked = enabled, Margin = new Avalonia.Thickness(18, 0, 0, 0) };
        check.IsCheckedChanged += (_, _) => onChanged(check.IsChecked == true);
        return check;
    }

    private async Task RefreshAsync(McpServerConfig server, Button trigger)
    {
        trigger.IsEnabled = false;
        _status.Text = _loc["RefreshingTools"];
        try
        {
            IReadOnlyList<McpToolInfo> tools = await McpManager.RefreshToolsAsync(server, CancellationToken.None);
            server.KnownTools = [.. tools];
            server.ToolsRefreshedAt = DateTimeOffset.UtcNow;
            await _persist();
            Rebuild();
            _status.Text = _loc.F("ToolsRefreshed", server.Name, tools.Count);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Refreshing MCP tools for '{server.Name}' failed: {ex.Message}");
            _status.Text = $"{_loc["Error"]}: {ex.Message}";
        }
        finally
        {
            trigger.IsEnabled = true;
        }
    }

    private void AfterChange()
    {
        _status.Text = _loc.F("ToolsSelected", CountEnabled());
        _ = _persist();
    }

    /// <summary>当前勾上的工具总数(内置 + 各 MCP)。</summary>
    private int CountEnabled()
    {
        HashSet<string> builtinOff = Lines(_settings.DisabledBuiltinTools);
        int total = AgentToolbox.Catalog.Count(t => !builtinOff.Contains(t.Name));
        foreach (McpServerConfig server in _settings.McpServers)
        {
            HashSet<string> off = Lines(server.DisabledTools);
            total += server.KnownTools.Count(t => !off.Contains(t.Name));
        }
        return total;
    }

    private static HashSet<string> Lines(string text)
        => new(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private static void Toggle(HashSet<string> disabled, string name, bool enabled)
    {
        if (enabled)
        {
            disabled.Remove(name);
        }
        else
        {
            disabled.Add(name);
        }
    }
}

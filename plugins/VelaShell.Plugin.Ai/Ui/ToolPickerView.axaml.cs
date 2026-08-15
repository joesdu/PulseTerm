using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// "配置工具":按来源分组(内置 / 每台 MCP 服务器)列出全部工具,逐个勾选是否暴露给模型;
/// 每台 MCP 服务器都可以"更新工具库" —— 连上去把它现在提供的工具重新拉一遍。
/// </summary>
/// <remarks>
/// 勾选状态<b>存的是"没勾的那些"</b>(<see cref="McpServerConfig.DisabledTools" /> /
/// <see cref="AiSettings.DisabledBuiltinTools" />):服务器以后新增了工具,默认就是可用的,
/// 而不是因为"不在已保存的白名单里"被静默屏蔽掉。
///
/// <para>服务器本身怎么配在<see cref="McpServersView" />(标题行右侧的 ⚙ 另开一个窗口)——
/// 那是一整套左列表右表单,压在勾选列表上面会把这一页挤得没法看;
/// 改完它会回调 <see cref="Rebuild" />,这边的分组当场跟着变。</para>
/// </remarks>
public sealed class ToolPickerView : UserControl
{
    private readonly IPluginContext _context;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Func<Task> _persist;
    private readonly StackPanel _groups = new() { Spacing = 14 };
    private readonly TextBlock _status = new() { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap };

    /// <summary>用户点了右上角的 ⚙,要去配 MCP 服务器。</summary>
    public event Action? ConfigureServersRequested;

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
        // 这套版式规则原先由插件自己的对话框外壳下发;窗体换成宿主的自绘卡片之后自己带上
        Styles.Add(new StyleInclude(new Uri("avares://VelaShell.Plugin.Ai/"))
        {
            Source = new Uri("avares://VelaShell.Plugin.Ai/Ui/DialogStyles.axaml")
        });
        // 勾选框要用插件自建的那套(15×15 + 强调色):Fluent 默认那个是高饱和蓝方块,
        // 跟本程序的强调色不是一回事,一屏十几个尤其扎眼。
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://VelaShell.Plugin.Ai/"))
        {
            Source = new Uri("avares://VelaShell.Plugin.Ai/Ui/AiTheme.axaml")
        });

        // 内边距 20/16:内容直接贴着窗口边框很难看(这是补上的)。
        // 右边这 20 拆成 10(根)+ 10(滚动区内):滚动条是浮在内容上、贴着<b>滚动区</b>右缘画的,
        // 全放根上的话它就飘在卡片边上了;拆开之后滚动区比卡片宽出 10,条子正好落在空档里。
        // 卡片左右仍旧各离窗口 20(离屏渲染量过:左 20 / 右 20)。
        const double Gutter = 10;
        var root = new DockPanel { Margin = new Avalonia.Thickness(20, 16, 20 - Gutter, 16) };
        _status.Margin = new Avalonia.Thickness(0, 10, Gutter, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(BuildHeader(Gutter));
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            // 覆盖式滚动条会压住右缘,留白放在内容上而不是 ScrollViewer.Padding
            Content = new Border { Padding = new Avalonia.Thickness(0, 0, Gutter, 0), Child = _groups }
        });
        Content = root;
        Rebuild();
    }

    /// <summary>
    /// 顶上一行:左边一句说明,右边那枚 ⚙ 通向 MCP 服务器配置。
    /// </summary>
    /// <remarks>
    /// 放在内容区右上而不是窗口标题栏里:标题栏由宿主自绘(和链路追踪那些窗口同一套规格),
    /// 插件插不进去,也不该插。
    /// </remarks>
    /// <param name="gutter">右侧让给滚动条的那条沟槽,标题行要自己补回来才和卡片右缘对齐。</param>
    private Grid BuildHeader(double gutter)
    {
        var gear = new Button
        {
            Height = 26,
            Width = 30,
            Padding = default,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new Viewbox { Width = 13, Height = 13, Child = Glyph("Icon.settings") }
        };
        gear[!TemplatedControl.ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        ToolTip.SetTip(gear, _loc["McpServers"]);
        gear.Click += (_, _) => ConfigureServersRequested?.Invoke();

        var row = new Grid
        {
            ColumnDefinitions = [with("*,Auto")],
            Margin = new Avalonia.Thickness(0, 0, gutter, 10)
        };
        row.Children.Add(new TextBlock
        {
            Classes = { "dim" },
            Text = _loc["McpHint"],
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 10, 0)
        });
        Grid.SetColumn(gear, 1);
        row.Children.Add(gear);
        DockPanel.SetDock(row, Dock.Top);
        return row;
    }

    /// <summary>
    /// 一枚 lucide 图标。几何与描边都用 <see cref="DynamicResourceExtension" /> 绑,
    /// <b>不要 FindResource 取一次</b> —— Vela* 颜色令牌住在 ThemeDictionaries 里,
    /// 一次性取值经常拿到 null,结果就是"按钮框在、图标不见了"。
    /// </summary>
    private static Avalonia.Controls.Shapes.Path Glyph(string icon)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Width = 24,
            Height = 24,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        path[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension(icon);
        path[!Shape.StrokeProperty] = new DynamicResourceExtension("VelaTextSecondary");
        return path;
    }

    /// <summary>整页重建(改完 MCP 服务器、刷新工具库之后都走这里)。</summary>
    public void Rebuild()
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
        var rows = new StackPanel { Spacing = 8 };
        foreach ((string name, string description, bool readOnly) in AgentToolbox.Catalog)
        {
            rows.Children.Add(BuildRow(name, description, readOnly, !disabled.Contains(name), enabled =>
            {
                Toggle(disabled, name, enabled);
                _settings.DisabledBuiltinTools = string.Join('\n', disabled);
                AfterChange();
            }));
        }
        return BuildGroup(_loc["ToolsBuiltin"], subtitle: "", rows, header: null);
    }

    private Border BuildMcpGroup(McpServerConfig server)
    {
        HashSet<string> disabled = Lines(server.DisabledTools);
        var rows = new StackPanel { Spacing = 8 };
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
            Height = 26,
            Padding = new Avalonia.Thickness(12, 0),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        refresh[!TemplatedControl.ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        refresh.Click += async (_, _) => await RefreshAsync(server, refresh);

        // 刷新时刻是次要信息,压成小字跟在名字后面,别和名字抢同一个字重
        string subtitle = server.ToolsRefreshedAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
        if (!server.Enabled)
        {
            subtitle = subtitle.Length > 0 ? $"{subtitle} · {_loc["McpDisabledMark"]}" : _loc["McpDisabledMark"];
        }
        return BuildGroup(
            string.IsNullOrWhiteSpace(server.Name) ? "(unnamed MCP)" : server.Name, subtitle, rows, refresh);
    }

    /// <summary>一组:标题(+ 小字副标题)+ 可选的右侧按钮 + 若干勾选行。</summary>
    private static Border BuildGroup(string title, string subtitle, Control rows, Control? header)
    {
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(new TextBlock
        {
            Classes = { "section-title" },
            Text = title,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (subtitle.Length > 0)
        {
            caption.Children.Add(new TextBlock
            {
                Classes = { "dim" },
                Text = subtitle,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var head = new Grid { ColumnDefinitions = [with("*,Auto")] };
        head.Children.Add(caption);
        if (header is not null)
        {
            Grid.SetColumn(header, 1);
            head.Children.Add(header);
        }
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(head);
        stack.Children.Add(new Border { Classes = { "sep" } });
        stack.Children.Add(rows);
        // section = 外观(与设置页那些分节同一张卡片);toolGroup 只是个语义标记,没有样式,
        // 供测试精确认出"一个来源一组"这件事。
        return new Border { Classes = { "section", "toolGroup" }, Child = stack };
    }

    /// <summary>一行:勾选框 + 工具名 + 说明,只读工具额外标一下(它们不走审批)。</summary>
    /// <remarks>
    /// 名字与说明<b>分两行</b>,说明整段换行。挤在一行里用省略号截断时,MCP 那些长描述
    /// 全都断在半句上("This tool retrieves the latest complete co…"),既占地方又什么都没说清。
    /// </remarks>
    private CheckBox BuildRow(string name, string description, bool readOnly, bool enabled, Action<bool> onChanged)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Classes = { "toolName" }, Text = name });
        if (readOnly)
        {
            title.Children.Add(new TextBlock { Classes = { "dim" }, Text = _loc["ToolReadOnly"] });
        }

        var label = new StackPanel { Spacing = 2 };
        label.Children.Add(title);
        if (description.Length > 0)
        {
            label.Children.Add(new TextBlock
            {
                Classes = { "hint" },
                Text = description,
                Margin = default
            });
        }
        var check = new CheckBox
        {
            Content = label,
            IsChecked = enabled,
            Margin = new Avalonia.Thickness(4, 0, 0, 0),
            Padding = new Avalonia.Thickness(8, 4, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        check[!TemplatedControl.ThemeProperty] = new DynamicResourceExtension("AiCheckBoxTheme");
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

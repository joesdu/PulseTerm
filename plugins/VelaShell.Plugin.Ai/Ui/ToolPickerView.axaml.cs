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
/// <para>版式是"左栏服务器概览 + 右栏工具清单"(设计图 G):配完一台服务器紧接着就在同一屏勾选它的工具,
/// 不用在两个窗口之间来回切。左栏只摆概览(状态点 + 名字 + 传输 · 工具库状态);
/// 服务器<b>本身怎么配</b>仍在 <see cref="McpServersView" /> —— 它自己就是一整套左列表右表单,
/// 塞不进 270 宽的左栏,所以另开一个窗口,点概览行或「新增服务器」进去。
/// 改完它会回调 <see cref="Rebuild" />,这边的左栏与分组当场跟着变。</para>
/// </remarks>
public sealed class ToolPickerView : UserControl
{
    private readonly IPluginContext _context;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Func<Task> _persist;
    private readonly StackPanel _groups = new() { Spacing = 14 };
    private readonly TextBlock _status = new() { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap };

    /// <summary>左栏的服务器概览行容器。</summary>
    private readonly StackPanel _servers = new();

    /// <summary>一台服务器都没有时左栏摆的那句话。</summary>
    private readonly TextBlock _serversEmpty = new() { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    /// <summary>点某台服务器(或「新增」,此时参数为 null)时打开那张配置表单。</summary>
    private readonly Action<string?> _openServerEditor;

    /// <summary>
    /// 各组的折叠状态,键 = 组 id(内置 = 空串,MCP = 服务器 id)。只活在这个视图里,不落盘:
    /// 重建(改服务器 / 刷新工具库)时保住用户刚才折/展的样子就够了。
    /// MCP 组默认折起 —— 一台服务器动辄二三十个工具,全展开这页长得没法看;
    /// 内置那组就几个,默认展开。
    /// </summary>
    private readonly Dictionary<string, bool> _collapsed = [with(StringComparer.Ordinal)];

    /// <param name="context">插件上下文(只用来记日志)。</param>
    /// <param name="settings">面板共享的设置实例;勾选直接改它。</param>
    /// <param name="loc">多语言文案。</param>
    /// <param name="persist">把设置落盘(勾一下就存一次,没有"保存"按钮)。</param>
    /// <param name="openServerEditor">
    /// 打开某台服务器的配置表单;参数为 null 表示"新增一台"。
    /// 左栏只摆概览,表单仍在 <see cref="McpServersView" />(它自己就是左列表右表单,塞不进 270 宽)。
    /// </param>
    public ToolPickerView(IPluginContext context, AiSettings settings, Loc loc, Func<Task> persist,
        Action<string?> openServerEditor)
    {
        _context = context;
        _settings = settings;
        _loc = loc;
        _persist = persist;
        _openServerEditor = openServerEditor;
        _serversEmpty.Text = _loc["McpNoServers"];
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
        var right = new DockPanel { Margin = new Avalonia.Thickness(20, 16, 20 - Gutter, 16) };
        _status.Margin = new Avalonia.Thickness(0, 10, Gutter, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        var hint = new TextBlock
        {
            Classes = { "dim" },
            Text = _loc["McpHint"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, Gutter, 10)
        };
        DockPanel.SetDock(hint, Dock.Top);
        right.Children.Add(hint);
        right.Children.Add(_status);
        right.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            // 覆盖式滚动条会压住右缘,留白放在内容上而不是 ScrollViewer.Padding
            Content = new Border { Padding = new Avalonia.Thickness(0, 0, Gutter, 0), Child = _groups }
        });

        // 左栏:MCP 服务器概览。配好一台服务器紧接着就要挑它的工具,那份勾选列表就在右边 ——
        // 原先两者隔着两个窗口来回切。这里只摆概览(状态点 + 名字 + 传输),
        // 真要改仍旧回到 McpServersView 那张"左列表右表单" —— 它塞不进 270 宽。
        var left = new Grid { RowDefinitions = [with("Auto,Auto,*,Auto")], Margin = new Avalonia.Thickness(16, 16, 16, 16) };
        left.Children.Add(new TextBlock
        {
            Classes = { "section-title" },
            Text = _loc["McpServers"],
            FontSize = 12
        });
        var paneHint = new TextBlock
        {
            Classes = { "dim" },
            Text = _loc["McpPaneHint"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 6, 0, 10)
        };
        Grid.SetRow(paneHint, 1);
        left.Children.Add(paneHint);
        var serverScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _servers
        };
        Grid.SetRow(serverScroll, 2);
        left.Children.Add(serverScroll);
        Button add = OutlineButton("Icon.plus", _loc["McpAdd"], () => _openServerEditor(null));
        add.Name = "McpRailAddButton"; // 用例按名字找它(文案随语言变,不能按文字找)
        add.Margin = new Avalonia.Thickness(0, 10, 0, 0);
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(add, 3);
        left.Children.Add(add);

        var rail = new Border
        {
            Width = 270,
            Child = left,
            BorderThickness = new Avalonia.Thickness(0, 0, 1, 0)
        };
        rail[!BackgroundProperty] = new DynamicResourceExtension("VelaBgSidebar");
        rail[!BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");

        var root = new Grid { ColumnDefinitions = [with("Auto,*")] };
        root.Children.Add(rail);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;
        Rebuild();
    }

    /// <summary>描边小按钮(图标 + 文字),供左栏的「新增服务器」与组里的「更新工具库」共用。</summary>
    private static Button OutlineButton(string icon, string text, Action onClick)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(new Viewbox { Width = 11, Height = 11, Child = Glyph(icon), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        var button = new Button { Content = content, Height = 26, Padding = new Avalonia.Thickness(10, 0) };
        button[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>左栏的服务器概览行:状态点 + 名字 + "传输 · 工具库状态"。点一下回到那张表单去改。</summary>
    private void RebuildServerRail()
    {
        _servers.Children.Clear();
        foreach (McpServerConfig server in _settings.McpServers)
        {
            string id = server.Id;
            var row = new Grid { ColumnDefinitions = [with("Auto,*")] };
            // 绿点 = 启用且工具库已拉过;其余一律灰点。没探过活就别拿颜色替用户下结论。
            bool live = server.Enabled && server.KnownTools.Count > 0;
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Avalonia.Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dot[!Shape.FillProperty] = new DynamicResourceExtension(live ? "VelaStatusConnected" : "VelaTextMuted");
            row.Children.Add(dot);

            var text = new StackPanel { Spacing = 2 };
            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(server.Name) ? _loc["Unnamed"] : server.Name,
                FontWeight = FontWeight.Medium,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            name[!ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
            text.Children.Add(name);
            text.Children.Add(new TextBlock
            {
                Classes = { "dim" },
                Text = ServerDetail(server),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var card = new Border
            {
                Classes = { "card" },
                Padding = new Avalonia.Thickness(9, 7),
                Margin = new Avalonia.Thickness(0, 0, 0, 5),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = row
            };
            card.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _openServerEditor(id);
            };
            _servers.Children.Add(card);
        }
        _serversEmpty.IsVisible = _settings.McpServers.Count == 0;
        if (_serversEmpty.IsVisible)
        {
            _servers.Children.Add(_serversEmpty);
        }
    }

    /// <summary>概览行的第二行文字:传输方式 · 工具库状态 [· 已停用]。</summary>
    private string ServerDetail(McpServerConfig server)
    {
        string transport = server.Transport == McpTransportType.Http ? "HTTP" : "Stdio";
        string tools = server.KnownTools.Count > 0
            ? _loc.F("McpToolCount", server.KnownTools.Count)
            : _loc["McpToolsNotLoaded"];
        string detail = $"{transport} · {tools}";
        return server.Enabled ? detail : $"{detail} · {_loc["McpDisabledMark"]}";
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
        RebuildServerRail();
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
        var rows = new StackPanel { Spacing = 8, Classes = { "toolRows" } }; // toolRows 只是语义标记,供测试认出勾选行容器
        foreach ((string name, string description, bool readOnly) in AgentToolbox.Catalog)
        {
            rows.Children.Add(BuildRow(name, description, readOnly, !disabled.Contains(name), enabled =>
            {
                Toggle(disabled, name, enabled);
                _settings.DisabledBuiltinTools = string.Join('\n', disabled);
                AfterChange();
            }));
        }
        int total = AgentToolbox.Catalog.Count;
        return BuildGroup("", _loc["ToolsBuiltin"], _loc.F("ToolsChecked", AgentToolbox.Catalog.Count(t => !disabled.Contains(t.Name)), total), rows, header: null, defaultCollapsed: false);
    }

    private Border BuildMcpGroup(McpServerConfig server)
    {
        HashSet<string> disabled = Lines(server.DisabledTools);
        var rows = new StackPanel { Spacing = 8, Classes = { "toolRows" } }; // toolRows 只是语义标记,供测试认出勾选行容器
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
        // 折起来时也得看得出这台勾了多少:数字放进副标题
        int total = server.KnownTools.Count;
        string counts = _loc.F("ToolsChecked", server.KnownTools.Count(t => !disabled.Contains(t.Name)), total);
        subtitle = subtitle.Length > 0 ? $"{counts} · {subtitle}" : counts;
        return BuildGroup(server.Id,
            string.IsNullOrWhiteSpace(server.Name) ? "(unnamed MCP)" : server.Name, subtitle, rows, refresh, defaultCollapsed: true);
    }

    /// <summary>
    /// 一组:可点的标题行(折叠箭头 + 标题 + 小字副标题)+ 可选的右侧按钮 + 若干勾选行。
    /// 点标题行折/展;折叠状态按 <paramref name="key" /> 记在 <see cref="_collapsed" /> 里,重建后仍保持。
    /// </summary>
    private Border BuildGroup(string key, string title, string subtitle, Control rows, Control? header, bool defaultCollapsed)
    {
        bool collapsed = _collapsed.TryGetValue(key, out bool saved) ? saved : defaultCollapsed;

        Avalonia.Controls.Shapes.Path chevron = Glyph("Icon.chevron-down");
        var chevronBox = new Viewbox
        {
            Width = 12,
            Height = 12,
            Child = chevron,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = Avalonia.RelativePoint.Center
        };
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(chevronBox);
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
        // 整个标题区(不含右侧按钮)都是折叠热区,而不是只有那枚小箭头
        var toggle = new Border
        {
            Child = caption,
            Padding = new Avalonia.Thickness(0, 2),
            Background = Brushes.Transparent, // 透明也要有:不然空白处不命中,点标题字之间的缝隙没反应
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Classes = { "toolGroupToggle" }
        };

        var sep = new Border { Classes = { "sep" } };
        void Apply()
        {
            rows.IsVisible = !collapsed;
            sep.IsVisible = !collapsed;
            // 折起来箭头朝右、展开朝下 —— 与树形控件的习惯一致
            chevronBox.RenderTransform = collapsed ? new RotateTransform(-90) : null;
        }
        Apply();
        toggle.PointerPressed += (_, e) =>
        {
            collapsed = !collapsed;
            _collapsed[key] = collapsed;
            Apply();
            e.Handled = true;
        };

        var head = new Grid { ColumnDefinitions = [with("*,Auto")] };
        head.Children.Add(toggle);
        if (header is not null)
        {
            Grid.SetColumn(header, 1);
            head.Children.Add(header);
        }
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(head);
        stack.Children.Add(sep);
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

using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.ViewModels;

/// <summary>
/// 下拉里的一项。是 <see cref="ProtocolSettingChoice" /> 的宿主侧包装,存在的理由只有一条:
/// <see cref="ToString" /> 必须返回**落盘值**。
/// <para>
/// 可编辑下拉(<c>ComboBox.IsEditable</c>)在用户选中一项时,会拿
/// <c>item.ToString()</c> 去填那个文本框。直接把 SDK 的 record 丢进去,文本框里出现的是
/// <c>ProtocolSettingChoice { Value = COM3, Label = … }</c>,而且它就是接下来落盘的值。
/// 包一层之后:下拉里按 <see cref="Label" /> 显示("USB-SERIAL CH340 (COM3)"),
/// 选中后文本框与落盘值都是 <see cref="Value" />("COM3")。
/// </para>
/// </summary>
/// <param name="Value">落盘值。</param>
/// <param name="Label">展示文案。</param>
public sealed record PluginChoiceItem(string Value, string Label)
{
    /// <summary>从 SDK 的候选项包一层。</summary>
    /// <param name="choice">SDK 候选项。</param>
    /// <returns>包装后的项。</returns>
    public static PluginChoiceItem From(ProtocolSettingChoice choice) => new(choice.Value, choice.Label);

    /// <summary>返回**落盘值** —— 可编辑下拉靠它决定选中一项后文本框里是什么。</summary>
    /// <returns>落盘值。</returns>
    public override string ToString() => Value;
}

/// <summary>连接配置页上的一个插件协议页签。</summary>
/// <param name="id">协议 id。</param>
/// <param name="displayName">页签名称。</param>
/// <param name="defaultPort">新建配置时的默认端口。</param>
public sealed class PluginProtocolTabViewModel(string id, string displayName, int defaultPort) : ReactiveObject
{
    /// <summary>协议 id。</summary>
    public string Id { get; } = id;

    /// <summary>页签名称。</summary>
    public string DisplayName { get; } = displayName;

    /// <summary>新建配置时的默认端口。</summary>
    public int DefaultPort { get; } = defaultPort;

    /// <summary>
    /// 是否为当前选中的页签。做成页签自己的状态而不是在 XAML 里拿转换器比对 ——
    /// <c>ConverterParameter</c> 不能是绑定,那条路在 Avalonia 上走不通。
    /// </summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

/// <summary>
/// "已保存的 SSH 配置"下拉里的一项。
/// <para>
/// 空 <see cref="Id" /> 表示"不经跳板机" —— 它必须是列表里的第一项而不是靠"没选中"表达:
/// 一个空着的下拉在界面上无法区分"我不用隧道"与"我还没选"。
/// </para>
/// </summary>
/// <param name="Id">会话配置 id;空串表示不经跳板机。</param>
/// <param name="Display">展示文本(名称 + 主机)。</param>
public sealed record PluginSessionChoice(string Id, string Display);

/// <summary>
/// 连接配置页上的一个插件协议设置字段。
/// <para>
/// 宿主按插件声明的 <see cref="ProtocolSettingField" /> 渲染出与内建协议一致的表单,
/// 插件因此**不需要写一行界面代码**。之所以做成声明式 schema 而不是让插件塞控件树:
/// 连接对话框是宿主的核心界面,布局、主题、校验与本地化都由它统一负责;
/// 插件只描述"要哪些参数"。这与蓝图 08 明确不做的通用声明式 UI 是两回事 ——
/// 那是任意界面,这只是一张形状封闭的参数表。
/// </para>
/// </summary>
public sealed class PluginProtocolFieldViewModel : ReactiveObject
{
    private string _text = string.Empty;

    /// <summary>创建字段视图模型。</summary>
    /// <param name="field">插件声明的字段。</param>
    /// <param name="value">当前值;为 null 时用字段声明的默认值。</param>
    /// <param name="sshSessions">
    /// 可选的 SSH 配置候选(仅 <see cref="ProtocolSettingKind.SshSession" /> 形态用到)。
    /// 由 <see cref="ConnectionProfileViewModel" /> 传入 —— 字段视图模型不该认识会话仓储。
    /// </param>
    /// <param name="reload">
    /// 重新向插件索取候选项(仅 <see cref="ProtocolSettingKind.DynamicChoice" /> 形态用到)。
    /// 由 <see cref="ConnectionProfileViewModel" /> 传入 —— 字段视图模型不该认识插件注册表。
    /// </param>
    public PluginProtocolFieldViewModel(
        ProtocolSettingField field,
        string? value,
        IReadOnlyList<PluginSessionChoice>? sshSessions = null,
        Func<PluginProtocolFieldViewModel, Task>? reload = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        Field = field;
        SshSessions = sshSessions ?? [];
        _text = value ?? field.DefaultValue ?? string.Empty;
        Choices = [.. field.Choices.Select(PluginChoiceItem.From)];
        RefreshChoicesCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (reload is null)
                {
                    return;
                }
                IsRefreshing = true;
                try
                {
                    await reload(this).ConfigureAwait(true);
                }
                finally
                {
                    IsRefreshing = false;
                }
            },
            // 刷新期间不让再点:枚举串口要几十毫秒,连点会把候选项列表反复重建。
            this.WhenAnyValue(x => x.IsRefreshing, refreshing => !refreshing));
        // 下拉的值对不上任何选项时(插件没给默认值、或升级后换了枚举),在构造时就归一到第一项。
        // 放在 getter 里兜底是不行的:源→目标方向的推送不经过 setter,界面显示第一项、
        // 落盘却仍是那个失效的旧值。
        //
        // 两类下拉**刻意不做**这次归一:
        //   · 可手输的(AllowsCustomValue)—— "值不在表里"正是它存在的意义(非标波特率);
        //   · 动态的(DynamicChoice)—— 候选项是打开表单时现取的。一条存着 COM7 的串口配置
        //     在适配器没插的时候打开,值被悄悄改写成 COM3 再保存下去,是一次静默的数据损坏。
        if (field.Kind == ProtocolSettingKind.Choice && !field.AllowsCustomValue && field.Choices.Count > 0
            && field.Choices.All(choice => choice.Value != _text))
        {
            _text = field.Choices[0].Value;
        }
    }

    /// <summary>字段声明。</summary>
    public ProtocolSettingField Field { get; }

    /// <summary>字段键(落进 <see cref="Core.Models.SessionProfile.PluginSettings" /> 的键)。</summary>
    public string Key => Field.Key;

    /// <summary>字段标签。</summary>
    public string Label => Field.Label;

    /// <summary>字段下方的说明。</summary>
    public string Hint => Field.Hint ?? string.Empty;

    /// <summary>是否有说明文字。</summary>
    public bool HasHint => Hint.Length > 0;

    /// <summary>输入框占位提示。</summary>
    public string Placeholder => Field.Placeholder ?? string.Empty;

    /// <summary>
    /// 下拉候选项。**可观察集合**而不是直读 <see cref="ProtocolSettingField.Choices" />:
    /// <see cref="ProtocolSettingKind.DynamicChoice" /> 的候选项是打开表单时向插件现取的,
    /// 之后用户还能按刷新再取一次(串口:USB 适配器是热插拔的)。
    /// 静态形态下它就是声明里那一份,只是永远不变。
    /// </summary>
    public ObservableCollection<PluginChoiceItem> Choices { get; }

    /// <summary>重新向插件索取候选项(动态下拉旁边那个刷新按钮)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshChoicesCommand { get; }

    /// <summary>是否为机密字段(随口令一起加密落盘)。</summary>
    public bool IsSecret => Field.IsSecret;

    /// <summary>是否属于「高级选项」(折叠时不显示)。</summary>
    public bool IsAdvanced => Field.IsAdvanced;

    /// <summary>
    /// 这一行当前显不显示。由 <see cref="ConnectionProfileViewModel" /> 按
    /// 「高级选项」的展开状态统一下发 —— 做成字段自己的状态,是因为模板里没有
    /// 现成的路子去比对父视图模型的属性(同 <see cref="PluginProtocolTabViewModel.IsSelected" />)。
    /// </summary>
    public bool IsRowVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>是否为单行文本输入(含数字 —— 数字只是带校验的文本)。</summary>
    public bool IsText => Field.Kind is ProtocolSettingKind.Text or ProtocolSettingKind.Integer;

    /// <summary>是否为掩码输入。</summary>
    public bool IsPassword => Field.Kind == ProtocolSettingKind.Password;

    /// <summary>是否为复选框。</summary>
    public bool IsToggle => Field.Kind == ProtocolSettingKind.Boolean;

    /// <summary>是否为**只读**下拉(值只能取自候选项)。</summary>
    public bool IsChoice => Field.Kind is ProtocolSettingKind.Choice or ProtocolSettingKind.DynamicChoice
                            && !Field.AllowsCustomValue;

    /// <summary>
    /// 是否为**可编辑**下拉(候选项是便利,不是白名单)。
    /// <para>
    /// 波特率就是活例子:表里给九个常用值,但 250000(Marlin 固件)、76800(工业模块)
    /// 都得填得进去 —— 做成封闭枚举等于对这些用户说"本工具不支持你的设备"。
    /// </para>
    /// </summary>
    public bool IsEditableChoice => Field.Kind is ProtocolSettingKind.Choice or ProtocolSettingKind.DynamicChoice
                                    && Field.AllowsCustomValue;

    /// <summary>候选项是否要向插件现取(界面据此在下拉旁给一个刷新按钮)。</summary>
    public bool IsDynamicChoice => Field.Kind == ProtocolSettingKind.DynamicChoice;

    /// <summary>正在向插件取候选项(刷新按钮转圈)。</summary>
    public bool IsRefreshing
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 换一批候选项。**保留当前值**:列表里没有它也照旧留着,由可编辑下拉显示出来 ——
    /// 用户存的那个端口这次没枚举到(适配器没插),不代表这条配置该被改写。
    /// </summary>
    /// <param name="choices">新的候选项;空表示这次什么也没列出来,退回声明里的兜底列表。</param>
    public void ReplaceChoices(IReadOnlyList<ProtocolSettingChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        Choices.Clear();
        foreach (ProtocolSettingChoice choice in choices.Count > 0 ? choices : Field.Choices)
        {
            Choices.Add(PluginChoiceItem.From(choice));
        }
        this.RaisePropertyChanged(nameof(SelectedChoice));
    }

    /// <summary>是否为"已保存的 SSH 配置"选择器。</summary>
    public bool IsSshSession => Field.Kind == ProtocolSettingKind.SshSession;

    /// <summary>SSH 配置候选(含一条"不经跳板机"的空项)。</summary>
    public IReadOnlyList<PluginSessionChoice> SshSessions { get; }

    /// <summary>
    /// 当前选中的 SSH 配置;值对不上任何一条(配置被删了)时取"不经跳板机" ——
    /// **不能保留那个失效的 id**,否则打开会话时会拿一个找不到的跳板机去建隧道。
    /// </summary>
    public PluginSessionChoice? SelectedSshSession
    {
        // 兜底项走索引器而非 FirstOrDefault():本 getter 挂在绑定上,每次刷新都会跑,
        // 而 SshSessions 是可索引集合 —— LINQ 那条要建一个枚举器。
        get => SshSessions.FirstOrDefault(choice => choice.Id == _text)
               ?? (SshSessions.Count > 0 ? SshSessions[0] : null);
        set
        {
            if (value is not null)
            {
                Text = value.Id;
            }
        }
    }

    /// <summary>文本/口令/数字/下拉的当前值(下拉存的是选项的 <c>Value</c>)。</summary>
    public string Text
    {
        get => _text;
        set
        {
            this.RaiseAndSetIfChanged(ref _text, value);
            this.RaisePropertyChanged(nameof(Toggle));
            this.RaisePropertyChanged(nameof(SelectedChoice));
            this.RaisePropertyChanged(nameof(SelectedSshSession));
        }
    }

    /// <summary>复选框的当前值(以 <c>"true"</c>/<c>"false"</c> 存进 <see cref="Text" />)。</summary>
    public bool Toggle
    {
        get => bool.TryParse(_text, out bool parsed) && parsed;
        set => Text = value ? "true" : "false";
    }

    /// <summary>
    /// 下拉当前选中的项。
    /// <para>
    /// 只读下拉:值对不上任何选项时取第一项(而不是留空)—— 空下拉在界面上没法解释。
    /// 可编辑/动态下拉:对不上就是**没有选中项**,当前值由文本框自己显示 ——
    /// 那里回落到第一项等于把用户填的非标波特率、或适配器没插时存的那个端口悄悄换掉。
    /// </para>
    /// </summary>
    public PluginChoiceItem? SelectedChoice
    {
        get
        {
            PluginChoiceItem? exact = Choices.FirstOrDefault(choice => choice.Value == _text);
            return exact ?? (IsChoice ? Choices.FirstOrDefault() : null);
        }
        set
        {
            if (value is not null)
            {
                Text = value.Value;
            }
        }
    }
}

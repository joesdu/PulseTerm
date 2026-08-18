using ReactiveUI;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.ViewModels;

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
    public PluginProtocolFieldViewModel(
        ProtocolSettingField field,
        string? value,
        IReadOnlyList<PluginSessionChoice>? sshSessions = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        Field = field;
        SshSessions = sshSessions ?? [];
        _text = value ?? field.DefaultValue ?? string.Empty;
        // 下拉的值对不上任何选项时(插件没给默认值、或升级后换了枚举),在构造时就归一到第一项。
        // 放在 getter 里兜底是不行的:源→目标方向的推送不经过 setter,界面显示第一项、
        // 落盘却仍是那个失效的旧值。
        if (field.Kind == ProtocolSettingKind.Choice && field.Choices.Count > 0
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

    /// <summary>下拉候选项。</summary>
    public IReadOnlyList<ProtocolSettingChoice> Choices => Field.Choices;

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

    /// <summary>是否为下拉选择。</summary>
    public bool IsChoice => Field.Kind == ProtocolSettingKind.Choice;

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
        get => SshSessions.FirstOrDefault(choice => choice.Id == _text) ?? SshSessions.FirstOrDefault();
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

    /// <summary>下拉当前选中的项;值对不上任何选项时取第一项(而不是留空)。</summary>
    public ProtocolSettingChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(choice => choice.Value == _text) ?? Choices.FirstOrDefault();
        set
        {
            if (value is not null)
            {
                Text = value.Value;
            }
        }
    }
}

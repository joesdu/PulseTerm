using ReactiveUI;
using VelaShell.Core.Import;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>会话导入预览列表中的单行:包裹一条 <see cref="ImportedSession" /> 并提供勾选状态与展示文案。</summary>
public sealed class SessionImportItemViewModel : ReactiveObject
{
    /// <summary>用一条已解析的会话构造预览行;不受支持的协议默认不勾选。</summary>
    public SessionImportItemViewModel(ImportedSession source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        IsDuplicate = source.AlreadyExists;
        IsSelected = source.IsSupported && !IsDuplicate;
    }

    /// <summary>底层已解析的会话数据。</summary>
    public ImportedSession Source { get; }

    /// <summary>是否勾选导入。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>会话显示名称。</summary>
    public string Name => Source.Name;

    /// <summary>连接目标(<c>user@host:port</c> 或 <c>host:port</c>)。</summary>
    public string Endpoint =>
        Source.Username.Length > 0
            ? $"{Source.Username}@{Source.Host}:{Source.Port}"
            : $"{Source.Host}:{Source.Port}";

    /// <summary>协议标签(SSH / SFTP,或不支持时的原始协议名)。</summary>
    public string Protocol => Source.Protocol.ToUpperInvariant();

    /// <summary>该行是否可被勾选(不支持的协议禁用勾选)。</summary>
    public bool CanSelect => Source.IsSupported;

    /// <summary>密码状态文案:已还原 / 未还原(需手填) / 无密码。</summary>
    public string PasswordStatus =>
        !Source.IsSupported ? Strings.Get("XImport_Unsupported") :
        Source.PasswordRecovered ? Strings.Get("XImport_PwRecovered") :
        Source.HasEncryptedPassword ? Strings.Get("XImport_PwFailed") :
        Strings.Get("XImport_PwNone");

    /// <summary>
    /// 是否与已有会话重复:VelaShell 中已存在同目标(<see cref="ImportedSession.AlreadyExists" />),
    /// 或本次扫描中另一个来源已经给出了同一目标(由聚合视图模型跨来源去重后回填)。
    /// 重复项默认不勾选。
    /// </summary>
    public bool IsDuplicate
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ExistsHint));
        }
    }

    /// <summary>重复提示文案(仅在 <see cref="IsDuplicate" /> 时非空)。</summary>
    public string ExistsHint => IsDuplicate ? Strings.Get("XImport_Duplicate") : string.Empty;
}

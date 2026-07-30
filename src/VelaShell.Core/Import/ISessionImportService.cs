namespace VelaShell.Core.Import;

/// <summary>「浏览来源」时对话框应弹出的选择器类型。</summary>
public enum ImportBrowseKind
{
    /// <summary>不支持手动浏览(来源固定,如注册表)。</summary>
    None,

    /// <summary>选择一个文件夹(如 Xshell 的 Sessions 目录)。</summary>
    Folder,

    /// <summary>选择一个文件(如 WinSCP.ini)。</summary>
    File
}

/// <summary>
/// 会话一键迁移服务:从某个外部工具(Xshell、WinSCP 等)定位来源、解析会话
/// (含以当前 Windows 用户身份尝试还原保存的密码),并把选中的会话写入 VelaShell 仓储。
/// </summary>
public interface ISessionImportService
{
    /// <summary>来源名称(如 <c>Xshell</c>、<c>WinSCP</c>),用于对话框标题与默认分组名。</summary>
    string SourceKey { get; }

    /// <summary>手动浏览来源时应弹出的选择器类型。</summary>
    ImportBrowseKind BrowseKind { get; }

    /// <summary>
    /// 探测默认来源并返回其可读描述(目录/文件路径或注册表键);探测不到时返回 <c>null</c>。
    /// 该值仅用于展示,扫描时传 <c>null</c> 即让服务自动定位。
    /// </summary>
    string? DetectDefaultSource();

    /// <summary>
    /// 扫描来源并生成导入预览。<paramref name="source" /> 为 <c>null</c> 时由服务自动定位
    /// (Xshell 用注册表定位 Sessions 目录;WinSCP 优先读注册表,其次默认 INI)。
    /// </summary>
    /// <param name="source">显式来源(目录或文件路径);<c>null</c> 表示自动定位。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<SessionImportScan> ScanAsync(string? source, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将选中的会话导入 VelaShell:新建一个分组承载它们,并逐条持久化(密码由仓储 AES 重新加密落盘)。
    /// </summary>
    /// <param name="items">用户勾选要导入的会话。</param>
    /// <param name="groupName">新建承载分组的名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<SessionImportOutcome> ImportAsync(IReadOnlyList<ImportedSession> items, string groupName, CancellationToken cancellationToken = default);
}

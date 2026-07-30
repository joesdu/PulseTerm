namespace VelaShell.Core.Import;

/// <summary>扫描一个导入来源后的结果:发现的会话列表与整体状态。</summary>
public sealed class SessionImportScan
{
    /// <summary>实际读取的来源(目录路径、配置文件路径或注册表键)的可读描述。</summary>
    public required string Source { get; init; }

    /// <summary>解析出的可预览会话集合(含不受支持的协议,由各项自身的标记区分)。</summary>
    public required IReadOnlyList<ImportedSession> Items { get; init; }

    /// <summary>
    /// 来源是否启用了主密码;为 <c>true</c> 时无法在不知主密码的情况下解密任何密码,
    /// 所有会话将以「不含密码」方式导入。
    /// </summary>
    public bool MasterPasswordEnabled { get; init; }
}

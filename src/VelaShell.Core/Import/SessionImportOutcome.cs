namespace VelaShell.Core.Import;

/// <summary>执行导入后的结果统计。</summary>
public sealed class SessionImportOutcome
{
    /// <summary>成功写入 VelaShell 的会话数量。</summary>
    public int Imported { get; init; }

    /// <summary>其中成功还原了密码明文的会话数量。</summary>
    public int PasswordsRecovered { get; init; }

    /// <summary>新建承载分组的标识;未创建分组(导入数为 0)时为 <c>null</c>。</summary>
    public Guid? GroupId { get; init; }
}

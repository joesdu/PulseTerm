namespace VelaShell.Core.Models;

/// <summary>
/// 一次连接历史记录。写入 SonnetDB 时序 measurement <c>conn_history</c>,
/// 侧边栏“最近连接”按时间倒序读取并按目标去重展示。
/// </summary>
public class RecentConnectionEntry
{
    /// <summary>连接协议类型;缺失或未知值均按 SSH 处理(白名单同 <see cref="SessionProfile.ConnectionType" />)。</summary>
    public ConnectionType ConnectionType
    {
        get;
        set => field = Enum.IsDefined(value) ? value : ConnectionType.SSH;
    } = ConnectionType.SSH;

    /// <summary>关联的会话配置 Id;快速连接等临时连接为 null。</summary>
    public Guid? ProfileId { get; set; }

    /// <summary>显示名称(会话配置的名称;临时连接时为 user@host 形式)。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所属分组名称(冗余存储,便于直接展示“名称-分组”)。</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>目标主机地址。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>目标端口,默认 22。</summary>
    public int Port { get; set; } = 22;

    /// <summary>登录用户名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>连接发起时间(UTC)。</summary>
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>连接是否成功建立。</summary>
    public bool Success { get; set; } = true;

    /// <summary>建立连接耗时(毫秒)。</summary>
    public long DurationMs { get; set; }
}

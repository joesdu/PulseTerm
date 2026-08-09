using VelaShell.Core.Models;

namespace VelaShell.Core.Import;

/// <summary>从外部工具(Xshell、WinSCP 等)解析出的一条可导入会话,携带导入预览所需的元数据与状态。</summary>
public sealed class ImportedSession
{
    /// <summary>会话显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>目标主机地址(主机名或 IP)。</summary>
    public required string Host { get; init; }

    /// <summary>连接端口。</summary>
    public int Port { get; init; } = 22;

    /// <summary>登录用户名;来源未填写时为空串。</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>映射后的连接协议类型(仅支持 SSH / SFTP)。</summary>
    public ConnectionType ConnectionType { get; init; } = ConnectionType.SSH;

    /// <summary>原文件中的协议字段原值(如 SSH、SFTP、SCP、FTP),用于预览展示与不支持判定。</summary>
    public string Protocol { get; init; } = "SSH";

    /// <summary>该协议是否可被 VelaShell 导入(仅 SSH / SFTP 支持)。</summary>
    public bool IsSupported { get; init; } = true;

    /// <summary>来源是否包含非空的加密密码字段。</summary>
    public bool HasEncryptedPassword { get; init; }

    /// <summary>成功解密并通过校验的明文密码;未启用密码、解密失败或校验不过时为 <c>null</c>。</summary>
    public string? Password { get; init; }

    /// <summary>是否成功还原出密码明文(即 <see cref="Password" /> 非空)。</summary>
    public bool PasswordRecovered => Password is { Length: > 0 };

    /// <summary>VelaShell 中是否已存在同主机/端口/用户名的会话(用于提示重复)。</summary>
    public bool AlreadyExists { get; init; }

    /// <summary>来源标识(文件路径或注册表键),用于诊断。</summary>
    public string Source { get; init; } = string.Empty;
}

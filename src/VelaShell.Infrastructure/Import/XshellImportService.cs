using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Import;

/// <summary>
/// Xshell 的 <see cref="ISessionImportService" /> 实现:定位 <c>Sessions</c> 目录、解析 <c>.xsh</c>
/// 会话(并尝试以当前 Windows 用户身份还原密码),将选中的会话写入 <see cref="ISessionRepository" />。
/// </summary>
public sealed class XshellImportService(ISessionRepository repository) : ISessionImportService
{
    private readonly ISessionRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <inheritdoc />
    public string SourceKey => "Xshell";

    /// <inheritdoc />
    public ImportBrowseKind BrowseKind => ImportBrowseKind.Folder;

    /// <inheritdoc />
    public string? DetectDefaultSource() => DetectSessionsDirectory();

    /// <inheritdoc />
    public async Task<SessionImportScan> ScanAsync(string? source, CancellationToken cancellationToken = default)
    {
        string? directory = string.IsNullOrWhiteSpace(source) ? DetectSessionsDirectory() : source;
        if (directory is null || !Directory.Exists(directory))
        {
            return new SessionImportScan { Source = directory ?? string.Empty, Items = [] };
        }

        bool masterPasswordEnabled = IsMasterPasswordEnabled(directory);
        (string userName, string sid) = GetCurrentUser();

        // 以已存在会话的 主机|端口|用户名 作为重复判据。
        List<SessionProfile> existing = await _repository.GetAllSessionsAsync().ConfigureAwait(false);
        var existingKeys = existing
            .Select(static s => DedupKey(s.Host, s.Port, s.Username))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<ImportedSession>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.xsh", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            XshellSessionFile parsed;
            try
            {
                parsed = XshellIniParser.Parse(ReadLines(file));
            }
            catch (IOException)
            {
                continue; // 单个文件读失败不阻断整体扫描。
            }
            if (string.IsNullOrWhiteSpace(parsed.Host))
            {
                continue;
            }

            (ConnectionType type, bool supported) = MapProtocol(parsed.Protocol);
            bool hasEncrypted = !string.IsNullOrWhiteSpace(parsed.EncryptedPassword);
            string? password = null;
            if (hasEncrypted && !masterPasswordEnabled && userName.Length > 0)
            {
                password = XshellCrypto.TryDecryptPassword(parsed.EncryptedPassword, userName, sid);
            }

            FtpSettings? ftpSettings = MapFtpSettings(parsed.Protocol);
            // 端口缺省值按协议给:FTP 是 21,不是 SSH 的 22。
            int port = parsed.Port > 0 ? parsed.Port : ftpSettings is null ? 22 : FtpSettings.DefaultPort;
            items.Add(new ImportedSession
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Host = parsed.Host!.Trim(),
                Port = port,
                Username = parsed.UserName?.Trim() ?? string.Empty,
                ConnectionType = type,
                Protocol = string.IsNullOrWhiteSpace(parsed.Protocol) ? "SSH" : parsed.Protocol!.Trim(),
                IsSupported = supported,
                HasEncryptedPassword = hasEncrypted,
                Password = password,
                AlreadyExists = existingKeys.Contains(DedupKey(parsed.Host!, port, parsed.UserName ?? string.Empty)),
                Source = file,
                FtpSettings = ftpSettings
            });
        }

        return new SessionImportScan
        {
            Source = directory,
            Items = [.. items.OrderBy(static i => i.Name, StringComparer.OrdinalIgnoreCase)],
            MasterPasswordEnabled = masterPasswordEnabled
        };
    }

    /// <inheritdoc />
    public async Task<SessionImportOutcome> ImportAsync(IReadOnlyList<ImportedSession> items, string groupName, CancellationToken cancellationToken = default) =>
        await SessionImportWriter.WriteAsync(_repository, items, groupName, "Xshell", cancellationToken).ConfigureAwait(false);

    /// <summary>把 Xshell 协议字段映射为 VelaShell 连接类型;SSH / SFTP / FTP / FTPS 受支持。</summary>
    private static (ConnectionType Type, bool Supported) MapProtocol(string? protocol) =>
        protocol?.Trim().ToUpperInvariant() switch
        {
            "SFTP" => (ConnectionType.SFTP, true),
            "SSH" or null or "" => (ConnectionType.SSH, true),
            "FTP" or "FTPS" => (ConnectionType.FTP, true),
            _ => (ConnectionType.SSH, false)
        };

    /// <summary>Xshell 的 FTPS 走显式 TLS(默认 21 端口),明文 FTP 不加密。</summary>
    private static FtpSettings? MapFtpSettings(string? protocol) =>
        protocol?.Trim().ToUpperInvariant() switch
        {
            "FTPS" => new FtpSettings { EncryptionMode = FtpEncryptionMode.Explicit },
            "FTP" => new FtpSettings { EncryptionMode = FtpEncryptionMode.None },
            _ => null
        };

    private static string DedupKey(string host, int port, string user) => $"{host.Trim()}|{port}|{user.Trim()}";

    /// <summary>按 BOM 自动识别编码(Xshell 为 UTF-16 LE)读入全部行。</summary>
    private static IReadOnlyList<string> ReadLines(string path)
    {
        using var reader = new StreamReader(path, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return lines;
    }

    private static string? DetectSessionsDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        string? userDataPath = ReadUserDataPathFromRegistry();
        if (userDataPath is { Length: > 0 })
        {
            string sessions = Path.Combine(userDataPath, "Xshell", "Sessions");
            if (Directory.Exists(sessions))
            {
                return sessions;
            }
        }
        // 便携/自定义安装读不到注册表时,回退到默认文档位置(Xshell 5/6/7/8)。
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (string version in (string[])["8", "7", "6", "5"])
        {
            string candidate = Path.Combine(documents, "NetSarang Computer", version, "Xshell", "Sessions");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>检测该会话目录对应的 Xshell 是否启用主密码;无法判定时按未启用处理。</summary>
    private static bool IsMasterPasswordEnabled(string sessionsDirectory)
    {
        // Sessions 目录布局:&lt;UserData&gt;\Xshell\Sessions;主密码文件在 &lt;UserData&gt;\common\MasterPassword.mpw。
        try
        {
            DirectoryInfo? xshell = Directory.GetParent(sessionsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            DirectoryInfo? userData = xshell?.Parent;
            if (userData is null)
            {
                return false;
            }
            string mpw = Path.Combine(userData.FullName, "common", "MasterPassword.mpw");
            if (!File.Exists(mpw))
            {
                return false;
            }
            foreach (string raw in ReadLines(mpw))
            {
                string line = raw.Trim();
                if (line.StartsWith("EnblMasterPasswd=", StringComparison.OrdinalIgnoreCase))
                {
                    return line["EnblMasterPasswd=".Length..].Trim() == "1";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到就按未启用处理:大不了解密失败,会话仍以「不含密码」导入。
        }
        return false;
    }

    private static (string UserName, string Sid) GetCurrentUser() =>
        OperatingSystem.IsWindows() ? GetWindowsUser() : (string.Empty, string.Empty);

    [SupportedOSPlatform("windows")]
    private static (string UserName, string Sid) GetWindowsUser()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string fullName = identity.Name; // DOMAIN\user 或 machine\user
            int slash = fullName.LastIndexOf('\\');
            string name = slash >= 0 ? fullName[(slash + 1)..] : fullName;
            string sid = identity.User?.Value ?? string.Empty;
            return (name, sid);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (string.Empty, string.Empty);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadUserDataPathFromRegistry()
    {
        try
        {
            using RegistryKey? common = Registry.CurrentUser.OpenSubKey(@"Software\NetSarang\Common");
            if (common is null)
            {
                return null;
            }
            // 取版本号最高、且以 5/6/7/8 起始的子键。
            string? bestVersion = common.GetSubKeyNames()
                .Where(static v => v.StartsWith('5') || v.StartsWith('6') || v.StartsWith('7') || v.StartsWith('8'))
                .OrderByDescending(static v => v, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (bestVersion is null)
            {
                return null;
            }
            using RegistryKey? userData = common.OpenSubKey($@"{bestVersion}\UserData");
            return userData?.GetValue("UserDataPath") as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }
}

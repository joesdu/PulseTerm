using System.Runtime.Versioning;
using Microsoft.Win32;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Import;

/// <summary>
/// WinSCP 的 <see cref="ISessionImportService" /> 实现:从注册表
/// <c>HKCU\Software\Martin Prikryl\WinSCP 2\Sessions</c> 或 <c>WinSCP.ini</c> 读取会话,
/// 解码保存的密码(<see cref="WinScpCrypto" />),写入 <see cref="ISessionRepository" />。
/// </summary>
public sealed class WinScpImportService(ISessionRepository repository) : ISessionImportService
{
    private const string RegRoot = @"Software\Martin Prikryl\WinSCP 2";
    private const string SessionsSubKey = RegRoot + @"\Sessions";
    private const string SecuritySubKey = RegRoot + @"\Configuration\Security";
    private const string DefaultSessionName = "Default Settings";

    private readonly ISessionRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <inheritdoc />
    public string SourceKey => "WinSCP";

    /// <inheritdoc />
    public ImportBrowseKind BrowseKind => ImportBrowseKind.File;

    /// <inheritdoc />
    public string? DetectDefaultSource()
    {
        if (OperatingSystem.IsWindows() && RegistryHasSessions())
        {
            return @"HKCU\" + SessionsSubKey;
        }
        return FindIniFile();
    }

    /// <inheritdoc />
    public async Task<SessionImportScan> ScanAsync(string? source, CancellationToken cancellationToken = default)
    {
        // 显式指向一个存在的文件 → 按 INI 解析;否则自动(优先注册表,其次默认 INI)。
        string? autoIni = FindIniFile();
        (List<WinScpRawSession>? rawSessions, bool masterPassword, string? sourceLabel) =
            !string.IsNullOrWhiteSpace(source) && File.Exists(source)
                ? ReadFromIni(source)
                : OperatingSystem.IsWindows() && RegistryHasSessions()
                    ? ReadFromRegistry()
                    : autoIni is not null
                        ? ReadFromIni(autoIni)
                        : ([], false, source ?? string.Empty);

        List<SessionProfile> existing = await _repository.GetAllSessionsAsync().ConfigureAwait(false);
        var existingKeys = existing
            .Select(static s => DedupKey(s.Host, s.Port, s.Username))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<ImportedSession>();
        foreach (WinScpRawSession raw in rawSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw.Host))
            {
                continue;
            }
            (ConnectionType type, bool supported, string protocol) = MapProtocol(raw.FsProtocol);
            FtpSettings? ftpSettings = type == ConnectionType.FTP
                ? new FtpSettings { EncryptionMode = MapFtps(raw.Ftps) }
                : null;
            // WinSCP 的 S3 会话把端点存在 Host 里(默认 s3.amazonaws.com),
            // Access Key ID 在 UserName、Secret 在 Password —— 与 VelaShell 的字段约定一致,
            // 因此只要指名协议即可,凭据与端点都走通用字段。
            // 端点/区域等细节留给协议自己的默认值:导入器不该去猜某个插件的字段语义。
            string? pluginProtocol = type == ConnectionType.Plugin ? S3ProtocolId : null;
            bool hasEncrypted = !string.IsNullOrWhiteSpace(raw.Password);
            string? password = hasEncrypted && !masterPassword
                ? WinScpCrypto.Decrypt(raw.Host, raw.Username, raw.Password)
                : null;
            // 端口缺省值按协议给:FTP 是 21(隐式 FTPS 是 990)、S3 是 443,不是 SSH 的 22。
            int defaultPort = pluginProtocol is not null
                ? S3DefaultPort
                : ftpSettings is null
                    ? 22
                    : ftpSettings.EncryptionMode == FtpEncryptionMode.Implicit
                        ? Core.Models.FtpSettings.DefaultImplicitPort
                        : Core.Models.FtpSettings.DefaultPort;
            int port = raw.Port is int p and > 0 ? p : defaultPort;

            items.Add(new ImportedSession
            {
                Name = raw.Name,
                Host = raw.Host.Trim(),
                Port = port,
                Username = raw.Username.Trim(),
                ConnectionType = type,
                Protocol = protocol,
                IsSupported = supported,
                HasEncryptedPassword = hasEncrypted,
                Password = password,
                AlreadyExists = existingKeys.Contains(DedupKey(raw.Host, port, raw.Username)),
                Source = sourceLabel,
                FtpSettings = ftpSettings,
                PluginProtocolId = pluginProtocol
            });
        }

        return new SessionImportScan
        {
            Source = sourceLabel,
            Items = [.. items.OrderBy(static i => i.Name, StringComparer.OrdinalIgnoreCase)],
            MasterPasswordEnabled = masterPassword
        };
    }

    /// <inheritdoc />
    public async Task<SessionImportOutcome> ImportAsync(IReadOnlyList<ImportedSession> items, string groupName, CancellationToken cancellationToken = default) =>
        await SessionImportWriter.WriteAsync(_repository, items, groupName, "WinSCP", cancellationToken).ConfigureAwait(false);

    /// <summary>按优先级探测 <c>WinSCP.ini</c>:漫游目录 → 安装目录(卸载信息)→ 常见 Program Files。</summary>
    private static string? FindIniFile()
    {
        foreach (string candidate in IniCandidates())
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> IniCandidates()
    {
        // 漫游配置(WinSCP 选“INI 文件”存储时的默认位置)。
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSCP.ini");

        // 便携/自定义安装:INI 与 WinSCP.exe 同目录,安装目录取自卸载信息 InstallLocation。
        foreach (string dir in InstallLocations())
        {
            yield return Path.Combine(dir, "WinSCP.ini");
        }

        // 常见安装目录兜底。
        foreach (string env in (string[])["ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"])
        {
            string? root = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, "WinSCP", "WinSCP.ini");
            }
        }
    }

    private static IEnumerable<string> InstallLocations()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }
        foreach (string location in ReadInstallLocations())
        {
            yield return location;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ReadInstallLocations()
    {
        var result = new List<string>();
        // Inno Setup 卸载键(32/64 位视图)。
        (RegistryKey Root, string Path)[] keys =
        [
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\winscp3_is1"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\winscp3_is1"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\winscp3_is1")
        ];
        foreach ((RegistryKey root, string path) in keys)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(path);
                if (key?.GetValue("InstallLocation") is string location && location.Length > 0)
                {
                    result.Add(location);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // 忽略读不到的键。
            }
        }
        return result;
    }

    /// <summary>
    /// 官方 S3 插件的协议 id。导入器认识外部工具的格式,把 WinSCP 的 S3 会话映射到它;
    /// 插件没装时配置照常导入,只是要连的时候提示去装插件 —— 这比拒绝导入更符合用户预期。
    /// </summary>
    private const string S3ProtocolId = "velashell.s3";

    /// <summary>S3(HTTPS)的默认端口。这一个常量不值得为它去查插件描述,直接写在这里。</summary>
    private const int S3DefaultPort = 443;

    /// <summary>
    /// 把 WinSCP 的 FSProtocol 数值映射为 VelaShell 连接类型:SSH 系(SCP/SFTP)、FTP/FTPS
    /// 与 S3(经插件)受支持,WebDAV 仍不支持。
    /// </summary>
    private static (ConnectionType Type, bool Supported, string Protocol) MapProtocol(int? fsProtocol) =>
        fsProtocol switch
        {
            0 => (ConnectionType.SSH, true, "SCP"),
            1 or 2 or null => (ConnectionType.SSH, true, "SFTP"),
            5 => (ConnectionType.FTP, true, "FTP"),
            6 => (ConnectionType.SSH, false, "WebDAV"),
            7 => (ConnectionType.Plugin, true, "S3"),
            _ => (ConnectionType.SSH, false, "?")
        };

    /// <summary>
    /// WinSCP 的 <c>Ftps</c> 取值 → 加密方式:0/缺失=明文,1=隐式,2(SSL)/3(TLS)=显式。
    /// 缺失时用「不加密」而不是「自动」—— 用户在 WinSCP 里就是这么连的,导入不该悄悄改变行为。
    /// </summary>
    private static FtpEncryptionMode MapFtps(int? ftps) =>
        ftps switch
        {
            1 => FtpEncryptionMode.Implicit,
            2 or 3 => FtpEncryptionMode.Explicit,
            _ => FtpEncryptionMode.None
        };

    private static string DedupKey(string host, int port, string user) => $"{host.Trim()}|{port}|{user.Trim()}";

    // ---- 注册表来源 --------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static bool RegistryHasSessions()
    {
        try
        {
            using RegistryKey? sessions = Registry.CurrentUser.OpenSubKey(SessionsSubKey);
            return sessions is not null && sessions.GetSubKeyNames().Length > 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private static (List<WinScpRawSession> Sessions, bool MasterPassword, string Source) ReadFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ([], false, string.Empty);
        }
        return ReadFromRegistryWindows();
    }

    [SupportedOSPlatform("windows")]
    private static (List<WinScpRawSession> Sessions, bool MasterPassword, string Source) ReadFromRegistryWindows()
    {
        var result = new List<WinScpRawSession>();
        bool master = false;
        try
        {
            using (RegistryKey? security = Registry.CurrentUser.OpenSubKey(SecuritySubKey))
            {
                master = security?.GetValue("UseMasterPassword") is int i && i == 1;
            }
            using RegistryKey? sessions = Registry.CurrentUser.OpenSubKey(SessionsSubKey);
            if (sessions is not null)
            {
                foreach (string subName in sessions.GetSubKeyNames())
                {
                    string decoded = CleanName(Uri.UnescapeDataString(subName));
                    if (decoded.Equals(DefaultSessionName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    using RegistryKey? session = sessions.OpenSubKey(subName);
                    if (session?.GetValue("HostName") is not string host || host.Length == 0)
                    {
                        continue;
                    }
                    result.Add(new WinScpRawSession(
                        decoded,
                        host,
                        session.GetValue("UserName") as string ?? string.Empty,
                        session.GetValue("Password") as string,
                        session.GetValue("PortNumber") as int?,
                        session.GetValue("FSProtocol") as int?,
                        session.GetValue("Ftps") as int?));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // 读不全就返回已读到的部分。
        }
        return (result, master, @"HKCU\" + SessionsSubKey);
    }

    // ---- INI 来源 ----------------------------------------------------------

    private static (List<WinScpRawSession> Sessions, bool MasterPassword, string Source) ReadFromIni(string path)
    {
        var result = new List<WinScpRawSession>();
        bool master = false;
        try
        {
            string currentSection = string.Empty;
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? sessionName = null;

            void Flush()
            {
                if (sessionName is not null &&
                    current.TryGetValue("HostName", out string? host) && host.Length > 0 &&
                    !sessionName.Equals(DefaultSessionName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new WinScpRawSession(
                        sessionName,
                        host,
                        current.GetValueOrDefault("UserName", string.Empty),
                        current.GetValueOrDefault("Password"),
                        ParseInt(current.GetValueOrDefault("PortNumber")),
                        ParseInt(current.GetValueOrDefault("FSProtocol")),
                        ParseInt(current.GetValueOrDefault("Ftps"))));
                }
                current.Clear();
                sessionName = null;
            }

            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                if (line[0] == '[' && line[^1] == ']')
                {
                    Flush();
                    currentSection = line[1..^1];
                    if (currentSection.StartsWith("Sessions\\", StringComparison.OrdinalIgnoreCase))
                    {
                        sessionName = CleanName(Uri.UnescapeDataString(currentSection["Sessions\\".Length..]));
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }
                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();
                if (sessionName is not null)
                {
                    current[key] = value;
                }
                else if (currentSection.Equals(@"Configuration\Security", StringComparison.OrdinalIgnoreCase) &&
                         key.Equals("UseMasterPassword", StringComparison.OrdinalIgnoreCase))
                {
                    master = value.Trim() == "1";
                }
            }
            Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读失败返回已解析部分。
        }
        return (result, master, path);
    }

    private static int? ParseInt(string? value) => int.TryParse(value, out int i) ? i : null;

    /// <summary>去除会话名中的 BOM 与首尾空白(WinSCP.ini 偶有残留 BOM 混入名称)。</summary>
    private static string CleanName(string raw) => raw.Trim('﻿', ' ', '\t', '\r', '\n');

    private sealed record WinScpRawSession(string Name, string Host, string Username, string? Password, int? Port, int? FsProtocol, int? Ftps = null);
}

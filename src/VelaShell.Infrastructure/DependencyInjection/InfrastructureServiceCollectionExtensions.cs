using Microsoft.Extensions.DependencyInjection;
using Tmds.Ssh;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Recording;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Core.Sync;
using VelaShell.Core.Tunnels;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Infrastructure.Tunnels;
using VelaConnectionInfo = VelaShell.Core.Models.ConnectionInfo;

namespace VelaShell.Infrastructure.DependencyInjection;

/// <summary>
/// Provides extension methods for registering VelaShell infrastructure services in an <see cref="IServiceCollection" />.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    // ---- Persistence & Services (unchanged) ----
    /// <summary>
    /// Registers the VelaShell infrastructure services, including persistence, SSH connection management, SFTP, and related services, into the provided <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="VelaSshConnectionException"></exception>
    public static IServiceCollection AddVelaShellInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<VelaShellStoragePaths>();
        services.AddSingleton<SonnetDbEngine>(sp => new(sp.GetRequiredService<VelaShellStoragePaths>()));
        services.AddSingleton<ISecretProtector>(sp => new AesSecretProtector(sp.GetRequiredService<VelaShellStoragePaths>()));
        services.AddSingleton<ISessionRepository>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbSessionRepository(sp.GetRequiredService<SonnetDbEngine>(),
                sp.GetRequiredService<ISecretProtector>(), paths.SessionsFile);
        });
        services.AddSingleton<ISettingsService>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbSettingsService(sp.GetRequiredService<SonnetDbEngine>(),
                [paths.RootDirectory, paths.LegacyDotDirectory]);
        });
        services.AddSingleton<IHostKeyService>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbHostKeyService(sp.GetRequiredService<SonnetDbEngine>(),
                Path.Combine(paths.LegacyDotDirectory, "known_hosts.json"));
        });
        services.AddSingleton<IRecentConnectionService, SonnetDbRecentConnectionService>();
        services.AddSingleton<IAuditLogService, SonnetDbAuditLogService>();
        services.AddSingleton<IAppDataStore, SonnetDbAppDataStore>();
        services.AddSingleton<IQuickCommandRepository>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbQuickCommandRepository(sp.GetRequiredService<IAppDataStore>(),
                paths.LegacyQuickCommandsFile);
        });
        services.AddSingleton<ISessionRecordingStore, SonnetDbSessionRecordingStore>();
        services.AddSingleton<ISshKeyService>(_ => new SshKeyService());
        services.AddSingleton<ISecurityAlertService>(sp => new SecurityAlertService(
            sp.GetRequiredService<ISettingsService>(), sp.GetService<IAuditLogService>()));

        // SSH connection service
        services.AddSingleton<ISshConnectionService>(sp =>
        {
            IHostKeyService hostKey = sp.GetRequiredService<IHostKeyService>();
            ISettingsService settings = sp.GetRequiredService<ISettingsService>();
            IHostKeyPrompt? prompt = sp.GetService<IHostKeyPrompt>();
            ISecurityAlertService? alerts = sp.GetService<ISecurityAlertService>();
            return new SshConnectionService(ci =>
                CreateSshClientWrapper(ci, hostKey, settings, prompt, alerts));
        });

        // SFTP service
        services.AddSingleton<ISftpService>(sp =>
        {
            ISshConnectionService connSvc = sp.GetRequiredService<ISshConnectionService>();
            ISettingsService settings = sp.GetRequiredService<ISettingsService>();
            return new SftpService(connSvc, session =>
            {
                ISshClientWrapper? wrapper = connSvc.GetClient(session.SessionId);
                if (wrapper is not TmdsSshClientWrapper tmds)
                    throw new InvalidOperationException("SFTP requires Tmds.Ssh backend.");
                return new TmdsSftpClientWrapper(async () =>
                {
                    // SFTP 复用主连接的 SSH 通道。主连接不在时不得偷偷另建连接:
                    // 新连接无人持有、无人释放(泄漏),且会绕过用户可见的连接生命周期。
                    SshClient inner = tmds.InnerClient
                        ?? throw new VelaSshConnectionException(
                            "SSH connection is not established; cannot open SFTP channel.");
                    return await inner.OpenSftpClientAsync().ConfigureAwait(false);
                });
            }, settings);
        });
        services.AddSingleton<ITransferManager, TransferManager>();
        services.AddSingleton<IGistSyncService>(sp => new Sync.GistSyncService(
            sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<ISessionRepository>(),
            sp.GetRequiredService<IAppDataStore>(), sp.GetRequiredService<IQuickCommandRepository>(),
            sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<ISessionMetricsService>(sp =>
            new SessionMetricsService(sp.GetRequiredService<ISshConnectionService>()));
        services.AddSingleton<ITunnelService>(sp =>
        {
            ISshConnectionService connSvc = sp.GetRequiredService<ISshConnectionService>();
            return new TunnelService(connSvc, sid =>
                connSvc.GetClient(sid) ?? throw new InvalidOperationException($"No SSH client for session {sid}."));
        });
        return services;
    }

    // ---- Settings helpers ----

    private static TimeSpan ConnectTimeout(ISettingsService? s)
    {
        try { return TimeSpan.FromSeconds(Math.Clamp(s?.GetSettingsAsync().GetAwaiter().GetResult().General.ConnectTimeoutSeconds ?? 10, 1, 600)); }
        catch { return TimeSpan.FromSeconds(10); }
    }

    private static TimeSpan KeepAliveInterval(ISettingsService? s)
    {
        try
        {
            int sec = s?.GetSettingsAsync().GetAwaiter().GetResult().General.KeepAliveSeconds ?? 0;
            return sec > 0 ? TimeSpan.FromSeconds(sec) : TimeSpan.Zero;
        }
        catch { return TimeSpan.Zero; }
    }

    private static SecurityOptions GetSecurityOptions(ISettingsService? s)
    {
        try { return s?.GetSettingsAsync().GetAwaiter().GetResult().Security ?? new(); }
        catch { return new(); }
    }

    // ---- SSH client factory ----

    private static TmdsSshClientWrapper CreateSshClientWrapper(
        VelaConnectionInfo ci, IHostKeyService? hostKey, ISettingsService? settings,
        IHostKeyPrompt? prompt, ISecurityAlertService? alerts)
    {
        return new TmdsSshClientWrapper(BuildSshClientSettings(ci, hostKey, settings, prompt, alerts));
    }

    private static SshClientSettings BuildSshClientSettings(
        VelaConnectionInfo ci, IHostKeyService? hostKey, ISettingsService? settings,
        IHostKeyPrompt? prompt, ISecurityAlertService? alerts)
    {
        var s = new SshClientSettings($"{ci.Username}@{ci.Host}")
        {
            Port = ci.Port,
            ConnectTimeout = ConnectTimeout(settings),
            KeepAliveInterval = KeepAliveInterval(settings),

            // 连接必须只由 TmdsSshClientWrapper.ConnectAsync 显式发起。
            // Tmds.Ssh 的 AutoConnect 默认为 true:会话掉线后,任何一次后续操作
            // (SFTP 开通道、stat、列目录……)都会各自静默重连一次,失败就抛一发
            // ConnectFailedException。上传一个文件夹时这是"每个文件一次隐式重连"
            // ——调试输出被异常刷屏,用户却只看到操作莫名变慢/失败。
            // 关掉它:会话不在时立刻抛出明确错误,由上层的连接生命周期负责重连。
            AutoConnect = false,
        };
        AddCredential(s, ci);

        // ProxyJump
        if (ci.JumpHost is not null)
            s.Proxy = BuildProxyChain(ci.JumpHost, hostKey, settings, prompt, alerts);

        // Host key verification
        if (hostKey is not null)
            AddHostAuthentication(s, ci, hostKey, settings, prompt, alerts);

        return s;
    }

    internal static void AddCredential(SshClientSettings s, VelaConnectionInfo ci)
    {
        // 用用户配置的凭据【完全替换】Tmds.Ssh 的默认凭据列表,而非 Add 追加。
        // SshClientSettings 的默认 Credentials 本就非空,含 SshAgentCredentials 与 ~/.ssh 默认私钥:
        // 只 Add(且 `??= []` 因非空成为空操作)会保留它们,于是每次连接都先尝试 SSH Agent 认证。
        // Windows 上 Tmds 读 SSH_AUTH_SOCK 要求命名管道 \\.\pipe\...,而本机该变量常指向 msys/WSL/Git
        // 的 Unix 套接字路径,SshAgent.ConnectAsync 便抛 ArgumentException
        // ("SSH Agent path on Windows must be a named pipe ...")——被 Tmds 吞掉不影响连接,但每次连接
        // 稳定刷首发异常(用户在 VS 输出看到的正是这个)。VelaShell 没有"用 agent/默认私钥"这一 UI
        // 选项,静默回退本就非预期:只用用户显式选择的凭据,既除噪声又避免误用别的密钥。
        Credential credential = ci.AuthMethod switch
        {
            AuthMethod.Password => new PasswordCredential(ci.Password ?? ""),
            AuthMethod.PrivateKey => BuildPrivateKeyCredential(ci.PrivateKeyPath!, ci.PrivateKeyPassphrase),
            _ => throw new ArgumentOutOfRangeException(nameof(ci), ci.AuthMethod, "Unsupported authentication method.")
        };
        s.Credentials = [credential];
    }

    /// <summary>
    /// 构造私钥凭据,兼容传统 PEM 格式。Tmds.Ssh 只认 OpenSSH 私钥格式;用户导入的
    /// PKCS#1(-----BEGIN RSA PRIVATE KEY-----)、PKCS#8、加密 PKCS#8 会被判 Unsupported format
    /// 而【跳过】,认证以 "skipped: publickey" 失败。这里先用 BCL 读入并转成 OpenSSH 格式再交给 Tmds;
    /// 已是 OpenSSH 格式或无法读取/转换时,原样交回文件路径,让 Tmds 按其原生路径处理并给出错误。
    /// </summary>
    internal static Credential BuildPrivateKeyCredential(string path, string? passphrase)
    {
        try
        {
            string pem = File.ReadAllText(path);
            if (OpenSshPrivateKey.TryConvertToOpenSsh(pem, passphrase) is { } openSshKey)
            {
                // 转换后的 OpenSSH 私钥是未加密的(口令已在转换时用掉),故不再传口令。
                return new PrivateKeyCredential(openSshKey, (string?)null, path);
            }
        }
        catch
        {
            // 读文件/转换失败绝不阻断:退回原路径,由 Tmds 加载并报其原生错误。
        }
        return string.IsNullOrWhiteSpace(passphrase)
            ? new PrivateKeyCredential(path, null, null)
            : new PrivateKeyCredential(path, passphrase, null);
    }

    private static void AddHostAuthentication(
        SshClientSettings s, VelaConnectionInfo ci, IHostKeyService hostKey,
        ISettingsService? ss, IHostKeyPrompt? prompt, ISecurityAlertService? alerts)
    {
        SecurityOptions security = GetSecurityOptions(ss);
        s.HostAuthentication = async (ctx, ct) =>
        {
            string fingerprint = ctx.ConnectionInfo.ServerKey.Key.SHA256FingerPrint;
            string keyType = ctx.ConnectionInfo.ServerKey.Key is { } k ? k.GetType().Name : "unknown";
            string host = ci.Host;
            int port = ci.Port;

            HostKeyVerification verification = await hostKey
                .VerifyHostKeyAsync(host, port, keyType, fingerprint, ct).ConfigureAwait(false);
            string target = $"{host}:{port}";

            if (verification == HostKeyVerification.Trusted
                || HostTrustOnceCache.IsTrusted(host, port, fingerprint))
                return true;

            HostKeyDecision decision;
            if (verification == HostKeyVerification.Unknown)
            {
                decision = security.ConfirmFirstFingerprint && prompt is not null
                    ? await prompt.DecideAsync(host, port, keyType, fingerprint, verification)
                    : HostKeyDecision.TrustPermanently;
            }
            else
            {
                decision = !security.BlockOnFingerprintChange && prompt is not null
                    ? await prompt.DecideAsync(host, port, keyType, fingerprint, verification)
                    : HostKeyDecision.Reject;
            }

            if (decision == HostKeyDecision.Reject)
            {
                alerts?.RaiseAsync("hostkey-rejected",
                    Strings.Format("KeySvc_AlertFirstRejected", target, fingerprint));
                return false;
            }
            if (decision == HostKeyDecision.TrustPermanently)
            {
                await hostKey.TrustHostKeyAsync(host, port, keyType, fingerprint, ct);
                if (verification == HostKeyVerification.Changed && alerts is not null)
                    await alerts.RaiseAsync("hostkey-changed-accepted",
                        Strings.Format("KeySvc_AlertChangedAccepted", target, fingerprint));
            }
            else if (decision == HostKeyDecision.TrustOnce)
            {
                HostTrustOnceCache.Remember(host, port, fingerprint);
                if (alerts is not null)
                    await alerts.RaiseAsync("hostkey-trusted-once",
                        verification == HostKeyVerification.Changed
                            ? Strings.Format("KeySvc_AlertChangedTrustOnce", target, fingerprint)
                            : Strings.Format("KeySvc_AlertFirstTrustOnce", target, fingerprint));
            }
            return true;
        };
    }

    private static SshProxy BuildProxyChain(VelaConnectionInfo jumpHost,
        IHostKeyService? hostKey, ISettingsService? ss,
        IHostKeyPrompt? prompt, ISecurityAlertService? alerts)
    {
        var proxy = new SshClientSettings($"{jumpHost.Username}@{jumpHost.Host}")
        {
            Port = jumpHost.Port,
            ConnectTimeout = ConnectTimeout(ss),
        };
        AddCredential(proxy, jumpHost);

        if (jumpHost.JumpHost is not null)
            proxy.Proxy = BuildProxyChain(jumpHost.JumpHost, hostKey, ss, prompt, alerts);

        if (hostKey is not null)
        {
            string host = jumpHost.Host;
            int port = jumpHost.Port;
            proxy.HostAuthentication = async (ctx, ct) =>
            {
                string fp = ctx.ConnectionInfo.ServerKey.Key.SHA256FingerPrint;
                string kt = ctx.ConnectionInfo.ServerKey.Key is { } k ? k.GetType().Name : "unknown";
                HostKeyVerification v = await hostKey.VerifyHostKeyAsync(host, port, kt, fp, ct);
                return v == HostKeyVerification.Trusted
                    || HostTrustOnceCache.IsTrusted(host, port, fp);
            };
        }

        return new SshProxy(proxy);
    }
}

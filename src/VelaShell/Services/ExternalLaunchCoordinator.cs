using Avalonia.Controls;
using Avalonia.Layout;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Startup;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Services;

/// <summary>
/// 处理从进程外来的登录请求(Xshell 兼容登录):把 <see cref="ExternalLaunchRequest" /> 落成一次真正的连接。
/// <para>
/// 这条路径与用户在界面里点「连接」有一处本质区别:目标和凭据都是**别人给的**。
/// 所以它比界面路径多两道闸 —— 设置里的总开关,以及默认开启的确认弹窗;
/// 并且请求里带的一次性密码只活在这次连接里:配置一律 <see cref="SessionProfile.RememberPassword" /> = false,
/// 也从不写进会话仓储。
/// </para>
/// </summary>
public sealed class ExternalLaunchCoordinator(
    Window window,
    MainWindowViewModel viewModel,
    ISettingsService? settingsService,
    ISessionRepository? sessionRepository,
    IAuditLogService? auditLog = null)
{
    private readonly Window _window = window ?? throw new ArgumentNullException(nameof(window));
    private readonly MainWindowViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    /// <summary>处理一条请求。全程不抛:外部拉起失败最多是一句提示,绝不能掀翻应用。</summary>
    public async Task HandleAsync(ExternalLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            BringToFront();
            if (request.Kind != ExternalLaunchKind.Connect)
            {
                return;
            }

            AppSettings settings = settingsService is not null
                ? await settingsService.GetSettingsAsync().ConfigureAwait(true)
                : new AppSettings();
            if (!settings.Security.AllowExternalLaunch)
            {
                _viewModel.StatusBar.Status = Strings.Get("ExtLaunch_Blocked");
                return;
            }
            if (!request.IsSupported)
            {
                await MessageDialog.ShowMessageAsync(
                    _window,
                    Strings.Get("ExtLaunch_Title"),
                    Strings.Format("ExtLaunch_UnsupportedScheme", request.Scheme),
                    MessageDialogKind.Warning).ConfigureAwait(true);
                return;
            }
            if (!await ConfirmAsync(request, settings).ConfigureAwait(true))
            {
                return;
            }

            SessionProfile profile = await ResolveProfileAsync(request).ConfigureAwait(true);
            await AuditAsync(request).ConfigureAwait(true);
            _viewModel.StatusBar.Status = Strings.Format("ExtLaunch_Connecting", request.DisplayTarget);
            await _viewModel.TryConnectProfileAsync(profile).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] External launch failed: {ex}");
        }
    }

    /// <summary>
    /// 记一条审计:谁在什么时候被外部拉起去连了哪台。事后要查「这台机器上的会话是不是我开的」,
    /// 靠的就是这条 —— 尤其在关掉了确认弹窗的部署里,它是唯一的痕迹。
    /// </summary>
    /// <remarks>
    /// 刻意只写审计,不走 <c>ISecurityAlertService</c>:那条链路会按设置弹应用内告警/推 Webhook,
    /// 而用户刚刚在确认弹窗上点过「连接」,再弹一次纯属打扰。详情里同样不含密码。
    /// </remarks>
    private async Task AuditAsync(ExternalLaunchRequest request)
    {
        if (auditLog is null)
        {
            return;
        }
        try
        {
            await auditLog.WriteAsync(new AuditEntry
            {
                Category = "security",
                Action = "external-launch",
                Detail = request.ToString()
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // 审计写失败不拦连接:用户点了连接就该连上。
        }
    }

    /// <summary>把主窗口唤到前台:外部拉起的第一反应必须是「看得见」,哪怕后面还要确认。</summary>
    private void BringToFront()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }

    /// <summary>
    /// 确认闸门:设置里关了确认、或该目标已被用户信任,则直接放行;否则弹窗,
    /// 并在用户勾了「不再询问」时把目标(不含凭据)记进设置。
    /// </summary>
    private async Task<bool> ConfirmAsync(ExternalLaunchRequest request, AppSettings settings)
    {
        if (!settings.Security.ConfirmExternalLaunch
            || settings.Security.TrustedExternalLaunchTargets.Contains(request.TrustKey, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var trustCheckBox = new CheckBox { Content = Strings.Get("ExtLaunch_TrustTarget") };
        bool confirmed = await MessageDialog.ShowCustomAsync(
            _window,
            Strings.Get("ExtLaunch_Title"),
            BuildSummary(request, trustCheckBox),
            Strings.Get("ExtLaunch_Connect"),
            Strings.Cancel,
            showCancel: true,
            MessageDialogKind.Question).ConfigureAwait(true);
        if (!confirmed)
        {
            return false;
        }
        if (trustCheckBox.IsChecked == true && settingsService is not null)
        {
            settings.Security.TrustedExternalLaunchTargets.Add(request.TrustKey);
            try
            {
                await settingsService.SaveSettingsAsync(settings).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // 信任没记住不影响本次连接,下次再问一遍即可。
            }
        }
        return true;
    }

    /// <summary>
    /// 确认弹窗的内容:来源、目标、协议、是否随请求带了凭据。
    /// 刻意不显示密码本身 —— 用户需要判断的是「要不要连这台」,而不是核对一串一次性口令。
    /// </summary>
    private static StackPanel BuildSummary(ExternalLaunchRequest request, Control trustCheckBox)
    {
        var grid = new Grid
        {
            ColumnDefinitions = [with("Auto,*")],
            RowSpacing = 6,
            ColumnSpacing = 12
        };
        AddRow(grid, 0, Strings.Get("ExtLaunch_Origin"), OriginText(request.Origin), mono: false);
        AddRow(grid, 1, Strings.Get("ExtLaunch_Target"), request.DisplayTarget, mono: true);
        AddRow(grid, 2, Strings.Get("ExtLaunch_Protocol"), request.Scheme.ToUpperInvariant(), mono: false);
        AddRow(grid, 3, Strings.Get("ExtLaunch_Credential"),
               Strings.Get(request.Password is null && request.PrivateKeyPath is null
                               ? "ExtLaunch_CredentialNone"
                               : "ExtLaunch_CredentialSupplied"),
               mono: false);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(grid);
        panel.Children.Add(trustCheckBox);
        return panel;
    }

    private static void AddRow(Grid grid, int row, string label, string value, bool mono)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var caption = new TextBlock
        {
            Text = label,
            Classes = { "dim" },
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);
        var content = new TextBlock
        {
            Text = value,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (mono)
        {
            content.Classes.Add("mono-accent");
        }
        Grid.SetRow(content, row);
        Grid.SetColumn(content, 1);
        grid.Children.Add(caption);
        grid.Children.Add(content);
    }

    private static string OriginText(ExternalLaunchOrigin origin) =>
        origin switch
        {
            ExternalLaunchOrigin.UrlProtocol => Strings.Get("ExtLaunch_OriginUrlProtocol"),
            ExternalLaunchOrigin.SessionFile => Strings.Get("ExtLaunch_OriginSessionFile"),
            _ => Strings.Get("ExtLaunch_OriginCommandLine")
        };

    /// <summary>
    /// 请求 → 会话配置。请求没带凭据时优先复用已保存的同目标配置(那里有用户自己存的密码/密钥,
    /// 体验上等同于点侧边栏那一条);带了凭据的一律现搭一份临时配置,**不落盘**。
    /// </summary>
    private async Task<SessionProfile> ResolveProfileAsync(ExternalLaunchRequest request)
    {
        if (request.Password is null && request.PrivateKeyPath is null && sessionRepository is not null)
        {
            try
            {
                List<SessionProfile> saved = await sessionRepository.GetAllSessionsAsync().ConfigureAwait(true);
                SessionProfile? match = saved.FirstOrDefault(p =>
                    p.ConnectionType == request.ConnectionType
                    && string.Equals(p.Host, request.Host, StringComparison.OrdinalIgnoreCase)
                    && p.Port == request.Port
                    && (request.Username.Length == 0
                        || string.Equals(p.Username, request.Username, StringComparison.Ordinal)));
                if (match is not null)
                {
                    return match;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // 仓储读不到就现搭一份临时配置,不影响这次登录。
            }
        }

        return new SessionProfile
        {
            Name = request.DisplayTarget,
            ConnectionType = request.ConnectionType,
            Host = request.Host,
            Port = request.Port,
            Username = request.Username,
            AuthMethod = request.PrivateKeyPath is not null ? AuthMethod.PrivateKey : AuthMethod.Password,
            Password = request.Password,
            PrivateKeyPath = request.PrivateKeyPath,
            // 一次性凭据的红线:既不勾「记住密码」,也不写进仓储 —— 它只活到这次连接结束。
            RememberPassword = false,
            Ftp = request.ConnectionType == ConnectionType.FTP
                ? new FtpSettings
                {
                    EncryptionMode = request.Scheme == "ftps"
                        ? FtpEncryptionMode.Explicit
                        : FtpEncryptionMode.None
                }
                : null
        };
    }
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Services;

/// <summary>
/// 主机指纹人工确认(设置 → 安全审计):在 UI 线程弹既有的 HostKeyPromptView,
/// 返回用户三选项裁决(永久信任/仅本次信任/取消)。由 SSH 握手线程同步等待,
/// 拿不到主窗口或弹窗异常时一律按拒绝处理(fail-closed)。
/// </summary>
public sealed class HostKeyPromptDialogService : IHostKeyPrompt
{
    /// <summary>在 UI 线程弹出主机指纹确认对话框,返回用户裁决;无主窗口或发生异常时按拒绝处理(fail-closed)。</summary>
    public async Task<HostKeyDecision> DecideAsync(string host,
        int port,
        string keyType,
        string fingerprint,
        HostKeyVerification verification)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime
                    is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
                {
                    return HostKeyDecision.Reject;
                }
                var dialog = new HostKeyPromptView
                {
                    DataContext = new HostKeyPromptViewModel(host, port, keyType, fingerprint, verification)
                };
                HostKeyDecision? result = await dialog.ShowDialog<HostKeyDecision?>(owner);

                // 直接关窗(Esc/系统关闭)没有 Result → 拒绝。
                return result ?? HostKeyDecision.Reject;
            });
        }
        catch
        {
            // 安全决策失败时一律拒绝。
            return HostKeyDecision.Reject;
        }
    }
}

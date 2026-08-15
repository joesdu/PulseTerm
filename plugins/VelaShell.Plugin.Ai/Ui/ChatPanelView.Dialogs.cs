using Avalonia.Controls;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 设置与"配置工具"都开成独立窗口。
/// </summary>
/// <remarks>
/// 之前设置是挤在面板中间、和聊天流三选一显示的:一进设置就看不见对话,改完还要点回去。
/// 面板本身常常只有三成宽(侧栏),设置页那些字段在那个宽度里也铺不开。
/// 独立窗口两个问题一起解决,也和 VSCode 里 Copilot 的做法一致。
/// </remarks>
public partial class ChatPanelView
{
    private PluginDialog? _settingsDialog;
    private PluginDialog? _toolsDialog;

    /// <summary>打开设置窗口(已开着就带到前面,不重复开)。</summary>
    private void OpenSettingsDialog()
    {
        if (Activate(_settingsDialog))
        {
            return;
        }
        _settingsView ??= new SettingsView(_context, _store, _settings, _loc, OnProvidersChanged);
        // SettingsView 是长表单,自己带滚动;窗口给足宽度让那些两列/三列的行铺得开
        _settingsDialog = new PluginDialog(_loc["Settings"], _settingsView, _loc["Close"])
        {
            Width = 940,
            Height = 760
        };
        _settingsDialog.Closed += (_, _) =>
        {
            // 视图要留着复用(它持有编辑中的状态),只是从窗口上摘下来
            _settingsDialog!.DetachBody();
            _settingsDialog = null;
        };
        Show(_settingsDialog);
    }

    /// <summary>打开"配置工具"窗口。</summary>
    private void OpenToolsDialog()
    {
        if (Activate(_toolsDialog))
        {
            return;
        }
        var picker = new ToolPickerView(_context, _settings, _loc, PersistSettingsAsync);
        _toolsDialog = new PluginDialog(_loc["ConfigureTools"], picker, _loc["Ok"])
        {
            Width = 720,
            Height = 620
        };
        _toolsDialog.Closed += (_, _) => _toolsDialog = null;
        Show(_toolsDialog);
    }

    /// <summary>已经开着就带到前面并返回 true。</summary>
    private static bool Activate(PluginDialog? dialog)
    {
        if (dialog is null)
        {
            return false;
        }
        dialog.Activate();
        return true;
    }

    /// <summary>
    /// 挂到宿主主窗口上打开。用 <c>Show(owner)</c> 而不是 <c>ShowDialog</c> ——
    /// 模态会把整个 VelaShell 锁住,而改设置的时候人往往正想回去看一眼终端。
    /// </summary>
    private void Show(PluginDialog dialog)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            dialog.Show(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    /// <summary>面板关闭时把这两个窗口一并带走,别留在屏幕上。</summary>
    private void CloseDialogs()
    {
        _settingsDialog?.DetachBody();
        _settingsDialog?.Close();
        _settingsDialog = null;
        _toolsDialog?.Close();
        _toolsDialog = null;
    }
}

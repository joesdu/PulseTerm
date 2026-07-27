using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 侧边栏“最近连接”列表:数据来自 SonnetDB 连接历史(<see cref="IRecentConnectionService" />),
/// 按时间倒序、同一目标去重,最多展示 <see cref="MaxRecentConnections" /> 条。
/// </summary>
public sealed class RecentConnectionsViewModel : ReactiveObject
{
    private const int MaxRecentConnections = 10;

    private readonly IRecentConnectionService? _recentConnectionService;

    /// <summary>创建“最近连接”视图模型;未提供服务时列表保持为空且刷新为空操作。</summary>
    public RecentConnectionsViewModel(IRecentConnectionService? recentConnectionService = null)
    {
        _recentConnectionService = recentConnectionService;
        Connections = [];
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        ClearCommand = ReactiveCommand.CreateFromTask(ClearAllAsync);
    }

    /// <summary>当前展示的最近连接项集合,供侧边栏列表绑定。</summary>
    public ObservableCollection<RecentConnectionItemViewModel> Connections { get; }

    /// <summary>是否正在从历史存储加载列表,用于展示加载态。</summary>
    public bool IsLoading
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>快速连接头部 history 按钮:重新加载最近连接。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>清空最近连接历史,并同步清除当前列表。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearCommand { get; }

    /// <summary>从连接历史重新加载列表;存储故障时保留现有内容,不影响主流程。</summary>
    public async Task RefreshAsync()
    {
        if (_recentConnectionService is null)
        {
            return;
        }
        IsLoading = true;
        try
        {
            List<RecentConnectionEntry> entries = await _recentConnectionService.GetRecentAsync(MaxRecentConnections).ConfigureAwait(true);
            Connections.Clear();
            foreach (RecentConnectionEntry entry in entries)
            {
                Connections.Add(new(entry));
            }
        }
        catch
        {
            // 历史读取失败不影响侧边栏其余功能。
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ClearAllAsync()
    {
        if (_recentConnectionService is not null)
        {
            try
            {
                await _recentConnectionService.ClearAsync().ConfigureAwait(true);
            }
            catch
            {
                return;
            }
        }
        Connections.Clear();
    }
}

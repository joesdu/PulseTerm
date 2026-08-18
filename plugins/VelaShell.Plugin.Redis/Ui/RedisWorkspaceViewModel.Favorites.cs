using System.Collections.ObjectModel;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 收藏的键。
/// <para>
/// 运维的真实习惯是"每天就看那几个键" —— 一个限流计数器、一把分布式锁、一份配置缓存。
/// 让他每次都重新过一遍"打前缀 → 等扫描 → 展开三层"是纯粹的摩擦。
/// </para>
/// <para>
/// 按连接分开存:同一个键名在两台服务器上是两件事。
/// </para>
/// </summary>
public sealed partial class RedisWorkspaceViewModel
{
    /// <summary>收藏的键名(显示形式)。</summary>
    public ObservableCollection<string> Favorites { get; } = [];

    /// <summary>有收藏可选(界面据此显示那个下拉)。</summary>
    public bool HasFavorites => Favorites.Count > 0;

    /// <summary>
    /// 下拉里选中的收藏。选中即跳过去 —— 这个下拉的唯一用途就是跳转,
    /// 所以不需要额外一个"前往"按钮。
    /// </summary>
    public string? SelectedFavorite
    {
        get;
        set
        {
            SetProperty(ref field, value);
            if (field is { Length: > 0 } key)
            {
                _ = JumpToAsync(key);
            }
        }
    }

    /// <summary>当前选中的键是否已收藏。</summary>
    public bool IsSelectedFavorite =>
        Selected?.Key is { } key && Favorites.Contains(key.Display, StringComparer.Ordinal);

    /// <summary>收藏按钮的文案(★ / ☆ 由界面画,这里只给提示文本)。</summary>
    public string FavoriteLabel => IsSelectedFavorite ? Loc["Redis_Unfavorite"] : Loc["Redis_Favorite"];

    /// <summary>收藏 / 取消收藏当前键。</summary>
    public AsyncCommand ToggleFavoriteCommand { get; private set; } = null!;

    private void InitializeFavorites() =>
        ToggleFavoriteCommand = new(ToggleFavoriteAsync, () => HasSelection);

    private async Task ToggleFavoriteAsync()
    {
        if (Selected?.Key is not { } key || _store is null)
        {
            return;
        }
        string display = key.Display;
        if (Favorites.Contains(display, StringComparer.Ordinal))
        {
            Favorites.Remove(display);
        }
        else
        {
            Favorites.Add(display);
        }
        RaiseFavoriteState();
        await _store.SaveFavoritesAsync(_connectionKey, [.. Favorites]).ConfigureAwait(true);
    }

    /// <summary>
    /// 跳到一个键:把过滤条设成它的**完整名字**并重扫,然后选中那一片叶子。
    /// <para>
    /// 用过滤条而不是"直接在树上找":收藏的键可能压根不在当前已扫描到的那一批里
    /// (键空间几百万,树上只有五千)。走一次精确 <c>MATCH</c> 是唯一可靠的路。
    /// </para>
    /// </summary>
    private async Task JumpToAsync(string keyDisplay)
    {
        MatchMode = RedisMatchMode.Glob;
        // 收藏存的是转义后的显示形式;通配模式下它就是模式本身,
        // 而 \xNN 这类转义在 MATCH 里按字面量匹配 —— 与它被写下来时的形状一致。
        Filter = keyDisplay;
        TypeFilter = string.Empty;
        await ScanAsync(restart: true).ConfigureAwait(true);
        if (RevealKey(keyDisplay) is { } row)
        {
            SelectedRow = row;
            // 列表关了自动滚动,选中不等于看得见 —— 显式请视图滚过去。
            RaiseKeyRevealed(row);
        }
        else
        {
            // 键不在了(过期/被删):如实说,而不是留一个空白的详情区。
            StatusMessage = Loc["Redis_KeyGone"];
        }
    }

    private void RaiseFavoriteState()
    {
        RaisePropertyChanged(nameof(IsSelectedFavorite));
        RaisePropertyChanged(nameof(FavoriteLabel));
        RaisePropertyChanged(nameof(HasFavorites));
    }

    /// <summary>面板首次加载:读回收藏与控制台历史(后端不可用时静默降级)。</summary>
    private async Task RestorePersistedStateAsync()
    {
        if (_store is null)
        {
            return;
        }
        foreach (string key in await _store.LoadFavoritesAsync(_connectionKey).ConfigureAwait(true))
        {
            Favorites.Add(key);
        }
        RaiseFavoriteState();
        await Console.RestoreHistoryAsync().ConfigureAwait(true);
    }
}

using Avalonia.Controls;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>
/// 设置窗口的页面宿主:按 <see cref="SettingsSectionKey" /> 按需创建页面,并把创建过的缓存住。
/// </summary>
/// <remarks>
/// <para>
/// 原先 12 个页面全写在一个 <c>Panel</c> 里靠 <c>IsVisible</c> 切换:窗口一打开就把 12 棵
/// 控件树全建出来(外观页 9 个 <c>ItemsControl</c>、快捷键页 8 个、关于页 5 个),
/// 而绝大多数用户只会看其中一两页。改成按需创建后,首次打开只付一页的代价。
/// </para>
/// <para>
/// <b>为什么不是 <c>ContentControl</c> + <c>IDataTemplate</c>。</b>那条路看着更"MVVM",
/// 但 <c>ContentControl</c> 会把 <c>Content</c> 当作内容的 <c>DataContext</c> ——
/// 把分区标识放进 <c>Content</c> 之后,每个页面的绑定都会去
/// <see cref="SettingsSectionKey" /> 这个枚举上找属性,整窗设置项集体失灵
/// (实测报 <c>InvalidCastException: Unable to cast SettingsSectionKey to SettingsViewModel</c>)。
/// 普通子元素则照常沿视觉树继承窗口的 <c>DataContext</c>,各页绑定一个字都不用改。
/// </para>
/// <para>
/// <b>创建过的必须缓存。</b>页面上有滚动位置、展开的分组、填了一半的输入框 ——
/// 切走再切回来时这些都要在。每次现建等于把它们清零,比多占一点内存糟得多。
/// </para>
/// </remarks>
internal sealed class SettingsPageHost(Panel host)
{
    private readonly Dictionary<SettingsSectionKey, Control> _cache = [];

    /// <summary>把宿主面板的内容切到指定分区的页面(首次访问时创建)。</summary>
    /// <param name="key">目标分区。</param>
    public void Show(SettingsSectionKey key)
    {
        if (!_cache.TryGetValue(key, out Control? page))
        {
            page = Create(key);
            _cache[key] = page;
        }
        if (host.Children.Count == 1 && ReferenceEquals(host.Children[0], page))
        {
            return;
        }
        host.Children.Clear();
        host.Children.Add(page);
    }

    /// <summary>已经建出来的页面数(懒加载回归用例读它)。</summary>
    internal int CreatedPageCount => _cache.Count;

    private static Control Create(SettingsSectionKey key) => key switch
    {
        SettingsSectionKey.General => new GeneralSettingsPage(),
        SettingsSectionKey.Appearance => new AppearanceSettingsPage(),
        SettingsSectionKey.Terminal => new TerminalSettingsPage(),
        SettingsSectionKey.Keys => new KeyManagementPage(),
        SettingsSectionKey.Shortcuts => new ShortcutsPage(),
        SettingsSectionKey.Transfer => new TransferSettingsPage(),
        SettingsSectionKey.Security => new SecurityAuditPage(),
        SettingsSectionKey.Proxy => new ProxySettingsPage(),
        SettingsSectionKey.Snippets => new SnippetsPage(),
        SettingsSectionKey.Sync => new SyncPage(),
        SettingsSectionKey.About => new AboutPage(),
        SettingsSectionKey.Support => new DonatePage(),
        // 枚举里加了一页却忘了在这里登记:与其显示空白,不如退回常规页 ——
        // 至少窗口仍然可用。SettingsSectionKeyTests 会先一步在 CI 上拦住这种情况。
        _ => new GeneralSettingsPage()
    };
}

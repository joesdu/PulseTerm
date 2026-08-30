using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>消息列表里的一条:包装共享的 <see cref="NotificationItem" />,向界面暴露展示属性。</summary>
public class NotificationItemViewModel(NotificationItem item) : ReactiveObject
{
    private readonly NotificationItem _item = item ?? throw new ArgumentNullException(nameof(item));

    /// <summary>消息标识(去重与已读状态的键)。</summary>
    public string Id => _item.Id;

    /// <summary>标题。</summary>
    public string Title => _item.Title;

    /// <summary>正文;无正文时为空。</summary>
    public string? Body => _item.Body;

    /// <summary>是否有正文(控制正文行显隐)。</summary>
    public bool HasBody => !string.IsNullOrWhiteSpace(_item.Body);

    /// <summary>消息种类。</summary>
    public NotificationKind Kind => _item.Kind;

    /// <summary>是否已读。</summary>
    public bool IsRead
    {
        get => _item.IsRead;
        set
        {
            if (_item.IsRead == value)
            {
                return;
            }
            _item.IsRead = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsUnread));
        }
    }

    /// <summary>是否未读(未读行左侧有强调竖条、标题用主文本色)。</summary>
    public bool IsUnread => !_item.IsRead;

    /// <summary>种类徽标文字(资讯 / 更新 / 安全 / 推广)。</summary>
    public string KindBadge => Kind switch
    {
        NotificationKind.Update => Strings.Get("Notify_KindUpdate"),
        NotificationKind.Security => Strings.Get("Notify_KindSecurity"),
        NotificationKind.Promotion => Strings.Get("Notify_KindPromotion"),
        _ => Strings.Get("Notify_KindNews")
    };

    /// <summary>是否为需要留意的消息(警告/严重),用于把徽标换成警示色。</summary>
    public bool IsHighlighted => _item.Severity is NotificationSeverity.Warning or NotificationSeverity.Critical;

    /// <summary>这条消息有没有可去的地方。</summary>
    public bool HasLink => _item.Link is not null;

    /// <summary>动作文案;源里没给就按去处兜底(站内「查看」/ 站外「打开链接」)。</summary>
    public string LinkLabel =>
        _item.Link is not { } link
            ? string.Empty
            : !string.IsNullOrWhiteSpace(link.Label)
                ? link.Label
                : Strings.Get(link.CommandId is { Length: > 0 } ? "Notify_LinkOpen" : "Notify_LinkExternal");

    /// <summary>
    /// 外链的主机名,显示在动作旁边。远端源指过来的链接,**要让用户在点之前就看见去哪** ——
    /// 一个只显示「阅读全文」的按钮,点下去到哪只有投递方知道。
    /// </summary>
    public string? LinkHost =>
        _item.Link?.CommandId is { Length: > 0 }
            ? null
            : _item.Link?.Url is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                ? parsed.Host
                : null;

    /// <summary>是否显示外链主机名。</summary>
    public bool HasLinkHost => LinkHost is { Length: > 0 };

    /// <summary>底层的去处(由面板执行跳转)。</summary>
    public NotificationLink? Link => _item.Link;

    /// <summary>相对时间(刚刚 / 5 分钟前 / 3 小时前 / 2 天前)。</summary>
    public string RelativeTime => FormatRelative(DateTime.UtcNow - _item.PublishedAt);

    /// <summary>由面板的时钟调用,刷新相对时间。</summary>
    public void RefreshRelativeTime() => this.RaisePropertyChanged(nameof(RelativeTime));

    /// <summary>把时间差说成人话;未来时间(源端时钟偏了)一律当作「刚刚」。</summary>
    public static string FormatRelative(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return Strings.Get("Notify_TimeJustNow");
        }
        if (age < TimeSpan.FromHours(1))
        {
            return Strings.Format("Notify_TimeMinutes", (int)age.TotalMinutes);
        }
        if (age < TimeSpan.FromDays(1))
        {
            return Strings.Format("Notify_TimeHours", (int)age.TotalHours);
        }
        return age < TimeSpan.FromDays(30)
                   ? Strings.Format("Notify_TimeDays", (int)age.TotalDays)
                   : Strings.Format("Notify_TimeMonths", (int)(age.TotalDays / 30));
    }
}

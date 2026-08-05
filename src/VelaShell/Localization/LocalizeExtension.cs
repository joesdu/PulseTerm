using System.Collections.Concurrent;
using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using VelaShell.Core.Localization;

namespace VelaShell.Localization;

/// <summary>
/// 实时刷新的本地化字符串绑定:<c>Text="{loc:Localize QuickConnect}"</c>。
/// 与 <c>{x:Static Strings.X}</c>(加载时冻结)不同,任何用法在语言切换时都会即时刷新
/// (#4 —— 无需重启)。底层由 ILocalizationService 经 resx 提供。
/// 绑定目标是按键缓存的 <see cref="LocalizedText" /> 条目的普通属性 —— 不依赖
/// INPC 索引器变更通知(Avalonia 12 的绑定引擎对 "Item[]" 通知不响应,
/// 曾表现为"保存后既有界面不换语言、新开窗口才是新语言")。
/// </summary>
public class LocalizeExtension : MarkupExtension
{
    /// <summary>无参构造;通过 <see cref="Key" /> 属性设置本地化键。</summary>
    public LocalizeExtension() { }

    /// <summary>以本地化键构造扩展(支持位置参数写法 <c>{loc:Localize QuickConnect}</c>)。</summary>
    public LocalizeExtension(string key) => Key = key;

    /// <summary>要绑定的本地化资源键。</summary>
    public string Key { get; set; } = "";

    /// <summary>返回指向该键缓存条目 <see cref="LocalizedText.Value" /> 的单向绑定。</summary>
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocalizedText.Value))
        {
            Mode = BindingMode.OneWay,
            Source = LocalizedStrings.Instance.GetEntry(Key)
        };
}

/// <summary>单个本地化键的实时值:换语言时 <see cref="Value" /> 触发标准属性变更通知。</summary>
public sealed class LocalizedText(LocalizedStrings owner, string key) : INotifyPropertyChanged
{
    /// <summary>该键在当前语言下的实时值。</summary>
    public string Value => owner[key];

    /// <summary>标准属性变更通知;<see cref="Value" /> 变化时触发。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void NotifyChanged() => PropertyChanged?.Invoke(this, new(nameof(Value)));
}

/// <summary>
/// 本地化取词源:按键缓存 <see cref="LocalizedText" /> 条目,语言切换时逐条目
/// 发标准属性通知,既有绑定全部重取。保留可绑定索引器 + INPC 供兼容。
/// </summary>
public sealed class LocalizedStrings : INotifyPropertyChanged
{
    private readonly ConcurrentDictionary<string, LocalizedText> _entries = new();
    private ILocalizationService _service = new LocalizationService();

    /// <summary>全局共享的取词源单例。</summary>
    public static LocalizedStrings Instance { get; } = new();

    /// <summary>按键取当前语言下的字符串;供 XAML 索引器绑定使用。</summary>
    public string this[string key] => _service.GetString(key);

    /// <summary>兼容用索引器变更通知;语言切换时以 "Item[]" 触发。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>取(或建)键的绑定条目;同键共享同一条目,通知一次全量生效。</summary>
    public LocalizedText GetEntry(string key) => _entries.GetOrAdd(key, k => new LocalizedText(this, k));

    /// <summary>注入 DI 提供的服务并挂接实时刷新(启动时调用一次)。</summary>
    public void Attach(ILocalizationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        service.LanguageChanged += _ =>
        {
            foreach (LocalizedText entry in _entries.Values)
            {
                entry.NotifyChanged();
            }
            PropertyChanged?.Invoke(this, new("Item[]"));
        };
    }
}

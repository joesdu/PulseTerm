using System.Collections.ObjectModel;

namespace VelaShell.Plugin.S3.Ui;

/// <summary>一个表单字段的控件类型。</summary>
public enum S3FieldKind
{
    /// <summary>单行文本。</summary>
    Text,

    /// <summary>开关。</summary>
    Toggle,

    /// <summary>下拉选择。</summary>
    Choice,

    /// <summary>整数。</summary>
    Number,

    /// <summary>键值对列表(标签、元数据)。</summary>
    TagList,
}

/// <summary>桶标签 / 对象标签 / 自定义元数据里的一行。</summary>
public sealed class S3TagRowViewModel : ObservableObject
{
    /// <summary>键。</summary>
    public string Key
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>值。</summary>
    public string Value
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;
}

/// <summary>
/// 结构化表单里的一个字段。
/// <para>
/// 表单是**数据驱动**的:每项桶配置声明自己有哪些字段,界面用同一套 DataTemplate 渲染。
/// 这样新增一种配置不必写新的 XAML,只在 <see cref="S3ConfigForm" /> 里加一段映射 ——
/// 二十多种配置各写一份表单是这套功能做不完整的主要原因,这里从一开始就避开。
/// </para>
/// </summary>
public sealed class S3FormFieldViewModel : ObservableObject
{
    /// <summary>字段在 JSON 文档里的路径(点号分隔,支持 <c>a.b[0].c</c> 形式的数组下标)。</summary>
    public required string Path { get; init; }

    /// <summary>显示名称。</summary>
    public required string Label { get; init; }

    /// <summary>控件类型。</summary>
    public required S3FieldKind Kind { get; init; }

    /// <summary>补充说明,显示在字段下方。</summary>
    public string Hint { get; init; } = string.Empty;

    /// <summary><see cref="S3FieldKind.Choice" /> 的候选项。</summary>
    public ObservableCollection<string> Choices { get; init; } = [];

    /// <summary><see cref="S3FieldKind.TagList" /> 的行。</summary>
    public ObservableCollection<S3TagRowViewModel> Tags { get; } = [];

    /// <summary>文本 / 数字 / 下拉的当前值。</summary>
    public string Text
    {
        get;
        // 拒绝 null 回写。ComboBox 在 ItemsSource 里找不到当前值时会把 SelectedItem 推成 null,
        // 而模板里四种控件是平铺的(只靠 IsVisible 区分)—— 隐藏的下拉照样跑绑定,
        // 不挡住这一下,每个文本字段都会被那个空下拉清成 null,保存时再 NRE。
        set => SetProperty(ref field, value ?? string.Empty);
    } = string.Empty;

    /// <summary>
    /// 供下拉双向绑定的可空中转:空串对下拉是"没选中",而 <see cref="Text" /> 不可空。
    /// 直接把 SelectedItem 绑到 <see cref="Text" /> 会让两者的 null 语义打架。
    /// </summary>
    public string? Selected
    {
        get => Text.Length == 0 ? null : Text;
        set => Text = value ?? string.Empty;
    }

    /// <summary>开关的当前值。</summary>
    public bool Toggle
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>该字段是否是文本框(供 XAML 选模板)。</summary>
    public bool IsText => Kind is S3FieldKind.Text or S3FieldKind.Number;

    /// <summary>该字段是否是开关。</summary>
    public bool IsToggle => Kind == S3FieldKind.Toggle;

    /// <summary>该字段是否是下拉。</summary>
    public bool IsChoice => Kind == S3FieldKind.Choice;

    /// <summary>该字段是否是键值列表。</summary>
    public bool IsTagList => Kind == S3FieldKind.TagList;

    /// <summary>是否有补充说明。</summary>
    public bool HasHint => Hint.Length > 0;

    /// <summary>新增一行键值对。</summary>
    public void AddTag() => Tags.Add(new());

    /// <summary>删除一行键值对。</summary>
    public void RemoveTag(S3TagRowViewModel row) => Tags.Remove(row);
}

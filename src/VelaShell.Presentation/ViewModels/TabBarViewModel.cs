using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace VelaShell.Presentation.ViewModels;

/// <summary>标签栏视图模型:管理终端标签集合、当前活动标签以及新增、关闭、切换等交互命令。</summary>
public sealed class TabBarViewModel : ReactiveObject
{
    /// <summary>初始化标签栏视图模型,创建空标签集合并绑定各交互命令。</summary>
    public TabBarViewModel()
    {
        Tabs = [];
        CloseTabCommand = ReactiveCommand.Create<TabViewModel>(CloseTab);
        SelectTabCommand = ReactiveCommand.Create<TabViewModel>(tab => ActiveTab = tab);
        CloseActiveTabCommand = ReactiveCommand.Create(CloseActiveTab);
        NextTabCommand = ReactiveCommand.Create(NextTab);
        PreviousTabCommand = ReactiveCommand.Create(PreviousTab);
    }

    /// <summary>当前打开的标签集合,按显示顺序排列。</summary>
    public ObservableCollection<TabViewModel> Tabs { get; }

    /// <summary>当前活动(选中)的标签;赋值时会同步切换新旧标签的激活状态,无标签时为 <see langword="null" />。</summary>
    public TabViewModel? ActiveTab
    {
        get;
        set
        {
            field?.IsActive = false;
            this.RaiseAndSetIfChanged(ref field, value);
            field?.IsActive = true;
        }
    }

    /// <summary>关闭指定标签的命令;关闭活动标签时会自动选中相邻标签。</summary>
    public ReactiveCommand<TabViewModel, RxVoid> CloseTabCommand { get; }

    /// <summary>将指定标签设为活动标签的命令。</summary>
    public ReactiveCommand<TabViewModel, RxVoid> SelectTabCommand { get; }

    /// <summary>关闭当前活动标签的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseActiveTabCommand { get; }

    /// <summary>循环切换到下一个标签的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NextTabCommand { get; }

    /// <summary>循环切换到上一个标签的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> PreviousTabCommand { get; }

    /// <summary>向标签集合追加一个标签,并将其设为当前活动标签。</summary>
    /// <param name="tab">要添加的标签视图模型,不能为 <see langword="null" />。</param>
    public void AddTab(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    private void CloseTab(TabViewModel tab)
    {
        int index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }
        Tabs.RemoveAt(index);
        if (ActiveTab != tab)
        {
            return;
        }
        ActiveTab = Tabs.Count == 0
                        ? null
                        : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    private void CloseActiveTab()
    {
        if (ActiveTab is not null)
        {
            CloseTab(ActiveTab);
        }
    }

    private void NextTab()
    {
        if (Tabs.Count == 0 || ActiveTab is null)
        {
            return;
        }
        int index = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index + 1) % Tabs.Count];
    }

    private void PreviousTab()
    {
        if (Tabs.Count == 0 || ActiveTab is null)
        {
            return;
        }
        int index = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[((index - 1) + Tabs.Count) % Tabs.Count];
    }
}

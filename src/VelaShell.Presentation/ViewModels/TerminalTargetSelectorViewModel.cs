using System.Collections.ObjectModel;
using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace VelaShell.Presentation.ViewModels;

/// <summary>快捷命令和广播输入共享的已连接终端选择器。</summary>
public sealed class TerminalTargetSelectorViewModel : ReactiveObject
{
    /// <summary>创建共享终端目标选择器。</summary>
    public TerminalTargetSelectorViewModel()
    {
        SelectAllCommand = ReactiveCommand.Create(SelectAll);
        ClearSelectionCommand = ReactiveCommand.Create(ClearSelection);
    }

    /// <summary>当前所有已连接、可接收输入的终端。</summary>
    public ObservableCollection<QuickCommandTargetViewModel> Targets { get; } = [];

    /// <summary>未显式选择目标时使用的活动终端。</summary>
    public Guid? CurrentTargetId
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前是否存在可用终端。</summary>
    public bool HasTargets => Targets.Count > 0;

    /// <summary>显式选中的终端数量。</summary>
    public int SelectedTargetCount => Targets.Count(target => target.IsSelected);

    /// <summary>是否至少显式选中了一个终端。</summary>
    public bool HasSelectedTargets => SelectedTargetCount > 0;

    /// <summary>显式选择或活动终端回退后是否存在发送目标。</summary>
    public bool CanSend => ResolveTargetIds().Count > 0;

    /// <summary>选择全部已连接终端。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectAllCommand { get; }

    /// <summary>清除显式终端选择。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearSelectionCommand { get; }

    /// <summary>刷新终端快照,保留仍存在终端的选中状态。</summary>
    public void UpdateTargets(IEnumerable<(Guid Id, string DisplayName)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var selectedIds = Targets
            .Where(target => target.IsSelected)
            .Select(target => target.Id)
            .ToHashSet();
        foreach (QuickCommandTargetViewModel target in Targets)
        {
            target.PropertyChanged -= OnTargetPropertyChanged;
        }
        Targets.Clear();
        foreach ((Guid id, string displayName) in targets.DistinctBy(target => target.Id))
        {
            var target = new QuickCommandTargetViewModel(id, displayName)
            {
                IsSelected = selectedIds.Contains(id),
            };
            target.PropertyChanged += OnTargetPropertyChanged;
            Targets.Add(target);
        }
        if (CurrentTargetId is { } currentId && Targets.All(target => target.Id != currentId))
        {
            CurrentTargetId = null;
        }
        RaiseStateChanged();
    }

    /// <summary>更新活动终端。</summary>
    public void SetCurrentTarget(Guid? targetId)
    {
        CurrentTargetId = targetId is { } id && Targets.Any(target => target.Id == id) ? id : null;
        RaiseStateChanged();
    }

    /// <summary>解析显式选择或活动终端回退后的目标标识。</summary>
    public IReadOnlyList<Guid> ResolveTargetIds()
    {
        Guid[] selected = [.. Targets.Where(target => target.IsSelected).Select(target => target.Id)];
        if (selected.Length > 0)
        {
            return selected;
        }
        return CurrentTargetId is { } currentId && Targets.Any(target => target.Id == currentId)
            ? [currentId]
            : [];
    }

    private void SelectAll()
    {
        foreach (QuickCommandTargetViewModel target in Targets)
        {
            target.IsSelected = true;
        }
        RaiseStateChanged();
    }

    private void ClearSelection()
    {
        foreach (QuickCommandTargetViewModel target in Targets)
        {
            target.IsSelected = false;
        }
        RaiseStateChanged();
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickCommandTargetViewModel.IsSelected))
        {
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasTargets));
        this.RaisePropertyChanged(nameof(SelectedTargetCount));
        this.RaisePropertyChanged(nameof(HasSelectedTargets));
        this.RaisePropertyChanged(nameof(CanSend));
    }
}

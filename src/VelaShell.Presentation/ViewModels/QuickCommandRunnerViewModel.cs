using System.Collections.ObjectModel;
using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace VelaShell.Presentation.ViewModels;

/// <summary>快捷命令运行区域,复用共享终端选择器并产生执行请求。</summary>
public sealed class QuickCommandRunnerViewModel : ReactiveObject
{
    /// <summary>创建快捷命令运行器并可选复用共享目标选择器。</summary>
    public QuickCommandRunnerViewModel(
        QuickCommandsViewModel library,
        TerminalTargetSelectorViewModel? targetSelector = null
    )
    {
        Library = library ?? throw new ArgumentNullException(nameof(library));
        TargetSelector = targetSelector ?? new();
        TargetSelector.PropertyChanged += OnTargetSelectorPropertyChanged;
        SendCommand = ReactiveCommand.Create<QuickCommandViewModel>(
            Send,
            this.WhenAnyValue(viewModel => viewModel.CanRun)
        );
    }

    /// <summary>可搜索、分组的快捷命令库。</summary>
    public QuickCommandsViewModel Library { get; }

    /// <summary>快捷命令使用的共享终端目标选择器。</summary>
    public TerminalTargetSelectorViewModel TargetSelector { get; }

    /// <summary>当前可用终端目标。</summary>
    public ObservableCollection<QuickCommandTargetViewModel> Targets => TargetSelector.Targets;

    /// <summary>未显式选择时使用的活动终端标识。</summary>
    public Guid? CurrentTargetId => TargetSelector.CurrentTargetId;

    /// <summary>是否存在已连接终端。</summary>
    public bool HasTargets => TargetSelector.HasTargets;

    /// <summary>显式选择的终端数量。</summary>
    public int SelectedTargetCount => TargetSelector.SelectedTargetCount;

    /// <summary>是否存在显式选择的终端。</summary>
    public bool HasSelectedTargets => TargetSelector.HasSelectedTargets;

    /// <summary>当前是否可以执行快捷命令。</summary>
    public bool CanRun => TargetSelector.CanSend;

    /// <summary>把命令文本发送到解析出的目标终端(不附加回车,由用户补全后自行执行)。</summary>
    public ReactiveCommand<QuickCommandViewModel, RxVoid> SendCommand { get; }

    /// <summary>选择全部目标终端。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectAllCommand => TargetSelector.SelectAllCommand;

    /// <summary>清除显式目标选择。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearSelectionCommand =>
        TargetSelector.ClearSelectionCommand;

    /// <summary>命令及目标解析完成后发出的执行请求。</summary>
    public event EventHandler<QuickCommandExecutionRequest>? ExecutionRequested;

    /// <summary>刷新可用终端目标。</summary>
    public void UpdateTargets(IEnumerable<(Guid Id, string DisplayName)> targets) =>
        TargetSelector.UpdateTargets(targets);

    /// <summary>更新活动终端回退目标。</summary>
    public void SetCurrentTarget(Guid? targetId) => TargetSelector.SetCurrentTarget(targetId);

    private void Send(QuickCommandViewModel command)
    {
        IReadOnlyList<Guid> targetIds = TargetSelector.ResolveTargetIds();
        if (targetIds.Count == 0 || string.IsNullOrWhiteSpace(command.CommandText))
        {
            return;
        }
        ExecutionRequested?.Invoke(this, new(command.CommandText, targetIds));
    }

    private void OnTargetSelectorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        this.RaisePropertyChanged(e.PropertyName);
        if (e.PropertyName == nameof(TerminalTargetSelectorViewModel.CanSend))
        {
            this.RaisePropertyChanged(nameof(CanRun));
        }
    }
}

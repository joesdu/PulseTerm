using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Presentation.Services;

namespace VelaShell.Features.Sessions;

/// <summary>诊断步骤行(设计 RGXg1 stepPanel):序号 + 名称 + 状态图形/耗时。</summary>
/// <param name="index">步骤在诊断流程中的序号(从 0 开始)。</param>
/// <param name="name">步骤显示名称,如"DNS 解析"。</param>
public sealed class DiagnosticStepItemViewModel(int index, string name) : ReactiveObject
{
    private int Index { get; } = index;

    private string Name { get; set; } = name;

    /// <summary>列表中显示的带序号步骤名,如"1. DNS 解析"。</summary>
    public string DisplayName => $"{Index + 1}. {Name}";

    /// <summary>当前步骤的检测状态(待检/检测中/成功/警告/失败/跳过)。</summary>
    public DiagnosticStepStatus Status
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = DiagnosticStepStatus.Pending;

    /// <summary>该步骤的补充说明或失败原因,可为空。</summary>
    public string? Detail
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>该步骤耗时(毫秒),未完成时为空。</summary>
    public long? ElapsedMs
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>状态列文本:✅ 4ms / ⚠ 原因 / ✗ 原因 / ⏸ 等待修复后重试(设计 RGXg1)。</summary>
    public string StatusText => Status switch
    {
        DiagnosticStepStatus.Running => "… 检测中",
        DiagnosticStepStatus.Success => ElapsedMs is { } ms ? $"✓  {ms}ms" : "✓",
        DiagnosticStepStatus.Warning => "⚠",
        DiagnosticStepStatus.Failed => "✗",
        DiagnosticStepStatus.Skipped => "⏸",
        _ => "—"
    };

    /// <summary>该步骤是否检测成功,供视图着色。</summary>
    public bool IsSuccess => Status == DiagnosticStepStatus.Success;

    /// <summary>该步骤是否处于警告状态,供视图着色。</summary>
    public bool IsWarning => Status == DiagnosticStepStatus.Warning;

    /// <summary>该步骤是否检测失败,供视图着色。</summary>
    public bool IsFailed => Status == DiagnosticStepStatus.Failed;

    /// <summary>该步骤是否为待检或跳过状态,供视图弱化显示。</summary>
    public bool IsMuted => Status is DiagnosticStepStatus.Pending or DiagnosticStepStatus.Skipped;

    /// <summary>该步骤是否正在检测中,供视图显示进行态。</summary>
    public bool IsRunning => Status == DiagnosticStepStatus.Running;

    /// <summary>该步骤是否存在补充说明文本。</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>应用一次步骤更新,刷新状态、说明、耗时并通知相关派生属性。</summary>
    /// <param name="update">来自诊断服务的步骤更新数据。</param>
    public void Apply(DiagnosticStepUpdate update)
    {
        Name = update.Name;
        Status = update.Status;
        Detail = update.Detail;
        ElapsedMs = update.ElapsedMs;
        this.RaisePropertyChanged(nameof(DisplayName));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(IsSuccess));
        this.RaisePropertyChanged(nameof(IsWarning));
        this.RaisePropertyChanged(nameof(IsFailed));
        this.RaisePropertyChanged(nameof(IsMuted));
        this.RaisePropertyChanged(nameof(IsRunning));
        this.RaisePropertyChanged(nameof(HasDetail));
    }

    /// <summary>将步骤重置为待检状态,用于重新开始诊断。</summary>
    public void Reset() => Apply(new(Index, Name, DiagnosticStepStatus.Pending));
}

/// <summary>连接诊断中心(设计 RGXg1):对一条连接配置逐步检测并给出问题与修复建议。</summary>
public class ConnectionDiagnosticsViewModel : ReactiveObject
{
    private readonly IConnectionDiagnosticsService _diagnosticsService;
    private readonly SessionProfile _profile;
    private DiagnosticReport? _lastReport;

    /// <summary>创建连接诊断视图模型,绑定目标连接配置与诊断服务并初始化四个检测步骤。</summary>
    /// <param name="profile">要诊断的连接配置。</param>
    /// <param name="diagnosticsService">执行逐步诊断的服务。</param>
    public ConnectionDiagnosticsViewModel(SessionProfile profile, IConnectionDiagnosticsService diagnosticsService)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _diagnosticsService = diagnosticsService ?? throw new ArgumentNullException(nameof(diagnosticsService));
        Steps =
        [
            new(0, "DNS 解析"),
            new(1, "TCP 建链"),
            new(2, "SSH 握手"),
            new(3, "用户认证")
        ];
        Suggestions = [];
        IObservable<bool> canRun = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        RunCommand = ReactiveCommand.CreateFromTask(RunAsync, canRun);
    }

    /// <summary>标题栏副标题里的目标描述。</summary>
    public string TargetSummary => $"// 逐步分析 DNS、握手、认证与通道建立 — {(_profile.Name is { Length: > 0 } n ? n : _profile.Host)} ({_profile.Username}@{_profile.Host}:{_profile.Port})";

    /// <summary>诊断步骤集合(DNS、TCP、SSH、认证),供步骤面板绑定。</summary>
    public ObservableCollection<DiagnosticStepItemViewModel> Steps { get; }

    /// <summary>诊断得出的修复建议文本集合。</summary>
    public ObservableCollection<string> Suggestions { get; }

    /// <summary>启动一次连接诊断的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RunCommand { get; }

    /// <summary>是否正在执行诊断,用于禁用重复触发并显示进度。</summary>
    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>诊断发现的问题标题,无问题时为空。</summary>
    public string? IssueTitle
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasIssue));
        }
    }

    /// <summary>诊断发现问题的详细描述,无问题时为空。</summary>
    public string? IssueDescription
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否存在已发现的问题,用于问题面板显隐。</summary>
    public bool HasIssue => !string.IsNullOrEmpty(IssueTitle);

    /// <summary>四步全部通过时问题面板显示绿色的"未发现问题"。</summary>
    public bool AllPassed
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否已有可导出的诊断报告,用于导出按钮可用性。</summary>
    public bool CanExport => _lastReport is not null;

    /// <summary>导出文件名建议。</summary>
    public string SuggestedReportFileName => $"诊断报告-{(_profile.Name is { Length: > 0 } n ? n : _profile.Host)}-{DateTime.Now:yyyyMMdd-HHmmss}.txt";

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        IssueTitle = null;
        IssueDescription = null;
        AllPassed = false;
        Suggestions.Clear();
        foreach (DiagnosticStepItemViewModel step in Steps)
        {
            step.Reset();
        }
        try
        {
            var progress = new Progress<DiagnosticStepUpdate>(update =>
            {
                // Progress<T> 回调已被封送回创建线程(UI);保险起见仍走 Dispatcher。
                if (Dispatcher.UIThread.CheckAccess())
                {
                    Steps[update.Index].Apply(update);
                }
                else
                {
                    Dispatcher.UIThread.Post(() => Steps[update.Index].Apply(update));
                }
            });
            DiagnosticReport report = await _diagnosticsService.DiagnoseAsync(_profile, progress, cancellationToken);
            _lastReport = report;
            IssueTitle = report.IssueTitle;
            IssueDescription = report.IssueDescription;
            foreach (string suggestion in report.Suggestions)
            {
                Suggestions.Add(suggestion);
            }
            AllPassed = report.Success && Suggestions.Count == 0;
            this.RaisePropertyChanged(nameof(CanExport));
        }
        catch (Exception ex)
        {
            IssueTitle = "诊断执行出错";
            IssueDescription = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>生成可保存的纯文本诊断报告(导出诊断报告按钮)。</summary>
    public string BuildReportText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("VelaShell 连接诊断报告");
        builder.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"目标: {_profile.Name} ({_profile.Username}@{_profile.Host}:{_profile.Port})");
        if (_profile.JumpHostProfileId is not null)
        {
            builder.AppendLine("链路: 经由跳板连接");
        }
        builder.AppendLine(new('-', 48));
        foreach (DiagnosticStepItemViewModel step in Steps)
        {
            string status = step.Status switch
            {
                DiagnosticStepStatus.Success => "通过",
                DiagnosticStepStatus.Warning => "警告",
                DiagnosticStepStatus.Failed => "失败",
                DiagnosticStepStatus.Skipped => "跳过",
                _ => "未执行"
            };
            builder.Append($"{step.DisplayName,-24} [{status}]");
            if (step.ElapsedMs is { } ms)
            {
                builder.Append($" {ms}ms");
            }
            builder.AppendLine();
            if (!string.IsNullOrEmpty(step.Detail))
            {
                builder.AppendLine($"    {step.Detail}");
            }
        }
        builder.AppendLine(new('-', 48));
        if (HasIssue)
        {
            builder.AppendLine($"发现问题: {IssueTitle}");
            if (!string.IsNullOrEmpty(IssueDescription))
            {
                builder.AppendLine(IssueDescription);
            }
        }
        else
        {
            builder.AppendLine("未发现问题,各项检测均通过。");
        }
        if (Suggestions.Count > 0)
        {
            builder.AppendLine();
            for (int i = 0; i < Suggestions.Count; i++)
            {
                builder.AppendLine($"建议 {i + 1}: {Suggestions[i]}");
            }
        }
        return builder.ToString();
    }
}

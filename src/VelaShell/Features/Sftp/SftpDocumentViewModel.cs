using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Presentation.Services;

namespace VelaShell.Features.Sftp;

/// <summary>持有一个独立 SFTP 文档的全部状态与生命周期资源。</summary>
public sealed class SftpDocumentViewModel : ReactiveObject, IAsyncDisposable
{
    private readonly IConnectionWorkflowService _workflow;
    private readonly SerializedSftpService _serializedSftp;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _closeTask;
    private readonly Lock _closeSync = new();

    /// <summary>初始化 SFTP 文档视图模型,并开始加载本地/远程文件树。</summary>
    /// <param name="profile">用于建立连接的会话配置。</param>
    /// <param name="session">已建立的 SSH 会话。</param>
    /// <param name="workflow">连接工作流服务,关闭文档时用它断开会话。</param>
    /// <param name="sftpService">底层 SFTP 服务,构造时会被包成本文档独占的串行化视图。</param>
    /// <param name="transferOptions">设置 → 文件传输 的选项快照。</param>
    /// <param name="transferSink">承载本文档所发起传输的浮动传输组件;为 null 时不上报进度。</param>
    /// <param name="getDefaultEditorPath">
    /// 解析「设置 → 文件传输 → 默认编辑器」的回调。独立 SFTP 标签与终端侧边栏的文件浏览器
    /// 是两个各自 new 出来的 <see cref="FileBrowserViewModel" />,宿主回调不会自动继承——
    /// 漏传这一个会让右键「使用默认编辑器打开」误报“未配置”(明明已配置)。
    /// </param>
    public SftpDocumentViewModel(
        SessionProfile profile,
        SshSession session,
        IConnectionWorkflowService workflow,
        ISftpService sftpService,
        TransferOptions transferOptions,
        FileTransferViewModel? transferSink = null,
        Func<Task<string?>>? getDefaultEditorPath = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _serializedSftp = new SerializedSftpService(sftpService ?? throw new ArgumentNullException(nameof(sftpService)), session.SessionId);
        SessionId = session.SessionId;
        Title = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
        RemoteFiles = new FileBrowserViewModel(_serializedSftp, SessionId)
        {
            ServerDisplayName = Title,
            TransferSink = transferSink,
            TransferOptions = transferOptions ?? throw new ArgumentNullException(nameof(transferOptions)),
            IsVisible = true,
            IsDragEnabled = true,
            GetDefaultEditorPath = getDefaultEditorPath,
        };
        LocalFiles = new LocalFilePaneViewModel(transferOptions)
        {
            UploadSelectedAsync = () => UploadSelectedAsync()
        };
        RemoteFiles.PickFolderForDownload = () => Task.FromResult<string?>(LocalFiles.CurrentPath);
        UploadSelectedCommand = ReactiveCommand.CreateFromTask(UploadSelectedAsync);
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask(DownloadSelectedAsync);
        DeleteLocalSelectedCommand = LocalFiles.DeleteSelectedCommand;
        LocalFiles.ConfirmDelete = message => ConfirmLocalDelete?.Invoke(message) ?? Task.FromResult(false);
        InitialLoadTask = LoadAsync();
    }

    /// <summary>用于建立连接的会话配置。</summary>
    public SessionProfile Profile { get; }
    /// <summary>当前活跃的 SSH 会话。</summary>
    public SshSession Session { get; }
    /// <summary>SSH 会话的唯一标识。</summary>
    public Guid SessionId { get; }
    /// <summary>SFTP 文档标签页的显示标题。</summary>
    public string Title { get; }
    /// <summary>本地文件浏览器面板。</summary>
    public LocalFilePaneViewModel LocalFiles { get; }
    /// <summary>远程文件浏览器面板。</summary>
    public FileBrowserViewModel RemoteFiles { get; }
    /// <summary>将选中的本地文件上传到远程服务器的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> UploadSelectedCommand { get; }
    /// <summary>将选中的远程文件下载到本机的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DownloadSelectedCommand { get; }
    /// <summary>删除选中的本地文件的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteLocalSelectedCommand { get; }
    /// <summary>确认本地文件删除的回调。</summary>
    public Func<string, Task<bool>>? ConfirmLocalDelete { get; set; }

    internal Task InitialLoadTask { get; }

    /// <summary>将选中的本地文件上传到远程服务器。</summary>
    public async Task UploadSelectedAsync(CancellationToken cancellationToken = default)
    {
        string[] paths = [.. LocalFiles.SelectedEntries
            .Where(entry => !entry.IsParentEntry)
            .Select(entry => entry.FullPath)];
        await RemoteFiles.UploadLocalPathsAsync(paths, cancellationToken);
    }

    /// <summary>将选中的远程文件下载到本机。</summary>
    public async Task DownloadSelectedAsync(CancellationToken cancellationToken = default)
    {
        RemoteFileInfoViewModel[] entries = [.. RemoteFiles.SelectedFiles.Where(entry => !entry.IsParentEntry)];
        if (entries.Length == 0 || string.IsNullOrWhiteSpace(LocalFiles.CurrentPath))
        {
            return;
        }
        await RemoteFiles.DownloadRemoteEntriesAsync(entries, LocalFiles.CurrentPath, cancellationToken);
        if (RemoteFiles.ErrorMessage is null)
        {
            await LocalFiles.RefreshAsync(cancellationToken);
        }
    }

    /// <summary>分离文件浏览器并取消生命周期令牌。</summary>
    public void Detach()
    {
        _lifetime.Cancel();
        RemoteFiles.Detach();
        LocalFiles.Detach();
    }

    /// <summary>关闭 SFTP 会话并清理资源,确保仅执行一次。</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_closeSync)
        {
            _closeTask ??= CloseCoreAsync();
            return cancellationToken.CanBeCanceled
                ? _closeTask.WaitAsync(cancellationToken)
                : _closeTask;
        }
    }

    /// <summary>异步释放 SFTP 文档视图模型。</summary>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task LoadAsync()
    {
        await LocalFiles.LoadInitialAsync(_lifetime.Token);
        await RemoteFiles.LoadInitialAsync(_lifetime.Token);
    }

    private async Task CloseCoreAsync()
    {
        Detach();
        try
        {
            await _serializedSftp.CloseAsync().ConfigureAwait(false);

            // 等初始加载收尾,但**不能**把它的取消当成关闭失败上报:上面的 Detach() 刚刚
            // 取消了 _lifetime,若初始加载还在飞(开标签后立刻关就会这样),它必然以
            // OperationCanceledException 收场 —— 那是本方法自己造成的,不是错误。
            // 其它异常照常抛出。
            //
            // 实测栈(2026-07-22,SftpDocumentClose_WhenCallerWaitIsCancelled_... 偶发红):
            //   LocalFilePaneViewModel.RefreshRootsAsync → LoadInitialAsync → LoadAsync
            //   → CloseCoreAsync 原样抛出 TaskCanceledException。
            // 注意本地栏与远程栏在这点上不一致:远程栏内部吞掉了自身的取消,本地栏没有,
            // 所以只有本地栏还在飞时才会炸 —— 这也是它只在混跑时偶发的原因。
            // 无对应单测:要稳定复现须把 ILocalRootProvider 从 SftpDocumentViewModel 一路穿透
            // 注入进 LocalFilePaneViewModel(目前只有后者的双参构造有这个注入点),
            // 属于为可测性改生产 API,暂未做。
            try
            {
                await InitialLoadTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
        finally
        {
            await _workflow.DisconnectAsync(SessionId).ConfigureAwait(false);
        }
    }
}

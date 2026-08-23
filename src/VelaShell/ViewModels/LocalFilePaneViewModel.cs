using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>SFTP 双栏文档本地侧的领域视图模型。</summary>
public sealed class LocalFilePaneViewModel : ReactiveObject
{
    private readonly TransferOptions _transferOptions;
    private readonly ILocalFileSystem _fileSystem;
    private readonly ILocalRootProvider _rootProvider;
    private readonly BatchObservableCollection<LocalFileEntry> _entries = [];
    private readonly ObservableCollection<LocalRootEntry> _roots = [];
    private readonly CancellationTokenSource _lifetime = new();
    private long _navigationVersion;
    private bool _isSwitchingRoot;

    /// <summary>使用配置中的传输下载目录创建一个本地面板。</summary>
    public LocalFilePaneViewModel(TransferOptions transferOptions)
        : this(transferOptions, null) { }

    internal LocalFilePaneViewModel(
        TransferOptions transferOptions,
        ILocalFileSystem? fileSystem = null,
        ILocalRootProvider? rootProvider = null
    )
    {
        _transferOptions = transferOptions ?? throw new ArgumentNullException(nameof(transferOptions));
        _fileSystem = fileSystem ?? new PhysicalLocalFileSystem();
        _rootProvider = rootProvider ?? new PhysicalLocalRootProvider();
        Entries = _entries;
        Roots = _roots;
        SelectedEntries = [];
        NavigateToCommand = ReactiveCommand.CreateFromTask<string>(path => NavigateToAsync(path, _lifetime.Token));
        ActivateCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => ActivateAsync(entry, _lifetime.Token));
        GoUpCommand = ReactiveCommand.CreateFromTask(() => GoUpAsync(_lifetime.Token));
        RefreshCommand = ReactiveCommand.CreateFromTask(() => RefreshAsync(_lifetime.Token));
        SwitchRootCommand = ReactiveCommand.CreateFromTask<LocalRootEntry>(root => SwitchRootAsync(root, _lifetime.Token));
        RefreshRootsCommand = ReactiveCommand.CreateFromTask(() => RefreshRootsAsync(_lifetime.Token));
        DeleteItemCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => DeleteItemAsync(entry, _lifetime.Token));
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(() => DeleteSelectedAsync(_lifetime.Token));
        UploadSelectedCommand = ReactiveCommand.CreateFromTask(() => UploadSelectedAsync?.Invoke() ?? Task.CompletedTask);
        RenameCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => RenameAsync(entry, _lifetime.Token));
        NewFolderCommand = ReactiveCommand.CreateFromTask(() => NewFolderAsync(_lifetime.Token));
        OpenItemCommand = ReactiveCommand.CreateFromTask<LocalFileEntry>(entry => OpenItemAsync(entry, _lifetime.Token));
        SortCommand = ReactiveCommand.Create<string>(ToggleSort);
    }

    /// <summary>可见的本地行,包含文件系统根之外的一个合成父目录行。</summary>
    public ObservableCollection<LocalFileEntry> Entries { get; }

    /// <summary>本地面板可选中的物理根。</summary>
    public ObservableCollection<LocalRootEntry> Roots { get; }

    /// <summary>包含当前路径的根,按最长前缀匹配选出。</summary>
    public LocalRootEntry? SelectedRoot
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>由宿主用于多项操作的已选中行。</summary>
    public ObservableCollection<LocalFileEntry> SelectedEntries { get; }

    /// <summary>导航到规范化的本地目录。</summary>
    public ReactiveCommand<string, RxVoid> NavigateToCommand { get; }

    /// <summary>进入一个被激活的目录行。</summary>
    public ReactiveCommand<LocalFileEntry, RxVoid> ActivateCommand { get; }

    /// <summary>导航到父目录。</summary>
    public ReactiveCommand<RxVoid, RxVoid> GoUpCommand { get; }

    /// <summary>刷新当前目录列举。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>将当前目录切换到所选根目录。</summary>
    public ReactiveCommand<LocalRootEntry, RxVoid> SwitchRootCommand { get; }

    /// <summary>重新加载可用的本地根列表。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshRootsCommand { get; }

    /// <summary>经确认后永久删除单个本地条目。</summary>
    public ReactiveCommand<LocalFileEntry, RxVoid> DeleteItemCommand { get; }

    /// <summary>经确认后永久删除选中的本地条目。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteSelectedCommand { get; }

    /// <summary>由宿主提供的上传委托,用于独立 SFTP 文档。</summary>
    public ReactiveCommand<RxVoid, RxVoid> UploadSelectedCommand { get; }

    /// <summary>就地重命名选中的本地条目。</summary>
    public ReactiveCommand<LocalFileEntry, RxVoid> RenameCommand { get; }

    /// <summary>在当前目录中创建新文件夹。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NewFolderCommand { get; }

    /// <summary>用默认程序打开本地文件,或进入某个目录。</summary>
    public ReactiveCommand<LocalFileEntry, RxVoid> OpenItemCommand { get; }

    /// <summary>由宿主提供、被 UploadSelectedCommand 调用的上传委托。</summary>
    public Func<Task>? UploadSelectedAsync { get; set; }

    /// <summary>按名称、大小或修改时间对可见行排序。</summary>
    public ReactiveCommand<string, RxVoid> SortCommand { get; }

    /// <summary>注入的破坏性操作确认回调。</summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>由视图设置:提示输入一行文本(标题、初始值)→ 输入的文本或 null。</summary>
    public Func<string, string, Task<string?>>? PromptForText { get; set; }

    /// <summary>由视图设置:用操作系统默认程序打开本地文件。</summary>
    public Func<string, Task>? OpenLocalFile { get; set; }

    /// <summary>在文档排空并关闭其 SFTP 通道前,取消本地工作。</summary>
    public void Detach() => _lifetime.Cancel();

    /// <summary>当前列举的规范化目录。</summary>
    public string CurrentPath
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Breadcrumbs));
            this.RaisePropertyChanged(nameof(RootBreadcrumbLabel));
            this.RaisePropertyChanged(nameof(RootBreadcrumbPath));
        }
    } = string.Empty;

    /// <summary>当前路径的可点击面包屑分段,最深的在最后。根由 RootBreadcrumbLabel 处理。</summary>
    public IReadOnlyList<LocalBreadcrumbSegment> Breadcrumbs
    {
        get
        {
            string root = RootBreadcrumbLabel;
            if (string.IsNullOrEmpty(CurrentPath) || CurrentPath == root)
            {
                return [];
            }

            // 守卫:CurrentPath 仍为空时 RootBreadcrumbLabel 可能解析为占位根,
            // 此时下方切片会因 startIndex > length 而报错,需直接返回空。
            if (root.Length >= CurrentPath.Length || !CurrentPath.StartsWith(root, PathComparison))
            {
                return [];
            }

            char sep = Path.DirectorySeparatorChar;
            ReadOnlySpan<char> relative = CurrentPath.AsSpan(root.Length).TrimStart(sep);
            if (relative.Length == 0)
            {
                return [];
            }

            string[] parts = relative.ToString().Split(sep, StringSplitOptions.RemoveEmptyEntries);
            return [.. parts.Select((part, i) =>
            {
                string path = root.TrimEnd(sep) + sep + string.Join(sep, parts.Take(i + 1));
                return new LocalBreadcrumbSegment(part, path);
            })];
        }
    }

    /// <summary>根面包屑按钮的标签(如 Linux 上为“/”,Windows 上为“C:\”)。</summary>
    public string RootBreadcrumbLabel
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath))
            {
                return OperatingSystem.IsWindows() ? "C:\\" : "/";
            }
            string? root = Path.GetPathRoot(CurrentPath);
            if (string.IsNullOrEmpty(root))
            {
                return "/";
            }
            // 确保 Windows 根路径以分隔符结尾(如 "C:\" 而非 "C:")
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
    }

    /// <summary>根面包屑按钮导航所用的路径。</summary>
    public string RootBreadcrumbPath => RootBreadcrumbLabel;

    /// <summary>用于面包屑显示的 OS 路径分隔符字符。</summary>
    public static string PathSeparator => Path.DirectorySeparatorChar.ToString();

    /// <summary>目录列举是否正在进行。</summary>
    public bool IsDirectoryLoading
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>最近一次列举或删除的错误。</summary>
    public string? ErrorMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前生效的排序列。</summary>
    public string SortColumn
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "name";

    /// <summary>当前排序是否为降序。</summary>
    public bool SortDescending
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>加载配置的目录,失败时回退到用户主目录。</summary>
    public async Task LoadInitialAsync(CancellationToken cancellationToken = default)
    {
        await RefreshRootsAsync(cancellationToken);
        string configured = ExpandConfiguredPath(_transferOptions.LocalDownloadDirectory);
        string home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (string candidate in new[] { configured, home }.Distinct(StringComparer.Ordinal))
        {
            if (!DirectoryIsAccessible(candidate))
            {
                continue;
            }

            await NavigateToAsync(candidate, cancellationToken);
            if (ErrorMessage is null)
            {
                return;
            }
        }

        CurrentPath = home;
        SyncSelectedRoot(home);
    }

    /// <summary>重新加载本地根列表,保留当前选中的路径。</summary>
    public async Task RefreshRootsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LocalRootEntry> roots = await _rootProvider.EnumerateAsync(cancellationToken);
        _roots.Clear();
        foreach (LocalRootEntry root in roots)
        {
            _roots.Add(root);
        }
        SyncSelectedRoot(CurrentPath);
    }

    /// <summary>若所选根可访问,则切换到其路径。</summary>
    public async Task SwitchRootAsync(LocalRootEntry? root, CancellationToken cancellationToken = default)
    {
        if (_isSwitchingRoot || root is null || !root.IsAccessible)
        {
            return;
        }

        _isSwitchingRoot = true;
        try
        {
            await NavigateToAsync(root.FullPath, cancellationToken);
        }
        finally
        {
            _isSwitchingRoot = false;
        }
    }

    /// <summary>列举目录,列举失败时保留原有行。</summary>
    public async Task NavigateToAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string canonical = Path.GetFullPath(path);
        long version = Interlocked.Increment(ref _navigationVersion);
        try
        {
            ErrorMessage = null;
            IsDirectoryLoading = true;
            List<LocalFileEntry> listed = await ListDirectoryAsync(canonical, cancellationToken);
            if (version != Volatile.Read(ref _navigationVersion))
            {
                return;
            }

            bool changed = !string.Equals(CurrentPath, canonical, PathComparison);
            var selected = SelectedEntries
                .Where(entry => !entry.IsParentEntry)
                .Select(entry => entry.FullPath)
                .ToHashSet(PathComparisonComparer);
            CurrentPath = canonical;
            if (changed)
            {
                SelectedEntries.Clear();
            }
            RebuildEntries(listed);
            SyncSelectedRoot(canonical);
            if (!changed)
            {
                RestoreSelection(selected);
            }
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref _navigationVersion))
            {
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _navigationVersion))
            {
                IsDirectoryLoading = false;
            }
        }
    }

    /// <summary>刷新当前目录,或执行初始加载。</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(CurrentPath)
            ? LoadInitialAsync(cancellationToken)
            : NavigateToAsync(CurrentPath, cancellationToken);

    /// <summary>导航到父目录,停留在文件系统根处。</summary>
    public Task GoUpAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(CurrentPath)
            ? LoadInitialAsync(cancellationToken)
            : NavigateToAsync(ParentOf(CurrentPath), cancellationToken);

    /// <summary>进入一个目录条目;文件与父目录行不响应。</summary>
    public Task ActivateAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default) =>
        entry is { IsDirectory: true } ? NavigateToAsync(entry.FullPath, cancellationToken) : Task.CompletedTask;

    /// <summary>经确认后永久删除单个条目,且不跟随链接。</summary>
    public async Task DeleteItemAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default)
    {
        if (entry is null || entry.IsParentEntry || ConfirmDelete is null)
        {
            return;
        }

        if (!await ConfirmDelete($"Delete '{entry.Name}' permanently?"))
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            await DeletePathAsync(ToFileSystemEntry(entry), cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>经确认后永久删除选中条目,且不跟随链接。</summary>
    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        LocalFileEntry[] targets = [.. SelectedEntries.Where(entry => !entry.IsParentEntry)];
        if (targets.Length == 0 || ConfirmDelete is null || !await ConfirmDelete($"Delete {targets.Length} item(s) permanently?"))
        {
            return;
        }

        foreach (LocalFileEntry target in targets)
        {
            try
            {
                await DeletePathAsync(ToFileSystemEntry(target), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ErrorMessage = null;
                break;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                break;
            }
        }

        await RefreshAsync(cancellationToken);
    }

    private async Task<List<LocalFileEntry>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<LocalFileSystemEntry> entries = await _fileSystem.EnumerateAsync(
            path,
            cancellationToken
        );
        return [
            .. entries.Select(entry => new LocalFileEntry(
                entry.Name,
                entry.FullPath,
                entry.IsDirectory,
                entry.SizeBytes,
                entry.LastModified,
                entry.IsReparsePoint
            )),
        ];
    }

    private async Task DeletePathAsync(
        LocalFileSystemEntry entry,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.IsReparsePoint)
        {
            await DeleteSingleEntryAsync(entry, cancellationToken);
            return;
        }

        if (!entry.IsDirectory)
        {
            await _fileSystem.DeleteFileAsync(entry.FullPath, cancellationToken);
            return;
        }

        IReadOnlyList<LocalFileSystemEntry> children = await _fileSystem.EnumerateAsync(
            entry.FullPath,
            cancellationToken
        );
        foreach (LocalFileSystemEntry child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeletePathAsync(child, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _fileSystem.DeleteDirectoryAsync(entry.FullPath, cancellationToken);
    }

    private async Task DeleteSingleEntryAsync(
        LocalFileSystemEntry entry,
        CancellationToken cancellationToken
    )
    {
        if (entry.IsDirectory)
        {
            await _fileSystem.DeleteDirectoryAsync(entry.FullPath, cancellationToken);
        }
        else
        {
            await _fileSystem.DeleteFileAsync(entry.FullPath, cancellationToken);
        }
    }

    private static LocalFileSystemEntry ToFileSystemEntry(LocalFileEntry entry) =>
        new(
            entry.Name,
            entry.FullPath,
            entry.IsDirectory,
            entry.SizeBytes,
            entry.LastModified,
            entry.IsReparsePoint
        );

    private void ToggleSort(string? column)
    {
        if (string.IsNullOrWhiteSpace(column) || column is not ("name" or "size" or "modified"))
        {
            return;
        }
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }
        RebuildEntries(_entries.Where(entry => !entry.IsParentEntry));
    }

    private void RebuildEntries(IEnumerable<LocalFileEntry> entries)
    {
        IEnumerable<LocalFileEntry> sorted = entries.Where(entry => !entry.IsParentEntry);
        IOrderedEnumerable<LocalFileEntry> directories = sorted.OrderByDescending(entry => entry.IsDirectory);
        sorted = SortColumn switch
        {
            "size" => SortDescending ? directories.ThenByDescending(entry => entry.SizeBytes) : directories.ThenBy(entry => entry.SizeBytes),
            "modified" => SortDescending ? directories.ThenByDescending(entry => entry.LastModified) : directories.ThenBy(entry => entry.LastModified),
            _ => SortDescending ? directories.ThenByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase) : directories.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };
        List<LocalFileEntry> rebuilt = string.Equals(
            CurrentPath,
            Path.GetPathRoot(CurrentPath),
            PathComparison
        )
            ? [.. sorted]
            : [LocalFileEntry.CreateParent(ParentOf(CurrentPath)), .. sorted];
        _entries.ReplaceAll(rebuilt);
    }

    private void SyncSelectedRoot(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        LocalRootEntry? root = Roots
            .Where(candidate => candidate.IsAccessible && IsPathWithin(candidate.FullPath, path))
            .OrderByDescending(candidate => candidate.FullPath.Length)
            .FirstOrDefault();
        if (!ReferenceEquals(SelectedRoot, root))
        {
            SelectedRoot = root;
        }
    }

    private static bool IsPathWithin(string root, string path)
    {
        string canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string canonicalPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(canonicalRoot, canonicalPath, PathComparison)
            || canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, PathComparison)
            || canonicalPath.StartsWith(canonicalRoot + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private void RestoreSelection(HashSet<string> selectedPaths)
    {
        SelectedEntries.Clear();
        foreach (LocalFileEntry entry in Entries.Where(entry => selectedPaths.Contains(entry.FullPath)))
        {
            SelectedEntries.Add(entry);
        }
    }

    /// <summary>
    /// 解析配置的下载目录:"~" 与相对路径一律以用户主目录为基准(见 <see cref="UserPathResolver" />),
    /// 空值跟随系统"下载"文件夹;该目录不可用时由调用方回退到用户主目录。
    /// </summary>
    private static string ExpandConfiguredPath(string configured) =>
        UserPathResolver.ResolveOrDownloads(configured);

    private static bool DirectoryIsAccessible(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return false;
            }

            using IEnumerator<FileSystemInfo> entries = new DirectoryInfo(path)
                .EnumerateFileSystemInfos()
                .GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string ParentOf(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        ?? Path.GetPathRoot(path)
        ?? path;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparisonComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private async Task RenameAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default)
    {
        entry ??= SelectedEntries.FirstOrDefault(static e => !e.IsParentEntry);
        if (PromptForText is null || entry is null || entry.IsParentEntry)
        {
            return;
        }
        string? newName = await PromptForText("Rename", entry.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == entry.Name)
        {
            return;
        }
        string trimmedName = newName.Trim();
        if (!LocalPathSafety.IsSafeLeafName(trimmedName))
        {
            ErrorMessage = Strings.Get("KeySvc_InvalidName");
            return;
        }
        string target = Path.Combine(ParentOf(entry.FullPath), trimmedName);
        try
        {
            ErrorMessage = null;
            await _fileSystem.MoveAsync(entry.FullPath, target, cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task NewFolderAsync(CancellationToken cancellationToken = default)
    {
        if (PromptForText is null)
        {
            return;
        }
        string? name = await PromptForText("New Folder", "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        string trimmedName = name.Trim();
        if (!LocalPathSafety.IsSafeLeafName(trimmedName))
        {
            ErrorMessage = Strings.Get("KeySvc_InvalidName");
            return;
        }
        string target = Path.Combine(CurrentPath, trimmedName);
        try
        {
            ErrorMessage = null;
            await _fileSystem.CreateDirectoryAsync(target, cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task OpenItemAsync(LocalFileEntry? entry, CancellationToken cancellationToken = default)
    {
        entry ??= SelectedEntries.FirstOrDefault(static e => !e.IsParentEntry);
        if (entry is null || entry.IsParentEntry)
        {
            return;
        }
        if (entry.IsDirectory)
        {
            await NavigateToAsync(entry.FullPath, cancellationToken);
        }
        else if (OpenLocalFile is not null)
        {
            await OpenLocalFile(entry.FullPath);
        }
    }
}

/// <summary>本地路径面包屑的一个可点击分段。</summary>
public sealed record LocalBreadcrumbSegment(string Name, string Path);

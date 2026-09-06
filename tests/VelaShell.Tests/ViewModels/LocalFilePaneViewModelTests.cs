using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
[TestCategory("LocalFilePane")]
public sealed class LocalFilePaneViewModelTests
{
    [TestMethod]
    public async Task InitialPath_UsesAccessibleConfiguredDirectory()
    {
        using TempDirectory temp = new();
        TransferOptions options = new() { LocalDownloadDirectory = temp.Path };

        LocalFilePaneViewModel viewModel = new(options);
        await viewModel.LoadInitialAsync();

        Assert.AreEqual(Path.GetFullPath(temp.Path), viewModel.CurrentPath);
    }

    // ---- 目录变化自动刷新 ----
    //
    // 远端面板早有静默刷新,本地面板却要手按 F5:在本地解个压、下个文件,面板还停在旧内容上。
    // 这几条钉住"挂得上、换得掉、释放得干净、监视不了时不炸"。

    [TestMethod]
    public async Task NavigatingToADirectory_StartsWatchingIt()
    {
        var fs = new FakeLocalFileSystem();
        fs.SetChildren("/a", new LocalFileSystemEntry("f.txt", "/a/f.txt", false, 1, DateTime.UtcNow));
        using var vm = new LocalFilePaneViewModel(new TransferOptions(), fs, new FakeLocalRootProvider());

        await vm.NavigateToAsync("/a");

        Assert.AreEqual(Path.GetFullPath("/a"), fs.WatchedDirectory);
        Assert.AreEqual(1, fs.WatchCount);
    }

    [TestMethod]
    public async Task NavigatingAway_SwapsTheWatcherInsteadOfStacking()
    {
        var fs = new FakeLocalFileSystem();
        using var vm = new LocalFilePaneViewModel(new TransferOptions(), fs, new FakeLocalRootProvider());

        await vm.NavigateToAsync("/a");
        await vm.NavigateToAsync("/b");

        Assert.AreEqual(2, fs.WatchCount);
        Assert.AreEqual(1, fs.DisposedWatchCount, "切目录必须把上一个监视器释放掉,否则句柄一路累加。");
        Assert.AreEqual(Path.GetFullPath("/b"), fs.WatchedDirectory);
    }

    [TestMethod]
    public async Task Dispose_StopsWatching()
    {
        var fs = new FakeLocalFileSystem();
        var vm = new LocalFilePaneViewModel(new TransferOptions(), fs, new FakeLocalRootProvider());
        await vm.NavigateToAsync("/a");

        vm.Dispose();

        Assert.AreEqual(1, fs.DisposedWatchCount);
        Assert.IsNull(fs.WatchedDirectory);
    }

    [TestMethod]
    public async Task UnsupportedWatch_DegradesQuietly()
    {
        // 网络盘上 FileSystemWatcher 装不起来。那只是"没有自动刷新",不是错误 ——
        // 既不该抛,也不该在面板上留一条错误信息。
        var fs = new FakeLocalFileSystem { WatchUnsupported = true };
        using var vm = new LocalFilePaneViewModel(new TransferOptions(), fs, new FakeLocalRootProvider());

        await vm.NavigateToAsync("/a");

        Assert.AreEqual(Path.GetFullPath("/a"), vm.CurrentPath);
        Assert.IsNull(vm.ErrorMessage);
    }

    [TestMethod]
    public async Task DisposingTwice_IsSafe()
    {
        var fs = new FakeLocalFileSystem();
        var vm = new LocalFilePaneViewModel(new TransferOptions(), fs, new FakeLocalRootProvider());
        await vm.NavigateToAsync("/a");

        vm.Dispose();
        vm.Dispose();

        Assert.AreEqual(1, fs.DisposedWatchCount);
    }

    [TestMethod]
    public void ParentEntry_ExposesOnlyParentIconState()
    {
        var parent = LocalFileEntry.CreateParent(Path.GetTempPath());
        LocalFileEntry folder = new("folder", Path.Combine(Path.GetTempPath(), "folder"), true, 0, DateTime.UtcNow);
        LocalFileEntry file = new("file.txt", Path.Combine(Path.GetTempPath(), "file.txt"), false, 1, DateTime.UtcNow);

        Assert.IsFalse(parent.IsRegularDirectory);
        Assert.IsFalse(parent.IsRegularFile);
        Assert.IsTrue(folder.IsRegularDirectory);
        Assert.IsFalse(folder.IsRegularFile);
        Assert.IsFalse(file.IsRegularDirectory);
        Assert.IsTrue(file.IsRegularFile);
        Assert.AreEqual(string.Empty, parent.FormattedSize);
        Assert.AreEqual(string.Empty, parent.FormattedModifiedTime);
        Assert.AreEqual(string.Empty, folder.FormattedSize);
        Assert.AreEqual("1.0 KB", new LocalFileEntry("data", "data", false, 1024, DateTime.Now).FormattedSize);
        Assert.IsFalse(string.IsNullOrWhiteSpace(file.FormattedModifiedTime));
    }

    [TestMethod]
    public async Task RootSwitch_ChangesDirectoryAndSynchronizesSelectedRoot()
    {
        string first = Path.Combine(Path.GetTempPath(), "velashell-root-a");
        string second = Path.Combine(Path.GetTempPath(), "velashell-root-b");
        FakeLocalFileSystem fileSystem = new();
        fileSystem.SetChildren(first);
        fileSystem.SetChildren(second);
        FakeLocalRootProvider roots = new(
            new LocalRootEntry("A", first, true, first),
            new LocalRootEntry("B", second, true, second)
        );
        LocalFilePaneViewModel viewModel = new(new() { LocalDownloadDirectory = first }, fileSystem, roots);

        await viewModel.LoadInitialAsync();
        await viewModel.SwitchRootAsync(viewModel.Roots.Single(root => root.DisplayName == "B"));

        Assert.AreEqual(Path.GetFullPath(second), viewModel.CurrentPath);
        Assert.AreEqual("B", viewModel.SelectedRoot?.DisplayName);
    }

    [TestMethod]
    public async Task RootSwitch_InaccessibleRootFailsClosed()
    {
        string accessible = Path.Combine(Path.GetTempPath(), "velashell-root-accessible");
        string inaccessible = Path.Combine(Path.GetTempPath(), "velashell-root-inaccessible");
        FakeLocalFileSystem fileSystem = new();
        fileSystem.SetChildren(accessible);
        FakeLocalRootProvider roots = new(
            new LocalRootEntry("Accessible", accessible, true, accessible),
            new LocalRootEntry("Unavailable", inaccessible, false, inaccessible)
        );
        LocalFilePaneViewModel viewModel = new(new() { LocalDownloadDirectory = accessible }, fileSystem, roots);

        await viewModel.LoadInitialAsync();
        await viewModel.SwitchRootAsync(viewModel.Roots.Single(root => !root.IsAccessible));

        Assert.AreNotEqual(Path.GetFullPath(inaccessible), viewModel.CurrentPath);
        Assert.IsNull(viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task RefreshRoots_ReplacesRootsAndKeepsLongestContainingRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "velashell-root");
        string nested = Path.Combine(root, "nested");
        FakeLocalFileSystem fileSystem = new();
        fileSystem.SetChildren(root, new LocalFileSystemEntry("nested", nested, true, 0, DateTime.Now));
        fileSystem.SetChildren(nested);
        FakeLocalRootProvider roots = new(new LocalRootEntry("Root", root, true, root));
        LocalFilePaneViewModel viewModel = new(new() { LocalDownloadDirectory = root }, fileSystem, roots);

        await viewModel.LoadInitialAsync();
        await viewModel.NavigateToAsync(nested);
        roots.Set(new LocalRootEntry("Nested", nested, true, nested));
        await viewModel.RefreshRootsAsync();

        Assert.AreEqual("Nested", viewModel.SelectedRoot?.DisplayName);
    }

    [TestMethod]
    public async Task PhysicalRoots_UseNeutralHomeLabelAndCanonicalTooltips()
    {
        IReadOnlyList<LocalRootEntry> roots = await new PhysicalLocalRootProvider().EnumerateAsync(CancellationToken.None);
        LocalRootEntry home = roots.Single(root =>
            string.Equals(root.FullPath, Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

        Assert.AreEqual("~", home.DisplayName);
        Assert.AreEqual(home.FullPath, home.Tooltip);
        Assert.Contains(root => root.FullPath == (OperatingSystem.IsWindows() ? Path.GetPathRoot(home.FullPath) : "/"), roots);
    }

    [TestMethod]
    public async Task InitialPath_FallsBackToHome_WhenConfiguredDirectoryIsInvalid()
    {
        TransferOptions options = new()
        {
            LocalDownloadDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        };

        LocalFilePaneViewModel viewModel = new(options);
        await viewModel.LoadInitialAsync();

        Assert.AreEqual(
            Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            viewModel.CurrentPath
        );
    }

    [TestMethod]
    public async Task InitialPath_FallsBackToHome_WhenConfiguredPathIsMalformed()
    {
        TransferOptions options = new() { LocalDownloadDirectory = "\0malformed" };
        LocalFilePaneViewModel viewModel = new(options);

        await viewModel.LoadInitialAsync();

        Assert.AreEqual(
            Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            viewModel.CurrentPath
        );
    }

    [TestMethod]
    public async Task Listing_NavigatesSortsAndPreservesMultiSelection()
    {
        using TempDirectory temp = new();
        Directory.CreateDirectory(Path.Combine(temp.Path, "z-folder"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "a.txt"), "a");
        LocalFilePaneViewModel viewModel = new(new() { LocalDownloadDirectory = temp.Path });

        await viewModel.LoadInitialAsync();
        Assert.AreEqual("z-folder", viewModel.Entries[1].Name);
        viewModel.SelectedEntries.Add(viewModel.Entries.Single(entry => entry.Name == "b.txt"));
        viewModel.SelectedEntries.Add(viewModel.Entries.Single(entry => entry.Name == "a.txt"));
        viewModel.SortCommand.Execute("name").Subscribe();
        Assert.IsTrue(viewModel.SortDescending);
        Assert.HasCount(2, viewModel.SelectedEntries);

        await viewModel.NavigateToAsync(Path.Combine(temp.Path, "z-folder"));
        Assert.AreEqual(Path.Combine(temp.Path, "z-folder"), viewModel.CurrentPath);
        await viewModel.GoUpAsync();
        Assert.AreEqual(Path.GetFullPath(temp.Path), viewModel.CurrentPath);
    }

    [TestMethod]
    public async Task Refresh_SetsErrorState_AndRetainsRows()
    {
        using TempDirectory temp = new();
        string file = Path.Combine(temp.Path, "file.txt");
        await File.WriteAllTextAsync(file, "data");
        LocalFilePaneViewModel viewModel = new(new() { LocalDownloadDirectory = temp.Path });
        await viewModel.LoadInitialAsync();
        int rowCount = viewModel.Entries.Count;

        string inaccessible = Path.Combine(temp.Path, "missing");
        await viewModel.NavigateToAsync(inaccessible);

        Assert.AreEqual(Path.GetFullPath(temp.Path), viewModel.CurrentPath);
        Assert.HasCount(rowCount, viewModel.Entries);
        Assert.IsFalse(string.IsNullOrWhiteSpace(viewModel.ErrorMessage));
    }

    [TestMethod]
    public async Task Delete_RequiresConfirmation_AndHonorsRejectAndAccept()
    {
        using TempDirectory temp = new();
        string file = Path.Combine(temp.Path, "delete.txt");
        await File.WriteAllTextAsync(file, "data");
        LocalFilePaneViewModel viewModel = new(new() { LocalDownloadDirectory = temp.Path });
        await viewModel.LoadInitialAsync();
        LocalFileEntry entry = viewModel.Entries.Single(item => item.Name == "delete.txt");

        viewModel.ConfirmDelete = _ => Task.FromResult(false);
        await viewModel.DeleteItemAsync(entry);
        Assert.IsTrue(File.Exists(file));

        viewModel.ConfirmDelete = _ => Task.FromResult(true);
        await viewModel.DeleteItemAsync(entry);
        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    public async Task Delete_CancellationLeavesRemainingTreeAndCanBeRetried()
    {
        using TempDirectory temp = new();
        string folder = Path.Combine(temp.Path, "folder");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        await File.WriteAllTextAsync(Path.Combine(folder, "nested", "file.txt"), "data");
        LocalFilePaneViewModel viewModel = new(new() { LocalDownloadDirectory = temp.Path })
        {
            ConfirmDelete = _ => Task.FromResult(true),
        };
        await viewModel.LoadInitialAsync();
        LocalFileEntry entry = viewModel.Entries.Single(item => item.Name == "folder");

        using (CancellationTokenSource cancelled = new())
        {
            cancelled.Cancel();
            await viewModel.DeleteItemAsync(entry, cancelled.Token);
        }
        Assert.IsTrue(Directory.Exists(folder));

        using (CancellationTokenSource interruptedAgain = new())
        {
            interruptedAgain.Cancel();
            await viewModel.DeleteItemAsync(entry, interruptedAgain.Token);
        }
        Assert.IsTrue(Directory.Exists(folder));

        await viewModel.DeleteItemAsync(entry);
        Assert.IsFalse(Directory.Exists(folder));
    }

    [TestMethod]
    public async Task Delete_ReparseDirectoryEntryIsDeletedWithoutEnumeratingItsTarget()
    {
        string root = Path.Combine(Path.GetTempPath(), $"velashell-fake-local-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "folder");
        string link = Path.Combine(folder, "outside-link");
        string outside = Path.Combine(root, "outside");
        FakeLocalFileSystem fileSystem = new();
        fileSystem.SetChildren(
            root,
            new LocalFileSystemEntry("folder", folder, true, 0, DateTime.UtcNow)
        );
        fileSystem.SetChildren(
            folder,
            new LocalFileSystemEntry("outside-link", link, true, 0, DateTime.UtcNow, true)
        );
        fileSystem.SetChildren(
            outside,
            new LocalFileSystemEntry("must-survive.txt", Path.Combine(outside, "must-survive.txt"), false, 1, DateTime.UtcNow)
        );
        LocalFilePaneViewModel viewModel = new(
            new() { LocalDownloadDirectory = root },
            fileSystem
        )
        {
            ConfirmDelete = _ => Task.FromResult(true),
        };

        await viewModel.NavigateToAsync(root);
        await viewModel.DeleteItemAsync(viewModel.Entries.Single(item => item.Name == "folder"));

        Assert.DoesNotContain(outside, fileSystem.EnumeratedPaths);
        Assert.Contains(link, fileSystem.DeletedDirectories);
        Assert.Contains(folder, fileSystem.DeletedDirectories);
        Assert.DoesNotContain(Path.Combine(outside, "must-survive.txt"), fileSystem.DeletedFiles);
    }

    [TestMethod]
    public async Task Delete_CancellationAfterFirstChildStopsRecursiveTraversalDeterministically()
    {
        string root = Path.Combine(Path.GetTempPath(), $"velashell-fake-local-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "folder");
        string first = Path.Combine(folder, "01-first.txt");
        string second = Path.Combine(folder, "02-second.txt");
        FakeLocalFileSystem fileSystem = new();
        fileSystem.SetChildren(
            root,
            new LocalFileSystemEntry("folder", folder, true, 0, DateTime.UtcNow)
        );
        fileSystem.SetChildren(
            folder,
            new LocalFileSystemEntry("01-first.txt", first, false, 1, DateTime.UtcNow),
            new LocalFileSystemEntry("02-second.txt", second, false, 1, DateTime.UtcNow)
        );
        using CancellationTokenSource cancellation = new();
        fileSystem.AfterFirstDeletion = cancellation.Cancel;
        LocalFilePaneViewModel viewModel = new(
            new() { LocalDownloadDirectory = root },
            fileSystem
        )
        {
            ConfirmDelete = _ => Task.FromResult(true),
        };

        await viewModel.NavigateToAsync(root);
        await viewModel.DeleteItemAsync(
            viewModel.Entries.Single(item => item.Name == "folder"),
            cancellation.Token
        );

        Assert.Contains(first, fileSystem.DeletedFiles);
        Assert.DoesNotContain(second, fileSystem.DeletedFiles);
        Assert.DoesNotContain(folder, fileSystem.DeletedDirectories);
        Assert.Contains(folder, fileSystem.EnumeratedPaths);
    }

    [TestMethod]
    [DataRow("C:\\absolute.txt")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("nested/name.txt")]
    [DataRow("nested\\name.txt")]
    public void Destination_RejectsUnsafeRemoteNames(string name)
    {
        using TempDirectory temp = new();
        Assert.IsFalse(LocalPathSafety.TryResolveDestination(temp.Path, name, out _));
    }

    [TestMethod]
    public void Destination_HandlesWindowsReservedDeviceNamesByPlatform()
    {
        using TempDirectory temp = new();
        bool safe = LocalPathSafety.TryResolveDestination(temp.Path, "CON", out _);

        Assert.AreEqual(!OperatingSystem.IsWindows(), safe);
    }

    [TestMethod]
    public void Destination_CanonicalizesInsideCurrentDirectory()
    {
        using TempDirectory temp = new();
        string? resolved = LocalPathSafety.ResolveDestination(temp.Path, "report.txt");

        Assert.AreEqual(Path.Combine(Path.GetFullPath(temp.Path), "report.txt"), resolved);
    }

    [TestMethod]
    public void Destination_RejectsExistingSymlinkChild()
    {
        using TempDirectory root = new();
        using TempDirectory outside = new();
        string link = Path.Combine(root.Path, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
                or PlatformNotSupportedException
                or IOException
        )
        {
            Assert.Inconclusive($"Symlink creation is unavailable: {ex.Message}");
            return;
        }

        Assert.IsFalse(LocalPathSafety.TryResolveDestination(root.Path, "linked", out _));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"velashell-local-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    private sealed class FakeLocalFileSystem : ILocalFileSystem
    {
        private readonly Dictionary<string, IReadOnlyList<LocalFileSystemEntry>> _children = [with(
            StringComparer.OrdinalIgnoreCase
        )];

        public List<string> EnumeratedPaths { get; } = [];
        public List<string> DeletedFiles { get; } = [];
        public List<string> DeletedDirectories { get; } = [];
        public Action? AfterFirstDeletion { get; set; }

        public void SetChildren(string path, params LocalFileSystemEntry[] children) =>
            _children[path] = children;

        public Task<IReadOnlyList<LocalFileSystemEntry>> EnumerateAsync(
            string path,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumeratedPaths.Add(path);
            return Task.FromResult(_children.GetValueOrDefault(path, []));
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeletedFiles.Add(path);
            AfterFirstDeletion?.Invoke();
            return Task.CompletedTask;
        }

        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeletedDirectories.Add(path);
            return Task.CompletedTask;
        }

        public Task MoveAsync(string sourcePath, string destPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(-1L);
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream());
        }

        public Task<Stream> OpenWriteAsync(string path, FileMode mode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream());
        }

        // ---- 目录监视(自动刷新) ----

        /// <summary>当前被监视的目录;没在监视时为 null。</summary>
        public string? WatchedDirectory { get; private set; }

        /// <summary>累计装过的监视器数量(切目录应当是"换一个",不是"再加一个")。</summary>
        public int WatchCount { get; private set; }

        /// <summary>已释放的监视器数量。</summary>
        public int DisposedWatchCount { get; private set; }

        /// <summary>置 true 时 <see cref="Watch" /> 返回 null,模拟网络盘等监视不了的情形。</summary>
        public bool WatchUnsupported { get; set; }

        private Action? _onChanged;

        /// <summary>模拟目录发生了变化。</summary>
        public void RaiseChanged() => _onChanged?.Invoke();

        public IDisposable? Watch(string directory, Action onChanged)
        {
            if (WatchUnsupported)
            {
                return null;
            }
            WatchedDirectory = directory;
            WatchCount++;
            _onChanged = onChanged;
            return new WatchHandle(this);
        }

        private sealed class WatchHandle(FakeLocalFileSystem owner) : IDisposable
        {
            public void Dispose()
            {
                owner.DisposedWatchCount++;
                owner.WatchedDirectory = null;
                owner._onChanged = null;
            }
        }
    }

    private sealed class FakeLocalRootProvider(params LocalRootEntry[] roots) : ILocalRootProvider
    {
        private IReadOnlyList<LocalRootEntry> _roots = roots;

        public void Set(params LocalRootEntry[] roots) => _roots = roots;

        public Task<IReadOnlyList<LocalRootEntry>> EnumerateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_roots);
        }
    }
}

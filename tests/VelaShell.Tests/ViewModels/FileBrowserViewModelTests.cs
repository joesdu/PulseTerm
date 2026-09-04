using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class FileBrowserViewModelTests
{
    private readonly Guid _sessionId;
    private readonly ISftpService _sftpService;
    private readonly FileBrowserViewModel _vm;

    public FileBrowserViewModelTests()
    {
        _sftpService = Substitute.For<ISftpService>();
        _sessionId = Guid.NewGuid();
        _vm = new(_sftpService, _sessionId);
    }

    private static List<RemoteFileInfo> CreateTestFiles()
    {
        return
        [
            new()
            {
                Name = "documents",
                FullPath = "/home/user/documents",
                Size = 4096,
                Permissions = "drwxr-xr-x",
                IsDirectory = true,
                LastModified = DateTime.UtcNow.AddHours(-1),
                Owner = "user",
                Group = "user",
            },
            new()
            {
                Name = "readme.txt",
                FullPath = "/home/user/readme.txt",
                Size = 1234,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow.AddDays(-2),
                Owner = "user",
                Group = "user",
            },
            new()
            {
                Name = "photo.jpg",
                FullPath = "/home/user/photo.jpg",
                Size = 3567890,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow.AddMinutes(-30),
                Owner = "user",
                Group = "user",
            },
        ];
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task ListDirectory_PopulatesFilesCollection()
    {
        List<RemoteFileInfo> testFiles = CreateTestFiles();
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(testFiles));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        // Leading ".." parent row (§6), then name-ascending with directories grouped first.
        Assert.HasCount(4, _vm.Files);
        Assert.AreEqual("..", _vm.Files[0].Name);
        Assert.IsTrue(_vm.Files[0].IsParentEntry);
        Assert.AreEqual("documents", _vm.Files[1].Name);
        Assert.AreEqual("photo.jpg", _vm.Files[2].Name);
        Assert.AreEqual("readme.txt", _vm.Files[3].Name);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NavigateIntoFolder_UpdatesCurrentPath()
    {
        List<RemoteFileInfo> rootFiles = CreateTestFiles();
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(rootFiles));
        var subFiles = new List<RemoteFileInfo>
        {
            new()
            {
                Name = "report.pdf",
                FullPath = "/home/user/documents/report.pdf",
                Size = 524288,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user",
            },
        };
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subFiles));
        await _vm.NavigateToCommand.Execute("/home/user/documents").FirstAsync();
        Assert.AreEqual("/home/user/documents", _vm.CurrentPath);
        Assert.HasCount(2, _vm.Files);
        Assert.AreEqual("..", _vm.Files[0].Name);
        Assert.AreEqual("report.pdf", _vm.Files[1].Name);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Activate_OnDirectory_NavigatesInto()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var dir = new RemoteFileInfoViewModel(CreateTestFiles()[0]); // "documents", IsDirectory
        await _vm.ActivateCommand.Execute(dir).FirstAsync();
        Assert.AreEqual("/home/user/documents", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Activate_OnFile_DoesNotNavigate()
    {
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // "readme.txt", not a directory
        await _vm.ActivateCommand.Execute(file).FirstAsync();
        Assert.AreEqual("/", _vm.CurrentPath);
        await _sftpService
            .DidNotReceive()
            .ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task LoadInitial_NavigatesToWorkingDirectory()
    {
        _sftpService
            .GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/root"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/root", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        await _vm.LoadInitialCommand.Execute().FirstAsync();
        Assert.AreEqual("/root", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task LoadInitial_FallsBackToRoot_WhenWorkingDirectoryUnavailable()
    {
        _sftpService
            .GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("no cwd"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        await _vm.LoadInitialCommand.Execute().FirstAsync();
        Assert.AreEqual("/", _vm.CurrentPath);
    }

    /// <summary>
    /// 配置里的「默认打开路径」(FTP / FTPS 高级选项)排在候选路径的第一位:
    /// 上传目标常年是同一个目录,而 FTP 给的登录工作目录往往就是根。
    /// </summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task LoadInitial_PrefersTheConfiguredInitialPath_OverTheWorkingDirectory()
    {
        _sftpService
            .GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/var/www/html", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.InitialRemotePath = "/var/www/html";

        await _vm.LoadInitialCommand.Execute().FirstAsync();

        Assert.AreEqual("/var/www/html", _vm.CurrentPath);
    }

    /// <summary>
    /// 路径配错(打错字、目录被删、账号被 chroot)不该把用户堵在一张报错的空白页上:
    /// 依次回退到登录工作目录、根目录,与家目录进不去时回退根目录是同一条纪律。
    /// </summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task LoadInitial_WhenTheConfiguredPathCannotBeOpened_FallsBackToTheWorkingDirectory()
    {
        _sftpService
            .GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/home/deploy"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/nope", Arg.Any<CancellationToken>())
            .Returns<Task<List<RemoteFileInfo>>>(_ => throw new InvalidOperationException("550 No such directory"));
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/deploy", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.InitialRemotePath = "/nope";

        await _vm.LoadInitialCommand.Execute().FirstAsync();

        Assert.AreEqual("/home/deploy", _vm.CurrentPath);
        Assert.IsNull(_vm.ErrorMessage, "回退成功了就不该再挂着上一次的失败提示。");
    }

    // —— 列显示开关与新增的 所有者/分组/类型 列 ——————————————————————

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void Columns_AreAllVisibleByDefault()
    {
        Assert.IsTrue(_vm.ShowSizeColumn);
        Assert.IsTrue(_vm.ShowPermissionsColumn);
        Assert.IsTrue(_vm.ShowOwnerColumn);
        Assert.IsTrue(_vm.ShowGroupColumn);
        Assert.IsTrue(_vm.ShowTypeColumn);
        Assert.IsTrue(_vm.ShowModifiedColumn);
    }

    /// <summary>Grid 靠把列宽压成 0 来隐藏列:宽度、最小宽度与拖拽条要一起塌缩,漏一个就留白。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public void HidingColumn_CollapsesWidthMinWidthAndSplitter()
    {
        _vm.ShowOwnerColumn = false;

        Assert.AreEqual(0, _vm.OwnerGridWidth.Value);
        Assert.AreEqual(0, _vm.OwnerGridMinWidth);
        Assert.AreEqual(0, _vm.OwnerSplitterWidth.Value);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void ShowingColumn_RestoresUserWidth()
    {
        _vm.OwnerColumnWidth = new(140);
        _vm.ShowOwnerColumn = false;
        _vm.ShowOwnerColumn = true;

        Assert.AreEqual(140, _vm.OwnerGridWidth.Value);
        Assert.AreEqual(FileBrowserViewModel.MinOwnerWidth, _vm.OwnerGridMinWidth);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void TogglingColumn_ReportsChangeForPersistence()
    {
        List<(string Key, bool Visible)> toggles = [];
        _vm.ColumnVisibilityToggled = (key, visible) => toggles.Add((key, visible));

        _vm.ShowTypeColumn = false;
        _vm.ShowTypeColumn = false; // 同值重复设置不该再报一次

        Assert.HasCount(1, toggles);
        Assert.AreEqual(("type", false), toggles[0]);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void ColumnWidth_IsClampedToItsMinimum()
    {
        _vm.OwnerColumnWidth = new(5);
        Assert.AreEqual(FileBrowserViewModel.MinOwnerWidth, _vm.OwnerColumnWidth.Value);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Sort_ByOwner_OrdersAscending_WithDirectoriesFirst()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateOwnedFiles()));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        _vm.SortCommand.Execute("owner").Subscribe();

        Assert.AreEqual("owner", _vm.SortColumn);
        Assert.AreEqual("..", _vm.Files[0].Name); // 父目录行仍置顶
        Assert.AreEqual("srv", _vm.Files[1].Name); // 目录仍成组在前(属主 zoe)
        Assert.AreEqual("alice.txt", _vm.Files[2].Name);
        Assert.AreEqual("bob.txt", _vm.Files[3].Name);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void FileTypeDisplay_DescribesFoldersFilesAndExtensions()
    {
        // 断言比对本地化资源而非字面量:测试跑在哪个 UI 语言下都成立。
        Assert.AreEqual(Strings.Folder, TypeOf("srv", true));
        Assert.AreEqual(Strings.Format("Sftp_FileTypeExt", "PHP"), TypeOf("index.php", false));

        // 多重扩展名取最后一段。这里顺带盯住 Sftp_FileTypeExt 这条资源本身:
        // 上面那种“两边都走 Strings.Format”的写法在资源缺失时会一起退化成键名而依旧相等。
        Assert.Contains("GZ", TypeOf("archive.tar.gz", false));

        // 无扩展名、点开头的隐藏文件、以及不像扩展名的尾巴,都归为“文件”。
        Assert.AreEqual(Strings.File, TypeOf("README", false));
        Assert.AreEqual(Strings.File, TypeOf(".bashrc", false));
        Assert.AreEqual(Strings.File, TypeOf("backup.tar 副本", false));
        Assert.AreEqual(Strings.File, TypeOf("trailing.", false));
    }

    /// <summary>合成的 ".." 行没有类型/大小/时间可言,这些列对它应为空。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public void FileTypeDisplay_IsEmptyForParentRow()
    {
        Assert.AreEqual(
            string.Empty,
            RemoteFileInfoViewModel.CreateParentEntry("/home").FileTypeDisplay
        );
    }

    private static string TypeOf(string name, bool isDirectory) =>
        new RemoteFileInfoViewModel(
            new()
            {
                Name = name,
                FullPath = "/tmp/" + name,
                Size = 1,
                Permissions = "-rw-r--r--",
                IsDirectory = isDirectory,
                LastModified = DateTime.UtcNow,
                Owner = "root",
                Group = "root",
            }
        ).FileTypeDisplay;

    private static List<RemoteFileInfo> CreateOwnedFiles() =>
        [
            new()
            {
                Name = "bob.txt",
                FullPath = "/home/user/bob.txt",
                Size = 1,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Owner = "bob",
                Group = "staff",
            },
            new()
            {
                Name = "alice.txt",
                FullPath = "/home/user/alice.txt",
                Size = 2,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Owner = "alice",
                Group = "staff",
            },
            new()
            {
                Name = "srv",
                FullPath = "/home/user/srv",
                Size = 4096,
                Permissions = "drwxr-xr-x",
                IsDirectory = true,
                LastModified = DateTime.UtcNow,
                Owner = "zoe",
                Group = "staff",
            },
        ];

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Sort_BySize_OrdersAscending_WithDirectoriesFirst()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        // Default sort is name-ascending: ".." pinned first, directory next, then files by name.
        Assert.AreEqual("..", _vm.Files[0].Name);
        Assert.AreEqual("documents", _vm.Files[1].Name);
        _vm.SortCommand.Execute("size").Subscribe();
        Assert.AreEqual("..", _vm.Files[0].Name); // parent row stays pinned
        Assert.AreEqual("documents", _vm.Files[1].Name); // directory stays grouped on top
        Assert.AreEqual("readme.txt", _vm.Files[2].Name); // 1234 bytes
        Assert.AreEqual("photo.jpg", _vm.Files[3].Name); // 3567890 bytes
        Assert.AreEqual("size", _vm.SortColumn);
        Assert.IsFalse(_vm.SortDescending);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Sort_SameColumnTwice_FlipsDirection()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();
        _vm.SortCommand.Execute("size").Subscribe();
        Assert.IsFalse(_vm.SortDescending);
        _vm.SortCommand.Execute("size").Subscribe();
        Assert.IsTrue(_vm.SortDescending);
        Assert.AreEqual("..", _vm.Files[0].Name); // parent row stays pinned
        Assert.AreEqual("documents", _vm.Files[1].Name); // directory still first
        Assert.AreEqual("photo.jpg", _vm.Files[2].Name); // largest file first
        Assert.AreEqual("readme.txt", _vm.Files[3].Name);
        Assert.AreEqual(" ▼", _vm.SizeSortGlyph);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task GoUp_NavigatesToParentDirectory()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/home/user/documents";
        await _vm.GoUpCommand.Execute().FirstAsync();
        Assert.AreEqual("/home/user", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Refresh_RelistsCurrentDirectory()
    {
        List<RemoteFileInfo> firstList = CreateTestFiles();
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(firstList));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();
        Assert.HasCount(4, _vm.Files); // ".." + 3 entries
        var secondList = new List<RemoteFileInfo> { firstList[0], firstList[1] };
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(secondList));
        await _vm.RefreshCommand.Execute().FirstAsync();
        Assert.HasCount(3, _vm.Files); // ".." + 2 entries
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow(0, "0 B")]
    [DataRow(500, "500.0 B")]
    [DataRow(1230, "1.2 KB")]
    [DataRow(3565158, "3.4 MB")]
    [DataRow(1181116006, "1.1 GB")]
    public void FormatSize_ReturnsHumanReadable(long bytes, string expected) =>
        Assert.AreEqual(expected, RemoteFileInfoViewModel.FormatSize(bytes));

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void RemoteFileInfoViewModel_ExposesPermissions()
    {
        var fileInfo = new RemoteFileInfo
        {
            Name = "test.sh",
            FullPath = "/home/user/test.sh",
            Size = 256,
            Permissions = "-rwxr-xr-x",
            IsDirectory = false,
            LastModified = DateTime.UtcNow,
            Owner = "root",
            Group = "root",
        };
        var vm = new RemoteFileInfoViewModel(fileInfo);
        Assert.AreEqual("-rwxr-xr-x", vm.Permissions);
        Assert.IsFalse(vm.IsDirectory);
        Assert.AreEqual("file", vm.Icon);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void ShowTransfers_ReopensTransferToast()
    {
        var toast = new FileTransferViewModel(Substitute.For<ITransferManager>());
        _vm.TransferSink = toast;
        Assert.IsFalse(toast.IsPanelVisible);
        _vm.ShowTransfersCommand.Execute().Subscribe();
        Assert.IsTrue(toast.IsPanelVisible);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void ToggleVisibility_TogglesIsVisible()
    {
        Assert.IsFalse(_vm.IsVisible);
        _vm.ToggleVisibilityCommand.Execute().Subscribe();
        Assert.IsTrue(_vm.IsVisible);
        _vm.ToggleVisibilityCommand.Execute().Subscribe();
        Assert.IsFalse(_vm.IsVisible);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task ErrorHandling_SetsErrorMessage()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/forbidden", Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                Task.FromException<List<RemoteFileInfo>>(
                    new UnauthorizedAccessException("Permission denied")
                )
            );
        Exception? thrownEx = null;
        _vm.NavigateToCommand.ThrownExceptions.Subscribe(ex => thrownEx = ex);
        await _vm.NavigateToCommand.Execute("/forbidden").FirstAsync();
        Assert.IsFalse(string.IsNullOrEmpty(_vm.ErrorMessage));
        Assert.Contains("Permission denied", _vm.ErrorMessage);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void RemoteFileInfoViewModel_DirectoryShowsSizeAndPlainName()
    {
        var dirInfo = new RemoteFileInfo
        {
            Name = "docs",
            FullPath = "/home/user/docs",
            Size = 4096,
            Permissions = "drwxr-xr-x",
            IsDirectory = true,
            LastModified = DateTime.UtcNow,
            Owner = "user",
            Group = "user",
        };
        var vm = new RemoteFileInfoViewModel(dirInfo);

        // Directories list their reported size; the name is shown plainly (no trailing slash),
        // since the folder icon already distinguishes directories.
        Assert.AreEqual("4.0 KB", vm.FormattedSize);
        Assert.AreEqual("docs", vm.DisplayName);
        Assert.IsTrue(vm.IsDirectory);
        Assert.IsTrue(vm.IsRegularDirectory);
        Assert.AreEqual("folder", vm.Icon);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task GoUp_AtRoot_StaysAtRoot()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/";
        await _vm.GoUpCommand.Execute().FirstAsync();
        Assert.AreEqual("/", _vm.CurrentPath);
    }

    // --- Right-click context-menu actions ---

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NewFolder_PromptsAndCreatesUnderCurrentPath()
    {
        _vm.CurrentPath = "/home/user";
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("docs");
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        await _vm.NewFolderCommand.Execute().FirstAsync();
        await _sftpService
            .Received(1)
            .CreateDirectoryAsync(_sessionId, "/home/user/docs", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NewFolder_Cancelled_DoesNothing()
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>(null);
        await _vm.NewFolderCommand.Execute().FirstAsync();
        await _sftpService
            .DidNotReceive()
            .CreateDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape")]
    [DataRow("../escape")]
    [DataRow("nested/name")]
    [DataRow("nested\\name")]
    public async Task NewFolder_RejectsUnsafeLeafNameBeforeSftp(string name)
    {
        _vm.CurrentPath = "/home/user";
        _vm.PromptForText = (_, _) => Task.FromResult<string?>(name);

        await _vm.NewFolderCommand.Execute().FirstAsync();

        Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), _vm.ErrorMessage);
        await _sftpService
            .DidNotReceive()
            .CreateDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NewFile_PromptsAndCreatesUnderCurrentPath()
    {
        _vm.CurrentPath = "/home/user";
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("notes.txt");
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        await _vm.NewFileCommand.Execute().FirstAsync();
        await _sftpService
            .Received(1)
            .CreateFileAsync(_sessionId, "/home/user/notes.txt", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape")]
    [DataRow("../escape")]
    [DataRow("nested/name")]
    [DataRow("nested\\name")]
    public async Task NewFile_RejectsUnsafeLeafNameBeforeSftp(string name)
    {
        _vm.CurrentPath = "/home/user";
        _vm.PromptForText = (_, _) => Task.FromResult<string?>(name);

        await _vm.NewFileCommand.Execute().FirstAsync();

        Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), _vm.ErrorMessage);
        await _sftpService
            .DidNotReceive()
            .CreateFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Rename_RenamesWithinParentDirectory()
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("renamed.txt");
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.RenameCommand.Execute(file).FirstAsync();
        await _sftpService
            .Received(1)
            .RenameAsync(
                _sessionId,
                "/home/user/readme.txt",
                "/home/user/renamed.txt",
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape")]
    [DataRow("../escape")]
    [DataRow("nested/name")]
    [DataRow("nested\\name")]
    public async Task Rename_RejectsUnsafeLeafNameBeforeSftp(string name)
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>(name);
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]);

        await _vm.RenameCommand.Execute(file).FirstAsync();

        Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), _vm.ErrorMessage);
        await _sftpService
            .DidNotReceive()
            .RenameAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Move_RenamesToDestinationPath()
    {
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("/tmp/moved.txt");
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.MoveCommand.Execute(file).FirstAsync();
        await _sftpService
            .Received(1)
            .RenameAsync(
                _sessionId,
                "/home/user/readme.txt",
                "/tmp/moved.txt",
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Move_WithSeveralSelected_AsksOnceForAFolderAndMovesThemAllIntoIt()
    {
        // 框选一片再"移动到":逐个问完整路径没法用,只问一次目标目录,各自按原名落进去。
        int prompts = 0;
        _vm.PromptForText = (_, _) =>
        {
            prompts++;
            return Task.FromResult<string?>("/srv/archive");
        };
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _vm.SelectedFiles.Add(new(files[1])); // readme.txt
        _vm.SelectedFiles.Add(new(files[2]));

        await _vm.MoveCommand.Execute(new(files[1])).FirstAsync();

        Assert.AreEqual(1, prompts, "批量移动只该问一次目标目录。");
        await _sftpService.Received(1).RenameAsync(
            _sessionId, files[1].FullPath, $"/srv/archive/{files[1].Name}", Arg.Any<CancellationToken>());
        await _sftpService.Received(1).RenameAsync(
            _sessionId, files[2].FullPath, $"/srv/archive/{files[2].Name}", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Move_WhenOneItemFails_StillMovesTheRestAndReportsTheFailure()
    {
        // 批量半途中断比"跑完再报告"更难收拾:一条失败不该把剩下的也拦住。
        _vm.PromptForText = (_, _) => Task.FromResult<string?>("/srv/archive");
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _sftpService
            .RenameAsync(_sessionId, files[1].FullPath, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("permission denied")));
        _vm.SelectedFiles.Add(new(files[1]));
        _vm.SelectedFiles.Add(new(files[2]));

        await _vm.MoveCommand.Execute(new(files[1])).FirstAsync();

        // 失败的那条不该挡住后面的。
        await _sftpService.Received(1).RenameAsync(
            _sessionId, files[2].FullPath, $"/srv/archive/{files[2].Name}", Arg.Any<CancellationToken>());
        Assert.IsNotNull(_vm.ErrorMessage);
        Assert.Contains("permission denied", _vm.ErrorMessage);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Move_WithASingleItem_KeepsTheFullPathPromptSoRenamingStillWorks()
    {
        // n=1 时保留老用法:提示的是完整目标路径,可以顺手改名。
        string? prompted = null;
        _vm.PromptForText = (_, initial) =>
        {
            prompted = initial;
            return Task.FromResult<string?>("/tmp/renamed.txt");
        };
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _vm.SelectedFiles.Add(new(files[1]));

        await _vm.MoveCommand.Execute(new(files[1])).FirstAsync();

        Assert.AreEqual(files[1].FullPath, prompted, "单个移动应预填完整路径,而不是目录。");
        await _sftpService.Received(1).RenameAsync(
            _sessionId, files[1].FullPath, "/tmp/renamed.txt", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task CopyTo_WithSeveralSelected_AsksOnceAndCopiesEachIntoTheFolder()
    {
        int prompts = 0;
        _vm.PromptForText = (_, _) =>
        {
            prompts++;
            return Task.FromResult<string?>("/srv/backup");
        };
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _vm.SelectedFiles.Add(new(files[1]));
        _vm.SelectedFiles.Add(new(files[2]));

        await _vm.CopyToCommand.Execute(new(files[1])).FirstAsync();

        Assert.AreEqual(1, prompts, "批量复制只该问一次目标目录。");
        await _sftpService.Received(1).CopyAsync(
            _sessionId, files[1].FullPath, $"/srv/backup/{files[1].Name}",
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>());
        await _sftpService.Received(1).CopyAsync(
            _sessionId, files[2].FullPath, $"/srv/backup/{files[2].Name}",
            Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task CopyPath_And_CopyName_WriteToClipboard()
    {
        string? copied = null;
        _vm.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // readme.txt
        await _vm.CopyPathCommand.Execute(file).FirstAsync();
        Assert.AreEqual("/home/user/readme.txt", copied);
        await _vm.CopyNameCommand.Execute(file).FirstAsync();
        Assert.AreEqual("readme.txt", copied);
    }

    /// <summary>
    /// 空白区右键的「复制当前文件夹路径」不需要选中任何条目 —— 它取的是 <c>CurrentPath</c>。
    /// 没有它,想拿当前目录的路径就得先退回上级、再右键那个文件夹。
    /// </summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task CopyCurrentPath_CopiesCurrentDirectoryWithoutSelection()
    {
        string? copied = null;
        _vm.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        _vm.CurrentPath = "/root/nginx-backup-before-ubuntu-package";
        _vm.SelectedFiles.Clear();

        await _vm.CopyCurrentPathCommand.Execute().FirstAsync();

        Assert.AreEqual("/root/nginx-backup-before-ubuntu-package", copied);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_InvokesViewCallback()
    {
        RemoteFileInfoViewModel? shown = null;
        _vm.ShowFileProperties = f =>
        {
            shown = f;
            return Task.FromResult<short?>(null);
        };
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[0]);
        await _vm.PropertiesCommand.Execute(file).FirstAsync();
        Assert.AreSame(file, shown);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadItem_OnFile_DownloadsToChosenPath()
    {
        _vm.PickSavePathForDownload = _ => Task.FromResult<string?>("C:/local/readme.txt");
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.DownloadItemCommand.Execute(file).FirstAsync();
        await _sftpService
            .Received(1)
            .DownloadFileAsync(
                _sessionId,
                "/home/user/readme.txt",
                "C:/local/readme.txt",
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape.txt")]
    [DataRow("../escape.txt")]
    [DataRow("nested/name.txt")]
    [DataRow("nested\\name.txt")]
    public async Task DownloadAndOpen_RejectsUnsafeRemoteLeafNameBeforeLocalTransfer(string name)
    {
        string? openedPath = null;
        _vm.OpenLocalFile = path =>
        {
            openedPath = path;
            return Task.CompletedTask;
        };
        _sftpService
            .DownloadFileAsync(
                _sessionId,
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(Task.CompletedTask);
        var file = new RemoteFileInfoViewModel(
            new()
            {
                Name = name,
                FullPath = "/home/user/" + name,
                Size = 1,
                IsDirectory = false,
                Permissions = "-rw-r--r--",
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user",
            }
        );

        await _vm.ActivateCommand.Execute(file).FirstAsync();

        Assert.IsNull(openedPath);
        await _sftpService
            .DidNotReceive()
            .DownloadFileAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task OpenWithDefaultEditor_RejectsUnsafeRemoteLeafNameBeforeExternalEdit()
    {
        try
        {
            _vm.GetDefaultEditorPath = () => Task.FromResult<string?>("not-a-real-editor");
            var file = new RemoteFileInfoViewModel(
                new()
                {
                    Name = "../escape.txt",
                    FullPath = "/home/user/../escape.txt",
                    Size = 1,
                    IsDirectory = false,
                    Permissions = "-rw-r--r--",
                    LastModified = DateTime.UtcNow,
                    Owner = "user",
                    Group = "user",
                }
            );

            await _vm.OpenWithDefaultEditorCommand.Execute(file).FirstAsync();

            Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), _vm.ErrorMessage);
            await _sftpService
                .DidNotReceive()
                .DownloadFileAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            ExternalEditSessionManager.CleanupAll();
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task OpenItem_RejectsUnsafeRemoteLeafNameBeforeSftp()
    {
        _vm.OpenInBuiltInEditor = (_, _, _) => Task.CompletedTask;
        var file = new RemoteFileInfoViewModel(
            new()
            {
                Name = "../escape.txt",
                FullPath = "/home/user/../escape.txt",
                Size = 1,
                IsDirectory = false,
                Permissions = "-rw-r--r--",
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user",
            }
        );

        await _vm.OpenItemCommand.Execute(file).FirstAsync();

        await _sftpService
            .DidNotReceive()
            .DownloadFileAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Delete_WhenConfirmed_DeletesItem()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.DeleteItemCommand.Execute(file).FirstAsync();
        await _sftpService
            .Received(1)
            .DeleteAsync(
                _sessionId,
                "/home/user/readme.txt",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Delete_WhenProgressReported_UpdatesDeleteProgressBarState()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        IProgress<SftpDeleteProgress>? capturedProgress = null;
        _sftpService
            .DeleteAsync(
                _sessionId,
                "/home/user/readme.txt",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async ci =>
            {
                capturedProgress = ci.ArgAt<IProgress<SftpDeleteProgress>?>(2);
                capturedProgress?.Report(new(2, 4, "/home/user/readme.txt"));
                await Task.CompletedTask;
            });
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]);
        await _vm.DeleteItemCommand.Execute(file).FirstAsync();
        Assert.IsNotNull(capturedProgress);
        Assert.IsFalse(_vm.IsDeleteProgressVisible); // hidden again after completion
        Assert.AreEqual(0d, _vm.DeleteProgressPercent);
        Assert.IsFalse(_vm.IsDeleteProgressIndeterminate);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Delete_WhenDeclined_DoesNothing()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(false);
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]);
        await _vm.DeleteItemCommand.Execute(file).FirstAsync();
        await _sftpService
            .DidNotReceive()
            .DeleteAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadItem_OnDirectory_RecursivelyDownloadsIntoPickedFolder()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            _vm.PickFolderForDownload = () => Task.FromResult<string?>(tempRoot);
            _sftpService
                .ListDirectoryAsync(
                    _sessionId,
                    "/home/user/documents",
                    Arg.Any<CancellationToken>()
                )
                .Returns(
                    Task.FromResult(
                        new List<RemoteFileInfo>
                        {
                            new()
                            {
                                Name = "report.pdf",
                                FullPath = "/home/user/documents/report.pdf",
                                Size = 1,
                                Permissions = "-rw-r--r--",
                                IsDirectory = false,
                                LastModified = DateTime.UtcNow,
                                Owner = "user",
                                Group = "user",
                            },
                        }
                    )
                );
            var dir = new RemoteFileInfoViewModel(CreateTestFiles()[0]); // documents (directory)
            await _vm.DownloadItemCommand.Execute(dir).FirstAsync();
            Assert.IsTrue(Directory.Exists(Path.Combine(tempRoot, "documents")));
            await _sftpService
                .Received(1)
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/documents/report.pdf",
                    Path.Combine(tempRoot, "documents", "report.pdf"),
                    Arg.Any<IProgress<TransferProgress>?>(),
                   cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadItem_OnDirectory_RejectsUnsafeChildNameBeforeCreatingOrTransferring()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            _vm.PickFolderForDownload = () => Task.FromResult<string?>(tempRoot);
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(
                        new List<RemoteFileInfo>
                        {
                            new()
                            {
                                Name = "../escape.txt",
                                FullPath = "/home/user/documents/../escape.txt",
                                Size = 1,
                                IsDirectory = false,
                                Permissions = "-rw-r--r--",
                                LastModified = DateTime.UtcNow,
                                Owner = "user",
                                Group = "user",
                            },
                        }
                    )
                );
            var dir = new RemoteFileInfoViewModel(CreateTestFiles()[0]);

            await _vm.DownloadItemCommand.Execute(dir).FirstAsync();

            Assert.IsFalse(File.Exists(Path.Combine(tempRoot, "escape.txt")));
            await _sftpService
                .DidNotReceive()
                .DownloadFileAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadItem_OnDirectory_RejectsExistingSymlinkChildBeforeTransfer()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-download-{Guid.NewGuid():N}");
        string localDirectory = Path.Combine(tempRoot, "documents");
        string outside = Path.Combine(Path.GetTempPath(), $"vela-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localDirectory);
        Directory.CreateDirectory(outside);
        string link = Path.Combine(localDirectory, "linked");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
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

            _vm.PickFolderForDownload = () => Task.FromResult<string?>(tempRoot);
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(
                        new List<RemoteFileInfo>
                        {
                            new()
                            {
                                Name = "linked",
                                FullPath = "/home/user/documents/linked",
                                Size = 4096,
                                IsDirectory = true,
                                Permissions = "drwxr-xr-x",
                                LastModified = DateTime.UtcNow,
                                Owner = "user",
                                Group = "user",
                            },
                        }
                    )
                );
            var dir = new RemoteFileInfoViewModel(CreateTestFiles()[0]);

            await _vm.DownloadItemCommand.Execute(dir).FirstAsync();

            Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), _vm.ErrorMessage);
            await _sftpService
                .DidNotReceive()
                .DownloadFileAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            await _sftpService
                .DidNotReceive()
                .ListDirectoryAsync(
                    _sessionId,
                    "/home/user/documents/linked",
                    Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task HiddenFiles_AreFilteredOut_UntilToggledOn()
    {
        List<RemoteFileInfo> files = CreateTestFiles();
        files.Add(
            new()
            {
                Name = ".bashrc",
                FullPath = "/home/user/.bashrc",
                Size = 100,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user",
            }
        );
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(files));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();
        Assert.DoesNotContain(f => f.Name == ".bashrc", _vm.Files); // hidden by default
        _vm.ToggleHiddenFilesCommand.Execute().Subscribe();
        Assert.IsTrue(_vm.ShowHiddenFiles);
        Assert.Contains(f => f.Name == ".bashrc", _vm.Files); // re-filtered without a re-list
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Activate_OnParentEntry_NavigatesUp()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();
        RemoteFileInfoViewModel parentRow = _vm.Files[0];
        Assert.IsTrue(parentRow.IsParentEntry);
        await _vm.ActivateCommand.Execute(parentRow).FirstAsync();
        Assert.AreEqual("/home", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task RootDirectory_HasNoParentEntry()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        await _vm.NavigateToCommand.Execute("/").FirstAsync();
        Assert.DoesNotContain(f => f.IsParentEntry, _vm.Files);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task NavigateFailure_PreservesPreviousPathAndRows()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        await _vm.NavigateToCommand.Execute("/home").FirstAsync();
        string[] previousRows = [.. _vm.Files.Select(file => file.DisplayName)];
        _sftpService
            .ListDirectoryAsync(_sessionId, "/denied", Arg.Any<CancellationToken>())
            .Returns<Task<List<RemoteFileInfo>>>(_ => throw new IOException("denied"));

        await _vm.NavigateToCommand.Execute("/denied").FirstAsync();

        Assert.AreEqual("/home", _vm.CurrentPath);
        Assert.AreSequenceEqual(previousRows, [.. _vm.Files.Select(file => file.DisplayName)]);
        Assert.AreEqual("denied", _vm.ErrorMessage);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task RefreshCurrentDirectory_PreservesSelectionAndDoesNotRaiseDirectoryChanged()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        int changes = 0;
        _vm.DirectoryChanged += (_, _) => changes++;
        await _vm.NavigateToCommand.Execute("/home").FirstAsync();
        RemoteFileInfoViewModel selected = _vm.Files.First(file => !file.IsParentEntry);
        _vm.SelectedFiles.Add(selected);

        await _vm.RefreshCommand.Execute().FirstAsync();

        Assert.AreEqual(1, changes);
        Assert.ContainsSingle(file => file.FullPath == selected.FullPath, _vm.SelectedFiles);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task RefreshCurrentDirectory_WhenUnchanged_DoesNotResetCollection()
    {
        List<RemoteFileInfo> files = CreateTestFiles();
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(files));
        await _vm.NavigateToCommand.Execute("/home").FirstAsync();
        int collectionChanges = 0;
        _vm.Files.CollectionChanged += (_, _) => collectionChanges++;

        await _vm.RefreshCommand.Execute().FirstAsync();

        Assert.AreEqual(0, collectionChanges);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task ConcurrentNavigation_LatestResultWins()
    {
        var slow = new TaskCompletionSource<List<RemoteFileInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var fast = new TaskCompletionSource<List<RemoteFileInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _sftpService
            .ListDirectoryAsync(_sessionId, "/slow", Arg.Any<CancellationToken>())
            .Returns(slow.Task);
        _sftpService
            .ListDirectoryAsync(_sessionId, "/fast", Arg.Any<CancellationToken>())
            .Returns(fast.Task);
        using IDisposable slowSubscription = _vm.NavigateToCommand.Execute("/slow").Subscribe();
        using IDisposable fastSubscription = _vm.NavigateToCommand.Execute("/fast").Subscribe();

        fast.SetResult([]);
        await Task.Delay(20);
        slow.SetResult(CreateTestFiles());
        await Task.Delay(20);

        Assert.AreEqual("/fast", _vm.CurrentPath);
        Assert.IsFalse(_vm.IsDirectoryLoading);
    }

    // 回归:静默刷新(切回标签时的后台对账)绝不能盖过用户随后发起的导航。
    // 否则会出现"列表显示旧目录、面包屑却是新目录"——因行路径是绝对路径,
    // 后续删除/下载会作用到错误的文件。
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task RefreshSilently_DoesNotOverwriteNewerNavigation()
    {
        // 初始定位到 /home/user(快速返回)。
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateTestFiles()));
        await _vm.NavigateToCommand.Execute("/home/user").FirstAsync();

        // 让针对 /home/user 的下一次列举变慢:模拟静默刷新在途、尚未返回。
        var slowRefresh = new TaskCompletionSource<List<RemoteFileInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(slowRefresh.Task);
        // 子目录内容(用户随后导航进入),与刷新的过期内容明显不同。
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new List<RemoteFileInfo>
                    {
                        new()
                        {
                            Name = "subfile.txt",
                            FullPath = "/home/user/documents/subfile.txt",
                            Size = 1,
                            Permissions = "-rw-r--r--",
                            IsDirectory = false,
                            LastModified = DateTime.UtcNow,
                            Owner = "user",
                            Group = "user",
                        },
                    }
                )
            );

        // 静默刷新开始并阻塞在慢列举上。
        Task refresh = _vm.RefreshSilentlyAsync();

        // 用户导航进入子目录(快速完成并应用)。
        await _vm.NavigateToCommand.Execute("/home/user/documents").FirstAsync();
        Assert.AreEqual("/home/user/documents", _vm.CurrentPath);

        // 慢刷新此刻才返回,内容是过期的 /home/user 目录(含 stale.txt)。
        slowRefresh.SetResult(
            [
                new()
                {
                    Name = "stale.txt",
                    FullPath = "/home/user/stale.txt",
                    Size = 1,
                    Permissions = "-rw-r--r--",
                    IsDirectory = false,
                    LastModified = DateTime.UtcNow,
                    Owner = "user",
                    Group = "user",
                },
            ]
        );
        await refresh;

        // 过期刷新不得覆盖较新的导航:仍停在 /home/user/documents,列表是子目录内容。
        Assert.AreEqual("/home/user/documents", _vm.CurrentPath);
        Assert.Contains(file => file.Name == "subfile.txt", _vm.Files);
        Assert.DoesNotContain(file => file.Name == "stale.txt", _vm.Files);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void Breadcrumbs_SplitCurrentPathIntoClickableSegments()
    {
        _vm.CurrentPath = "/home/user/documents";
        IReadOnlyList<BreadcrumbSegment> crumbs = _vm.Breadcrumbs;
        Assert.HasCount(3, crumbs);
        Assert.AreEqual("home", crumbs[0].Name);
        Assert.AreEqual("/home", crumbs[0].Path);
        Assert.AreEqual("user", crumbs[1].Name);
        Assert.AreEqual("/home/user", crumbs[1].Path);
        Assert.AreEqual("documents", crumbs[2].Name);
        Assert.AreEqual("/home/user/documents", crumbs[2].Path);
    }

    // 属性弹窗已合并 chmod(参考 WinSCP):ShowFileProperties 返回变更后的 mode,null = 取消/未改。
    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_WithChangedMode_AppliesChmod()
    {
        _vm.ShowFileProperties = _ => Task.FromResult<short?>(755);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]); // /home/user/readme.txt
        await _vm.PropertiesCommand.Execute(file).FirstAsync();
        await _sftpService
            .Received(1)
            .SetPermissionsAsync(
                _sessionId,
                "/home/user/readme.txt",
                755,
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task Properties_CancelledOrUnchanged_DoesNotChmod()
    {
        _vm.ShowFileProperties = _ => Task.FromResult<short?>(null);
        var file = new RemoteFileInfoViewModel(CreateTestFiles()[1]);
        await _vm.PropertiesCommand.Execute(file).FirstAsync();
        await _sftpService
            .DidNotReceive()
            .SetPermissionsAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<short>(),
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DeleteSelected_ConfirmsOnceAndDeletesAll()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _vm.SelectedFiles.Add(new(files[1])); // readme.txt
        _vm.SelectedFiles.Add(new(files[2])); // photo.jpg
        await _vm.DeleteSelectedCommand.Execute().FirstAsync();
        await _sftpService
            .Received(1)
            .DeleteAsync(
                _sessionId,
                "/home/user/readme.txt",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
        await _sftpService
            .Received(1)
            .DeleteAsync(
                _sessionId,
                "/home/user/photo.jpg",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DeleteSelected_SkipsParentEntry()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.SelectedFiles.Add(RemoteFileInfoViewModel.CreateParentEntry("/home"));
        await _vm.DeleteSelectedCommand.Execute().FirstAsync();
        await _sftpService
            .DidNotReceive()
            .DeleteAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
    }

    // PrepareDragOut 测试已随拖出下载功能一并移除(2ef75bf:仅保留右键菜单下载)。

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadSelected_WhenCancelled_StopsRemainingFiles_WithoutError()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            _vm.PickFolderForDownload = () => Task.FromResult<string?>(tempRoot);
            // 顺序传输语义(设置 → 文件传输 → 最大并发 = 1):并发 >1 时第二个文件可能已在飞行中。
            _vm.TransferOptions.MaxConcurrentTransfers = 1;
            List<RemoteFileInfo> files = CreateTestFiles();
            _vm.SelectedFiles.Add(new(files[1])); // readme.txt (first)
            _vm.SelectedFiles.Add(new(files[2])); // photo.jpg (should be skipped)

            // The first file's transfer is cancelled mid-flight.
            _sftpService
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/readme.txt",
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                )
                .Returns(_ => throw new OperationCanceledException());
            await _vm.DownloadSelectedCommand.Execute().FirstAsync();

            // The batch stops on cancellation: the second file is never attempted...
            await _sftpService
                .DidNotReceive()
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/photo.jpg",
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            // ...and cancellation is not surfaced as an error.
            Assert.IsTrue(string.IsNullOrEmpty(_vm.ErrorMessage));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task CancelDelete_StopsRemainingDeletes_AndKeepsThemListed()
    {
        _vm.ConfirmDelete = _ => Task.FromResult(true);
        _sftpService
            .ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        List<RemoteFileInfo> files = CreateTestFiles();
        _vm.SelectedFiles.Add(new(files[1])); // readme.txt (first)
        _vm.SelectedFiles.Add(new(files[2])); // photo.jpg (should be skipped)

        // The user hits cancel while the first delete is running.
        _sftpService
            .DeleteAsync(
                _sessionId,
                "/home/user/readme.txt",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(ci =>
            {
                CancellationToken token = ci.ArgAt<CancellationToken>(3);
                _vm.CancelDeleteCommand.Execute().Subscribe();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        await _vm.DeleteSelectedCommand.Execute().FirstAsync();
        await _sftpService
            .DidNotReceive()
            .DeleteAsync(
                _sessionId,
                "/home/user/photo.jpg",
                Arg.Any<IProgress<SftpDeleteProgress>?>(),
                Arg.Any<CancellationToken>()
            );
        Assert.IsTrue(string.IsNullOrEmpty(_vm.ErrorMessage)); // cancellation is not an error
        Assert.IsFalse(_vm.IsDeleteProgressVisible); // overlay dismissed afterwards
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task DownloadSelected_DownloadsAllFilesIntoPickedFolder()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            _vm.PickFolderForDownload = () => Task.FromResult<string?>(tempRoot);
            List<RemoteFileInfo> files = CreateTestFiles();
            _vm.SelectedFiles.Add(new(files[1])); // readme.txt
            _vm.SelectedFiles.Add(new(files[2])); // photo.jpg
            await _vm.DownloadSelectedCommand.Execute().FirstAsync();
            await _sftpService
                .Received(1)
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/readme.txt",
                    Path.Combine(tempRoot, "readme.txt"),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            await _sftpService
                .Received(1)
                .DownloadFileAsync(
                    _sessionId,
                    "/home/user/photo.jpg",
                    Path.Combine(tempRoot, "photo.jpg"),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task UploadCommand_MultiSelect_UploadsAllChosenFiles()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string fileA = Path.Combine(tempRoot, "a.txt");
            string fileB = Path.Combine(tempRoot, "b.log");
            await File.WriteAllTextAsync(fileA, "A");
            await File.WriteAllTextAsync(fileB, "B");
            _vm.CurrentPath = "/home/user";
            _vm.PickLocalPathsForUpload = () => Task.FromResult<IReadOnlyList<string>>([fileA, fileB]);
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<RemoteFileInfo>()));
            await _vm.UploadCommand.Execute().FirstAsync();
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    fileA,
                    "/home/user/a.txt",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    fileB,
                    "/home/user/b.log",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task UploadCommand_RecursivelyUploadsFolderTree()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"vela-folder-upload-{Guid.NewGuid():N}"
        );
        string folder = Path.Combine(tempRoot, "assets");
        string nested = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            string rootFile = Path.Combine(folder, "root.txt");
            string nestedFile = Path.Combine(nested, "child.txt");
            await File.WriteAllTextAsync(rootFile, "root");
            await File.WriteAllTextAsync(nestedFile, "child");
            _vm.CurrentPath = "/home/user";
            _vm.PickLocalPathsForUpload = () => Task.FromResult<IReadOnlyList<string>>([folder]);
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<RemoteFileInfo>()));
            await _vm.UploadCommand.Execute().FirstAsync();
            await _sftpService
                .Received(1)
                .EnsureDirectoryAsync(
                    _sessionId,
                    "/home/user/assets",
                    Arg.Any<CancellationToken>()
                );
            await _sftpService
                .Received(1)
                .EnsureDirectoryAsync(
                    _sessionId,
                    "/home/user/assets/nested",
                    Arg.Any<CancellationToken>()
                );
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    rootFile,
                    "/home/user/assets/root.txt",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    nestedFile,
                    "/home/user/assets/nested/child.txt",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task UploadCommand_MixedSelection_HandlesEachPathByItsOwnKind()
    {
        // 合并成一个上传入口的关键:一次选择里同时有文件和文件夹时,
        // 文件直接传,文件夹整棵递归 —— 不需要用户先想清楚"这次要点哪个菜单项"。
        string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-mixed-upload-{Guid.NewGuid():N}");
        string folder = Path.Combine(tempRoot, "assets");
        Directory.CreateDirectory(folder);
        try
        {
            string looseFile = Path.Combine(tempRoot, "notes.txt");
            string folderFile = Path.Combine(folder, "logo.png");
            await File.WriteAllTextAsync(looseFile, "notes");
            await File.WriteAllTextAsync(folderFile, "png");
            _vm.CurrentPath = "/home/user";
            _vm.PickLocalPathsForUpload = () => Task.FromResult<IReadOnlyList<string>>([looseFile, folder]);
            _sftpService
                .ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new List<RemoteFileInfo>()));

            await _vm.UploadCommand.Execute().FirstAsync();

            // 散落的文件:直接落在当前目录下。
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    looseFile,
                    "/home/user/notes.txt",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
            // 文件夹:建对应远端目录,内容按原结构传进去。
            await _sftpService
                .Received(1)
                .EnsureDirectoryAsync(_sessionId, "/home/user/assets", Arg.Any<CancellationToken>());
            await _sftpService
                .Received(1)
                .UploadFileAsync(
                    _sessionId,
                    folderFile,
                    "/home/user/assets/logo.png",
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>()
                );
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void NormalizeUploadRoots_DropsDuplicatesAndPathsAlreadyInsideAPickedFolder()
    {
        string root = Path.Combine("C:", "work");
        string nested = Path.Combine(root, "src", "main.cs");
        string sibling = Path.Combine("C:", "work-notes", "todo.txt");

        IReadOnlyList<string> kept = FileBrowserViewModel.NormalizeUploadRoots(
            [root, nested, root + Path.DirectorySeparatorChar, sibling]
        );

        // nested 已经被 root 包住,再单列一次就会把同一个文件传两遍、写同一个远端路径。
        Assert.AreSequenceEqual([root, sibling], [.. kept], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public void NormalizeUploadRoots_KeepsSiblingsWhoseNamesSharePrefix()
    {
        // "work" 不该把 "work-notes" 吞掉 —— 前缀判断必须落在目录分隔符上。
        string a = Path.Combine("C:", "work");
        string b = Path.Combine("C:", "work-notes");

        IReadOnlyList<string> kept = FileBrowserViewModel.NormalizeUploadRoots([a, b]);

        Assert.AreSequenceEqual([a, b], [.. kept], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    // ---- 手动输入路径(#226)-------------------------------------------------

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_NavigatesToTypedPath_AndClosesEditor()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/data/app", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/home/user";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        Assert.IsTrue(_vm.IsPathEditing);
        Assert.AreEqual("/home/user", _vm.PathEditText, "切入输入态时应预填当前路径。");

        _vm.PathEditText = "/data/app/";
        await _vm.CommitPathEditCommand.Execute().FirstAsync();

        Assert.AreEqual("/data/app", _vm.CurrentPath);
        Assert.IsFalse(_vm.IsPathEditing, "导航成功后应退回面包屑。");
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_KeepsEditorOpen_WhenTargetIsUnreadable()
    {
        // 输错路径 / 目标没权限是手动输入的常态:此时不能把输入框收掉,否则用户
        // 得从头再点一次才能改一个字母。
        _sftpService
            .ListDirectoryAsync(_sessionId, "/root/secret", Arg.Any<CancellationToken>())
            .Returns<Task<List<RemoteFileInfo>>>(_ => throw new UnauthorizedAccessException("denied"));
        _vm.CurrentPath = "/home/user";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        _vm.PathEditText = "/root/secret";
        await _vm.CommitPathEditCommand.Execute().FirstAsync();

        Assert.IsTrue(_vm.IsPathEditing, "导航失败应保持输入态,让用户就地改。");
        Assert.AreEqual("/home/user", _vm.CurrentPath, "失败不应改变当前目录。");
        Assert.IsFalse(string.IsNullOrEmpty(_vm.ErrorMessage));
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_ResolvesRelativeInputAgainstCurrentDirectory()
    {
        _sftpService
            .ListDirectoryAsync(_sessionId, "/home/user/logs", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/home/user";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        _vm.PathEditText = "logs";
        await _vm.CommitPathEditCommand.Execute().FirstAsync();

        Assert.AreEqual("/home/user/logs", _vm.CurrentPath);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_ExpandsTildeUsingRemoteHome()
    {
        // 报告 #226 的场景:根目录不可读,只能靠直接敲路径进目标目录。~ 展开要问服务端。
        _sftpService
            .GetWorkingDirectoryAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/data/data/com.termux/files/home"));
        _sftpService
            .ListDirectoryAsync(
                _sessionId,
                "/data/data/com.termux/files/home/storage",
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        _vm.CurrentPath = "/";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        _vm.PathEditText = "~/storage";
        await _vm.CommitPathEditCommand.Execute().FirstAsync();

        Assert.AreEqual("/data/data/com.termux/files/home/storage", _vm.CurrentPath);
        Assert.IsFalse(_vm.IsPathEditing);
    }

    [TestMethod]
    [TestCategory("FileBrowser")]
    public async Task PathEdit_CancelKeepsCurrentDirectory()
    {
        _vm.CurrentPath = "/home/user";

        await _vm.BeginPathEditCommand.Execute().FirstAsync();
        _vm.PathEditText = "/somewhere/else";
        await _vm.CancelPathEditCommand.Execute().FirstAsync();

        Assert.IsFalse(_vm.IsPathEditing);
        Assert.AreEqual("/home/user", _vm.CurrentPath);
        await _sftpService
            .DidNotReceive()
            .ListDirectoryAsync(_sessionId, "/somewhere/else", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 一次 OSC 7 都没收到过就打开「跟随终端目录」= 点了什么都不会发生,必须说清为什么。
    /// Windows 远端(cmd.exe/PowerShell)就是这种情况:那串钩子只对 POSIX shell 注入(#305)。
    /// </summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public void FollowTerminal_WithoutAnyReport_ExplainsWhyNothingHappens()
    {
        _vm.FollowTerminal = true;

        Assert.AreEqual(Strings.Get("Sftp_FollowTerminalNoReport"), _vm.ErrorMessage);
    }

    /// <summary>关掉开关就把这句提示收回去,别赖在面板顶上。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public void FollowTerminal_TurnedOff_RemovesTheHint()
    {
        _vm.FollowTerminal = true;
        _vm.FollowTerminal = false;

        Assert.IsNull(_vm.ErrorMessage);
    }

    /// <summary>终端有上报的会话不该看到这句提示 —— 它说的是"没收到过上报"。</summary>
    [TestMethod]
    [TestCategory("FileBrowser")]
    public void FollowTerminal_AfterAReport_ShowsNoHint()
    {
        _vm.OnTerminalWorkingDirectoryChanged("/srv/app");

        _vm.FollowTerminal = true;

        Assert.IsNull(_vm.ErrorMessage);
    }
}

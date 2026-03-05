using System.Reactive.Linq;
using FluentAssertions;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Core.Sftp;

namespace PulseTerm.App.Tests.ViewModels;

public class FileBrowserViewModelTests
{
    private readonly ISftpService _sftpService;
    private readonly Guid _sessionId;
    private readonly FileBrowserViewModel _vm;

    public FileBrowserViewModelTests()
    {
        _sftpService = Substitute.For<ISftpService>();
        _sessionId = Guid.NewGuid();
        _vm = new FileBrowserViewModel(_sftpService, _sessionId);
    }

    private static List<RemoteFileInfo> CreateTestFiles()
    {
        return new List<RemoteFileInfo>
        {
            new RemoteFileInfo
            {
                Name = "documents",
                FullPath = "/home/user/documents",
                Size = 4096,
                Permissions = "drwxr-xr-x",
                IsDirectory = true,
                LastModified = DateTime.UtcNow.AddHours(-1),
                Owner = "user",
                Group = "user"
            },
            new RemoteFileInfo
            {
                Name = "readme.txt",
                FullPath = "/home/user/readme.txt",
                Size = 1234,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow.AddDays(-2),
                Owner = "user",
                Group = "user"
            },
            new RemoteFileInfo
            {
                Name = "photo.jpg",
                FullPath = "/home/user/photo.jpg",
                Size = 3567890,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow.AddMinutes(-30),
                Owner = "user",
                Group = "user"
            }
        };
    }

    [Fact]
    [Trait("Category", "FileBrowser")]
    public async Task ListDirectory_PopulatesFilesCollection()
    {
        var testFiles = CreateTestFiles();
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(testFiles));

        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        _vm.Files.Should().HaveCount(3);
        _vm.Files[0].Name.Should().Be("documents");
        _vm.Files[1].Name.Should().Be("readme.txt");
        _vm.Files[2].Name.Should().Be("photo.jpg");
    }

    [Fact]
    [Trait("Category", "FileBrowser")]
    public async Task NavigateIntoFolder_UpdatesCurrentPath()
    {
        var rootFiles = CreateTestFiles();
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(rootFiles));

        var subFiles = new List<RemoteFileInfo>
        {
            new RemoteFileInfo
            {
                Name = "report.pdf",
                FullPath = "/home/user/documents/report.pdf",
                Size = 524288,
                Permissions = "-rw-r--r--",
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Owner = "user",
                Group = "user"
            }
        };
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user/documents", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subFiles));

        await _vm.NavigateToCommand.Execute("/home/user/documents").FirstAsync();

        _vm.CurrentPath.Should().Be("/home/user/documents");
        _vm.Files.Should().HaveCount(1);
        _vm.Files[0].Name.Should().Be("report.pdf");
    }

    [Fact]
    [Trait("Category", "FileBrowser")]
    public async Task GoUp_NavigatesToParentDirectory()
    {
        _sftpService.ListDirectoryAsync(_sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));

        _vm.CurrentPath = "/home/user/documents";
        await _vm.GoUpCommand.Execute().FirstAsync();

        _vm.CurrentPath.Should().Be("/home/user");
    }

    [Fact]
    [Trait("Category", "FileBrowser")]
    public async Task Refresh_RelistsCurrentDirectory()
    {
        var firstList = CreateTestFiles();
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(firstList));

        _vm.CurrentPath = "/home/user";
        await _vm.RefreshCommand.Execute().FirstAsync();

        _vm.Files.Should().HaveCount(3);

        var secondList = new List<RemoteFileInfo>
        {
            firstList[0],
            firstList[1]
        };
        _sftpService.ListDirectoryAsync(_sessionId, "/home/user", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(secondList));

        await _vm.RefreshCommand.Execute().FirstAsync();

        _vm.Files.Should().HaveCount(2);
    }

    [Theory]
    [Trait("Category", "FileBrowser")]
    [InlineData(0, "0 B")]
    [InlineData(500, "500.0 B")]
    [InlineData(1230, "1.2 KB")]
    [InlineData(3565158, "3.4 MB")]
    [InlineData(1181116006, "1.1 GB")]
    public void FormatSize_ReturnsHumanReadable(long bytes, string expected)
    {
        RemoteFileInfoViewModel.FormatSize(bytes).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "FileBrowser")]
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
            Group = "root"
        };

        var vm = new RemoteFileInfoViewModel(fileInfo);

        vm.Permissions.Should().Be("-rwxr-xr-x");
        vm.IsDirectory.Should().BeFalse();
        vm.Icon.Should().Be("file");
    }

    [Fact]
    [Trait("Category", "FileBrowser")]
    public void ToggleVisibility_TogglesIsVisible()
    {
        _vm.IsVisible.Should().BeFalse();

        _vm.ToggleVisibilityCommand.Execute().Subscribe();

        _vm.IsVisible.Should().BeTrue();

        _vm.ToggleVisibilityCommand.Execute().Subscribe();

        _vm.IsVisible.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "FileBrowser")]
    public async Task ErrorHandling_SetsErrorMessage()
    {
        _sftpService.ListDirectoryAsync(_sessionId, "/forbidden", Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromException<List<RemoteFileInfo>>(new UnauthorizedAccessException("Permission denied")));

        Exception? thrownEx = null;
        _vm.NavigateToCommand.ThrownExceptions.Subscribe(ex => thrownEx = ex);

        await _vm.NavigateToCommand.Execute("/forbidden").FirstAsync();

        _vm.ErrorMessage.Should().NotBeNullOrEmpty();
        _vm.ErrorMessage.Should().Contain("Permission denied");
    }

    [Fact]
    [Trait("Category", "FileBrowser")]
    public void RemoteFileInfoViewModel_DirectoryShowsDash()
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
            Group = "user"
        };

        var vm = new RemoteFileInfoViewModel(dirInfo);

        vm.FormattedSize.Should().Be("--");
        vm.IsDirectory.Should().BeTrue();
        vm.Icon.Should().Be("folder");
    }

    [Fact]
    [Trait("Category", "FileBrowser")]
    public async Task GoUp_AtRoot_StaysAtRoot()
    {
        _sftpService.ListDirectoryAsync(_sessionId, "/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));

        _vm.CurrentPath = "/";
        await _vm.GoUpCommand.Execute().FirstAsync();

        _vm.CurrentPath.Should().Be("/");
    }
}

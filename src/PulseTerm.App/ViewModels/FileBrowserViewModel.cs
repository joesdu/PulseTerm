using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using PulseTerm.Core.Sftp;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class FileBrowserViewModel : ReactiveObject
{
    private readonly ISftpService _sftpService;
    private readonly Guid _sessionId;

    private string _currentPath;
    private bool _isLoading;
    private bool _isVisible;
    private string? _errorMessage;

    public FileBrowserViewModel(ISftpService sftpService, Guid sessionId)
    {
        _sftpService = sftpService ?? throw new ArgumentNullException(nameof(sftpService));
        _sessionId = sessionId;
        _currentPath = "/";

        Files = new ObservableCollection<RemoteFileInfoViewModel>();
        SelectedFiles = new ObservableCollection<RemoteFileInfoViewModel>();

        NavigateToCommand = ReactiveCommand.CreateFromTask<string>(NavigateToAsync);
        GoUpCommand = ReactiveCommand.CreateFromTask(GoUpAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        UploadCommand = ReactiveCommand.CreateFromTask(UploadAsync);
        DownloadCommand = ReactiveCommand.CreateFromTask(DownloadAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
        CreateFolderCommand = ReactiveCommand.CreateFromTask(CreateFolderAsync);
        ToggleVisibilityCommand = ReactiveCommand.Create(ToggleVisibility);
    }

    public ObservableCollection<RemoteFileInfoViewModel> Files { get; }

    public ObservableCollection<RemoteFileInfoViewModel> SelectedFiles { get; }

    public string CurrentPath
    {
        get => _currentPath;
        set => this.RaiseAndSetIfChanged(ref _currentPath, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public ReactiveCommand<string, Unit> NavigateToCommand { get; }
    public ReactiveCommand<Unit, Unit> GoUpCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> UploadCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVisibilityCommand { get; }

    public string[] PathSegments => CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private async Task NavigateToAsync(string path, CancellationToken ct = default)
    {
        try
        {
            ErrorMessage = null;
            IsLoading = true;
            CurrentPath = path;

            var files = await _sftpService.ListDirectoryAsync(_sessionId, path, ct);

            Files.Clear();
            foreach (var file in files)
            {
                Files.Add(new RemoteFileInfoViewModel(file));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task GoUpAsync(CancellationToken ct = default)
    {
        if (CurrentPath == "/") return;

        var parentIndex = CurrentPath.TrimEnd('/').LastIndexOf('/');
        var parentPath = parentIndex <= 0 ? "/" : CurrentPath.Substring(0, parentIndex);

        await NavigateToAsync(parentPath, ct);
    }

    private async Task RefreshAsync(CancellationToken ct = default)
    {
        await NavigateToAsync(CurrentPath, ct);
    }

    private Task UploadAsync(CancellationToken ct = default) => Task.CompletedTask;

    private Task DownloadAsync(CancellationToken ct = default) => Task.CompletedTask;

    private async Task DeleteAsync(CancellationToken ct = default)
    {
        try
        {
            ErrorMessage = null;
            var toDelete = SelectedFiles.ToList();

            foreach (var file in toDelete)
            {
                await _sftpService.DeleteAsync(_sessionId, file.FullPath, ct);
            }

            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task CreateFolderAsync(CancellationToken ct = default)
    {
        try
        {
            ErrorMessage = null;
            var newFolderPath = CurrentPath.TrimEnd('/') + "/New Folder";
            await _sftpService.CreateDirectoryAsync(_sessionId, newFolderPath, ct);
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void ToggleVisibility()
    {
        IsVisible = !IsVisible;
    }
}

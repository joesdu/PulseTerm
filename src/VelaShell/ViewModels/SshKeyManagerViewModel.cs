using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.ViewModels;

/// <summary>设置 - 密钥管理页(设计 UBP59):枚举/搜索/导入/生成/删除 ~/.ssh 密钥。</summary>
public class SshKeyManagerViewModel : ReactiveObject
{
    private readonly ISshKeyService? _keyService;

    /// <summary>构造密钥管理视图模型,注入密钥服务并初始化集合与命令。</summary>
    public SshKeyManagerViewModel(ISshKeyService? keyService = null)
    {
        _keyService = keyService;
        Keys = [];
        FilteredKeys = [];
        KeyNames = [];
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<SshKeyInfo>(DeleteAsync);
        GenerateCommand = ReactiveCommand.CreateFromTask(GenerateAsync);
        this.WhenAnyValue(x => x.SearchQuery).Subscribe(_ => ApplyFilter());
    }

    /// <summary>已枚举到的全部 ~/.ssh 密钥。</summary>
    public ObservableCollection<SshKeyInfo> Keys { get; }

    /// <summary>按搜索条件过滤后的密钥,供列表展示。</summary>
    public ObservableCollection<SshKeyInfo> FilteredKeys { get; }

    /// <summary>密钥名称列表,供“默认认证密钥”下拉。</summary>
    public ObservableCollection<string> KeyNames { get; }

    /// <summary>密钥搜索关键字,变更时触发过滤。</summary>
    public string SearchQuery
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>操作结果状态提示文案。</summary>
    public string StatusMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>是否正在执行异步密钥操作(刷新/生成等)。</summary>
    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>重新枚举密钥列表的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>删除指定密钥的命令。</summary>
    public ReactiveCommand<SshKeyInfo, RxVoid> DeleteCommand { get; }

    /// <summary>生成新 RSA 密钥的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> GenerateCommand { get; }

    /// <summary>从密钥服务枚举密钥并刷新列表与下拉名单。</summary>
    public async Task RefreshAsync()
    {
        if (_keyService is null)
        {
            return;
        }
        IsBusy = true;
        try
        {
            List<SshKeyInfo> keys = await _keyService.ListKeysAsync();
            Keys.Clear();
            foreach (SshKeyInfo key in keys)
            {
                Keys.Add(key);
            }

            // 名单没变就不动 KeyNames:它是“默认认证密钥”下拉的 ItemsSource,
            // Clear 的瞬间 ComboBox 会把选中项清空并经 TwoWay 把 null 写回设置模型,
            // 已选择的默认密钥会被无谓抹掉。
            if (!keys.Select(k => k.Name).SequenceEqual(KeyNames))
            {
                KeyNames.Clear();
                foreach (SshKeyInfo key in keys)
                {
                    KeyNames.Add(key.Name);
                }
            }
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("Msg_ReadKeysFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>从指定路径导入密钥并刷新列表。</summary>
    public async Task ImportAsync(string sourcePath)
    {
        if (_keyService is null)
        {
            return;
        }
        try
        {
            SshKeyInfo? imported = await _keyService.ImportKeyAsync(sourcePath);
            StatusMessage = imported is null ? Strings.Get("Msg_KeyAlreadyExists") : Strings.Format("Msg_KeyImported", imported.Name);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("Msg_ImportFailed", ex.Message);
        }
    }

    private async Task GenerateAsync()
    {
        if (_keyService is null)
        {
            return;
        }
        IsBusy = true;
        try
        {
            // 自动挑选未占用的名称 velashell_rsa[, _2, _3…]。
            const string baseName = "velashell_rsa";
            string name = baseName;
            for (int i = 2; Keys.Any(k => k.Name == name); i++)
            {
                name = $"{baseName}_{i}";
            }
            SshKeyInfo generated = await _keyService.GenerateRsaKeyAsync(name);
            StatusMessage = Strings.Format("Msg_KeyGenerated", generated.Name, generated.Type);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("Msg_GenerateFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync(SshKeyInfo key)
    {
        if (_keyService is null)
        {
            return;
        }
        try
        {
            await _keyService.DeleteKeyAsync(key.Name);
            StatusMessage = Strings.Format("Msg_KeyDeleted", key.Name);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("Msg_DeleteFailed", ex.Message);
        }
    }

    private void ApplyFilter()
    {
        FilteredKeys.Clear();
        string query = SearchQuery.Trim();
        foreach (SshKeyInfo key in Keys)
        {
            if (query.Length == 0 || key.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || key.Type.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredKeys.Add(key);
            }
        }
    }
}

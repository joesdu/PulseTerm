using System.Collections.ObjectModel;

namespace VelaShell.Plugin.S3.Ui;

/// <summary>左侧导航里的一项。</summary>
public sealed class S3SectionViewModel : ObservableObject
{
    /// <summary>该项对应的桶配置;非配置页(概览/版本/分片上传)为 null。</summary>
    public S3ConfigKind? Kind { get; init; }

    /// <summary>非配置页的标识。</summary>
    public S3SectionRole Role { get; init; } = S3SectionRole.Configuration;

    /// <summary>显示名称。</summary>
    public required string Title { get; init; }

    /// <summary>该项当前是否被选中(用于高亮)。</summary>
    public bool IsSelected
    {
        get;
        set => SetProperty(ref field, value);
    }
}

/// <summary>非配置类页面的角色。</summary>
public enum S3SectionRole
{
    /// <summary>某一项桶配置。</summary>
    Configuration,

    /// <summary>桶概览。</summary>
    Overview,

    /// <summary>对象版本浏览。</summary>
    Versions,

    /// <summary>未完成的分片上传。</summary>
    MultipartUploads,
}

/// <summary>版本列表里的一行。</summary>
/// <param name="Version">版本数据。</param>
/// <param name="Localization">
/// 文案表。行是 namespace 级的独立 record,取不到 <see cref="S3ManagerViewModel" /> 的实例字段,
/// 因此由构造方递进来(参数名刻意不叫 Loc —— 与类型同名会撞上 Color-Color 规则)。
/// </param>
public sealed record S3VersionRowViewModel(S3ObjectVersion Version, Loc Localization)
{
    /// <summary>对象键。</summary>
    public string Key => Version.Key;

    /// <summary>版本标识;没有时显示 <c>null</c>(那正是未启用版本控制时 S3 的表示法)。</summary>
    public string VersionId => Version.VersionId ?? "null";

    /// <summary>大小(字节);删除标记显示为空。</summary>
    public string Size => Version.IsDeleteMarker ? string.Empty : Version.Size.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>最后修改时间。</summary>
    public string LastModified => Version.LastModified == DateTime.MinValue ? string.Empty : Version.LastModified.ToString("u", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>是否为当前版本。</summary>
    public bool IsLatest => Version.IsLatest;

    /// <summary>是否为删除标记(删掉它即可恢复被删除的对象)。</summary>
    public bool IsDeleteMarker => Version.IsDeleteMarker;

    /// <summary>状态标签。</summary>
    public string StatusText => Version.IsDeleteMarker
        ? Localization.Get("S3Ver_DeleteMarker")
        : Version.IsLatest ? Localization.Get("S3Ver_Latest") : Localization.Get("S3Ver_Historical");
}

/// <summary>未完成分片上传列表里的一行。</summary>
/// <param name="Upload">上传数据。</param>
public sealed record S3UploadRowViewModel(S3MultipartUpload Upload)
{
    /// <summary>目标对象键。</summary>
    public string Key => Upload.Key;

    /// <summary>分片上传标识。</summary>
    public string UploadId => Upload.UploadId;

    /// <summary>发起时间。</summary>
    public string Initiated => Upload.Initiated == DateTime.MinValue ? string.Empty : Upload.Initiated.ToString("u", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>发起者。</summary>
    public string Owner => Upload.OwnerDisplayName;
}

/// <summary>
/// S3 桶管理器的视图模型:左侧是全部可管理项,右侧按项的类型呈现
/// 结构化表单 / JSON 文档编辑器 / 专用列表。
/// <para>
/// 左侧导航由 <see cref="S3ConfigDescriptor.All" /> 驱动,因此**协议里有的配置这里就有**;
/// 新增一种配置不需要改这个类。
/// </para>
/// </summary>
public sealed class S3ManagerViewModel : ObservableObject
{
    private readonly IS3ManagementService _management;
    private readonly Loc _loc;
    private readonly Guid _sessionId;

    /// <summary>创建视图模型。</summary>
    /// <param name="management">S3 管理服务。</param>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">要管理的桶。</param>
    /// <param name="loc">插件文案表。</param>
    public S3ManagerViewModel(IS3ManagementService management, Guid sessionId, string bucket, Loc loc)
    {
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _sessionId = sessionId;
        Bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
        Title = _loc.Format("S3Mgr_Title", bucket);

        Sections =
        [
            new() { Role = S3SectionRole.Overview, Title = _loc.Get("S3Mgr_Overview") },
            new() { Role = S3SectionRole.Versions, Title = _loc.Get("S3Mgr_Versions") },
            new() { Role = S3SectionRole.MultipartUploads, Title = _loc.Get("S3Mgr_MultipartUploads") },
            .. S3ConfigDescriptor.All.Select(d => new S3SectionViewModel
            {
                Kind = d.Kind,
                Role = S3SectionRole.Configuration,
                Title = _loc.Get(d.ResourceKey),
            }),
        ];

        ReloadCommand = new AsyncCommand(LoadSelectedAsync);
        SaveCommand = new AsyncCommand(SaveAsync);
        DeleteCommand = new AsyncCommand(DeleteAsync);
        AddTagRowCommand = new AsyncCommand<S3FormFieldViewModel>(row => { row.AddTag(); return Task.CompletedTask; });
        AbortUploadCommand = new AsyncCommand<S3UploadRowViewModel>(AbortUploadAsync);
        DeleteVersionCommand = new AsyncCommand<S3VersionRowViewModel>(DeleteVersionAsync);
        RestoreVersionCommand = new AsyncCommand<S3VersionRowViewModel>(RestoreVersionAsync);
        _selectedSection = Sections[0];
        _selectedSection.IsSelected = true;
    }

    /// <summary>正在管理的桶。</summary>
    public string Bucket { get; }

    /// <summary>窗口标题。</summary>
    public string Title { get; }

    /// <summary>左侧导航项。</summary>
    public ObservableCollection<S3SectionViewModel> Sections { get; }

    /// <summary>按 id 分多份的配置的可选 id(清单/分析/指标/智能分层)。</summary>
    public ObservableCollection<string> ConfigIds { get; } = [];

    /// <summary>当前表单字段。</summary>
    public ObservableCollection<S3FormFieldViewModel> Fields { get; } = [];

    /// <summary>版本列表。</summary>
    public ObservableCollection<S3VersionRowViewModel> Versions { get; } = [];

    /// <summary>未完成的分片上传。</summary>
    public ObservableCollection<S3UploadRowViewModel> Uploads { get; } = [];

    /// <summary>重新加载当前项。</summary>
    public AsyncCommand ReloadCommand { get; }

    /// <summary>保存当前项。</summary>
    public AsyncCommand SaveCommand { get; }

    /// <summary>删除当前项的配置。</summary>
    public AsyncCommand DeleteCommand { get; }

    /// <summary>给键值列表加一行。</summary>
    public AsyncCommand<S3FormFieldViewModel> AddTagRowCommand { get; }

    /// <summary>中止一次分片上传。</summary>
    public AsyncCommand<S3UploadRowViewModel> AbortUploadCommand { get; }

    /// <summary>永久删除一个版本。</summary>
    public AsyncCommand<S3VersionRowViewModel> DeleteVersionCommand { get; }

    /// <summary>把一个历史版本恢复为当前版本。</summary>
    public AsyncCommand<S3VersionRowViewModel> RestoreVersionCommand { get; }

    private S3SectionViewModel _selectedSection;

    /// <summary>当前选中的导航项。</summary>
    public S3SectionViewModel SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedSection))
            {
                return;
            }
            _selectedSection.IsSelected = false;
            SetProperty(ref _selectedSection, value);
            _selectedSection.IsSelected = true;
            RaiseLayoutChanged();
            // 换一节 = 换一代。之前那一代的续体回来时会自行作废,
            // 否则快速点 清单 → 分析 会把两节的 id 混进同一个集合,
            // 再拿甲节的 id 去查乙节的配置。
            Interlocked.Increment(ref _loadGeneration);
            _ = LoadSelectedAsync();
        }
    }

    /// <summary>按 id 分多份的配置里当前选中的 id。</summary>
    public string? SelectedConfigId
    {
        get;
        set
        {
            // 只在**值真的变了**时才加载。用 value 判断会让"切走再切回同一个 id"重复发请求,
            // 而 LoadConfigIdsThenConfigAsync 里的赋值也会经过这里 —— 两处叠加就是每次切换两发 GET,
            // 两次 RunAsync 的忙态/错误态还会互相覆盖。
            string? previous = field;
            SetProperty(ref field, value);
            if (value is not null && !string.Equals(previous, value, StringComparison.Ordinal))
            {
                _ = LoadConfigAsync();
            }
        }
    }

    /// <summary>JSON 文档编辑器的内容。</summary>
    public string Json
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>用于版本浏览的键前缀过滤。</summary>
    public string VersionPrefix
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>状态/错误提示。</summary>
    public string StatusMessage
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>提示是否为错误(决定配色)。</summary>
    public bool IsError
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>是否正在加载。</summary>
    public bool IsBusy
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>桶概览文本。</summary>
    public string OverviewText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    // ---- 界面分支判据 -------------------------------------------------------

    /// <summary>当前是否显示概览页。</summary>
    public bool ShowOverview => SelectedSection.Role == S3SectionRole.Overview;

    /// <summary>当前是否显示版本页。</summary>
    public bool ShowVersions => SelectedSection.Role == S3SectionRole.Versions;

    /// <summary>当前是否显示分片上传页。</summary>
    public bool ShowUploads => SelectedSection.Role == S3SectionRole.MultipartUploads;

    /// <summary>当前是否显示结构化表单。</summary>
    public bool ShowForm => SelectedSection is { Role: S3SectionRole.Configuration, Kind: { } kind } && S3ConfigForm.HasForm(kind);

    /// <summary>当前是否显示 JSON 编辑器。</summary>
    public bool ShowJson => SelectedSection is { Role: S3SectionRole.Configuration, Kind: { } kind } && !S3ConfigForm.HasForm(kind);

    /// <summary>当前项是否可保存。</summary>
    public bool CanSave => SelectedSection.Role == S3SectionRole.Configuration;

    /// <summary>当前项是否可删除。</summary>
    public bool CanDelete => SelectedSection is { Role: S3SectionRole.Configuration, Kind: { } kind } && S3ConfigDescriptor.For(kind).SupportsDelete;

    /// <summary>当前项是否按 id 分多份。</summary>
    public bool IsKeyed => SelectedSection is { Role: S3SectionRole.Configuration, Kind: { } kind } && S3ConfigDescriptor.For(kind).IsKeyed;

    /// <summary>首次打开时加载。</summary>
    public Task InitializeAsync() => LoadSelectedAsync();

    // ---- 加载与保存 ---------------------------------------------------------

    private async Task LoadSelectedAsync()
    {
        switch (SelectedSection.Role)
        {
            case S3SectionRole.Overview:
                await LoadOverviewAsync().ConfigureAwait(true);
                break;
            case S3SectionRole.Versions:
                await LoadVersionsAsync().ConfigureAwait(true);
                break;
            case S3SectionRole.MultipartUploads:
                await LoadUploadsAsync().ConfigureAwait(true);
                break;
            default:
                await LoadConfigIdsThenConfigAsync().ConfigureAwait(true);
                break;
        }
    }

    private async Task LoadOverviewAsync() =>
        await RunAsync(async () =>
        {
            S3BucketOverview overview = await _management.GetBucketOverviewAsync(_sessionId, Bucket).ConfigureAwait(true);
            OverviewText = string.Join(Environment.NewLine,
                $"{_loc.Get("S3Ovw_Bucket")}: {overview.Name}",
                $"{_loc.Get("S3Ovw_Region")}: {overview.Region}",
                $"{_loc.Get("S3Ovw_Versioning")}: {overview.VersioningStatus}",
                $"{_loc.Get("S3Ovw_ObjectLock")}: {(overview.ObjectLockEnabled ? _loc.Get("S3Ovw_Enabled") : _loc.Get("S3Ovw_Disabled"))}",
                $"{_loc.Get("S3Ovw_PublicAccess")}: {DescribePublic(overview.IsPublic)}");
            return string.Empty;
        }).ConfigureAwait(true);

    private string DescribePublic(bool? isPublic) =>
        isPublic switch
        {
            true => _loc.Get("S3Ovw_Public"),
            false => _loc.Get("S3Ovw_NotPublic"),
            _ => _loc.Get("S3Ovw_Unknown"),
        };

    private async Task LoadVersionsAsync() =>
        await RunAsync(ReloadVersionsCoreAsync).ConfigureAwait(true);

    /// <summary>
    /// 版本列表的重载核心,**不带** RunAsync 外壳。恢复版本那条路要在自己的外壳里调它,
    /// 套两层会让内层的失败被外层的成功文案盖掉。
    /// </summary>
    private async Task<string> ReloadVersionsCoreAsync()
    {
        int generation = CurrentGeneration;
        IReadOnlyList<S3ObjectVersion> versions = await _management
            .ListObjectVersionsAsync(_sessionId, Bucket, VersionPrefix.Trim())
            .ConfigureAwait(true);
        if (generation != CurrentGeneration)
        {
            return string.Empty; // 已经切到别的导航项,这一代的结果作废
        }
        Versions.Clear();
        foreach (S3ObjectVersion version in versions)
        {
            Versions.Add(new(version, _loc));
        }
        return versions.Count == 0 ? _loc.Get("S3Msg_NoVersions") : string.Empty;
    }

    private async Task LoadUploadsAsync() =>
        await RunAsync(async () =>
        {
            int generation = CurrentGeneration;
            IReadOnlyList<S3MultipartUpload> uploads = await _management
                .ListMultipartUploadsAsync(_sessionId, Bucket)
                .ConfigureAwait(true);
            if (generation != CurrentGeneration)
            {
                return string.Empty;
            }
            Uploads.Clear();
            foreach (S3MultipartUpload upload in uploads)
            {
                Uploads.Add(new(upload));
            }
            return uploads.Count == 0 ? _loc.Get("S3Msg_NoUploads") : _loc.Format("S3Msg_UploadsFound", uploads.Count);
        }).ConfigureAwait(true);

    private async Task LoadConfigIdsThenConfigAsync()
    {
        ConfigIds.Clear();
        if (IsKeyed && SelectedSection.Kind is { } keyed)
        {
            // 必须包进 RunAsync:ListBucketConfigIdsAsync 只吞"没配过/不支持",
            // 403、凭据错误、超时一律往外抛。而本方法的两个调用点都是 fire-and-forget
            // (SelectedSection 的 setter 与视图构造里的 InitializeAsync),
            // 不包住的话异常直接变成未观察的 Task 异常:右侧空白、没有任何提示。
            bool loaded = false;
            await RunAsync(async () =>
            {
                int generation = CurrentGeneration;
                IReadOnlyList<string> ids = await _management.ListBucketConfigIdsAsync(_sessionId, Bucket, keyed).ConfigureAwait(true);
                if (generation != CurrentGeneration)
                {
                    return string.Empty; // 用户已经切到别的导航项,这一代的 id 不能往集合里灌
                }
                // 再清一次:开头那次 Clear 发生在第一个 await 之前,期间可能有另一代填过内容。
                ConfigIds.Clear();
                foreach (string id in ids)
                {
                    ConfigIds.Add(id);
                }
                loaded = true;
                return ids.Count == 0 ? _loc.Get("S3Msg_NoNamedConfigs") : string.Empty;
            }).ConfigureAwait(true);
            if (!loaded || ConfigIds.Count == 0)
            {
                Fields.Clear();
                Json = string.Empty;
                return;
            }
            // 先清空再赋值,保证"重进同一个 section、id 与上次相同"时 setter 仍认为值变了并加载一次。
            SelectedConfigId = null;
            SelectedConfigId = ConfigIds[0];
            return; // 上一行的 setter 已经触发加载,这里不能再发一次。
        }
        await LoadConfigAsync().ConfigureAwait(true);
    }

    private async Task LoadConfigAsync()
    {
        if (SelectedSection.Kind is not { } kind)
        {
            return;
        }
        await RunAsync(async () =>
        {
            int generation = CurrentGeneration;
            S3ConfigResult result = await _management
                .GetBucketConfigAsync(_sessionId, Bucket, kind, SelectedConfigId)
                .ConfigureAwait(true);
            if (generation != CurrentGeneration)
            {
                return string.Empty; // 迟到的结果不能盖掉当前导航项的内容
            }
            Json = result.Json;
            Fields.Clear();
            if (S3ConfigForm.HasForm(kind))
            {
                foreach (S3FormFieldViewModel field in S3ConfigForm.Build(kind, result.Json, _loc))
                {
                    Fields.Add(field);
                }
            }
            // 「没配过」与「不支持」是空状态,不是错误 —— 用提示条说明而不是弹红字。
            if (!result.Supported)
            {
                return _loc.Get("S3Msg_NotSupported");
            }
            return result.Exists ? string.Empty : _loc.Get("S3Msg_NotConfigured");
        }).ConfigureAwait(true);
    }

    private async Task SaveAsync()
    {
        if (SelectedSection.Kind is not { } kind)
        {
            return;
        }
        await RunAsync(async () =>
        {
            string payload = S3ConfigForm.HasForm(kind) ? S3ConfigForm.Apply(kind, Json, [.. Fields]) : Json;
            await _management.PutBucketConfigAsync(_sessionId, Bucket, kind, payload, SelectedConfigId).ConfigureAwait(true);
            Json = payload;
            return _loc.Get("S3Msg_Saved");
        }).ConfigureAwait(true);
    }

    private async Task DeleteAsync()
    {
        if (SelectedSection.Kind is not { } kind || !CanDelete)
        {
            return;
        }
        await RunAsync(async () =>
        {
            await _management.DeleteBucketConfigAsync(_sessionId, Bucket, kind, SelectedConfigId).ConfigureAwait(true);
            Json = string.Empty;
            Fields.Clear();
            foreach (S3FormFieldViewModel field in S3ConfigForm.Build(kind, string.Empty, _loc))
            {
                Fields.Add(field);
            }
            return _loc.Get("S3Msg_Deleted");
        }).ConfigureAwait(true);
    }

    private async Task AbortUploadAsync(S3UploadRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }
        await RunAsync(async () =>
        {
            await _management.AbortMultipartUploadAsync(_sessionId, Bucket, row.Key, row.UploadId).ConfigureAwait(true);
            Uploads.Remove(row);
            return _loc.Get("S3Msg_UploadAborted");
        }).ConfigureAwait(true);
    }

    private async Task DeleteVersionAsync(S3VersionRowViewModel? row)
    {
        if (row?.Version.VersionId is not { Length: > 0 } versionId)
        {
            return;
        }
        await RunAsync(async () =>
        {
            await _management.DeleteObjectVersionAsync(_sessionId, Bucket, row.Key, versionId).ConfigureAwait(true);
            Versions.Remove(row);
            return _loc.Get("S3Msg_VersionDeleted");
        }).ConfigureAwait(true);
    }

    private async Task RestoreVersionAsync(S3VersionRowViewModel? row)
    {
        if (row?.Version.VersionId is not { Length: > 0 } versionId)
        {
            return;
        }
        await RunAsync(async () =>
        {
            await _management.RestoreObjectVersionAsync(_sessionId, Bucket, row.Key, versionId).ConfigureAwait(true);
            // 调不带 RunAsync 外壳的那一版:套两层的话,内层把 IsError 置 true 之后,
            // 外层仍会用自己的返回值把 StatusMessage 覆盖成「版本已恢复」——
            // 刷新失败被伪装成完全成功,而列表还停在恢复前的快照上。
            await ReloadVersionsCoreAsync().ConfigureAwait(true);
            return _loc.Get("S3Msg_VersionRestored");
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 统一的「忙 → 执行 → 报结果」外壳。任何一次失败都只落在提示条上,
    /// 绝不让异常冒出去把窗口带崩 —— 桶管理里的每一项都可能因为权限或实现差异而失败,
    /// 那是常态不是事故。
    /// </summary>
    private async Task RunAsync(Func<Task<string>> action)
    {
        try
        {
            IsBusy = true;
            IsError = false;
            StatusMessage = string.Empty;
            StatusMessage = await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsError = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 加载代次。两处属性 setter 都是 fire-and-forget 地起加载(AsyncCommand 的互斥
    /// 覆盖不到 setter),续体必须自证「我这一代还算数」才能往集合和属性里写。
    /// </summary>
    private int _loadGeneration;

    /// <summary>当前代次(续体在每个 await 之后、写任何状态之前比对它)。</summary>
    private int CurrentGeneration => Volatile.Read(ref _loadGeneration);

    private void RaiseLayoutChanged()
    {
        RaisePropertyChanged(nameof(ShowOverview));
        RaisePropertyChanged(nameof(ShowVersions));
        RaisePropertyChanged(nameof(ShowUploads));
        RaisePropertyChanged(nameof(ShowForm));
        RaisePropertyChanged(nameof(ShowJson));
        RaisePropertyChanged(nameof(CanSave));
        RaisePropertyChanged(nameof(CanDelete));
        RaisePropertyChanged(nameof(IsKeyed));
    }
}

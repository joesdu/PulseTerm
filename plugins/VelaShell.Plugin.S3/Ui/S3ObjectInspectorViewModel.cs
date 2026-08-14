using System.Collections.ObjectModel;
using System.Globalization;

namespace VelaShell.Plugin.S3.Ui;

/// <summary>
/// 单个 S3 对象的检视器:详情、标签、权限(ACL)、保留与合法保留、存储类别与加密、
/// 归档取回、S3 Select 查询、预签名分享链接。
/// <para>
/// 这些能力都**没有** SFTP/FTP 的对应物,因此放不进协议无关的文件浏览器契约里;
/// 它们经 <see cref="IS3ManagementService" /> 独立成一扇窗,由文件浏览器的右键菜单唤起。
/// </para>
/// </summary>
public sealed class S3ObjectInspectorViewModel : ObservableObject
{
    private readonly IS3ManagementService _management;
    private readonly Loc _loc;
    private readonly Guid _sessionId;

    /// <summary>创建视图模型。</summary>
    /// <param name="management">S3 管理服务。</param>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="bucket">对象所在的桶。</param>
    /// <param name="key">对象键。</param>
    /// <param name="copyToClipboard">把文本写入剪贴板的回调(由视图注入)。</param>
    /// <param name="loc">插件文案表。</param>
    public S3ObjectInspectorViewModel(
        IS3ManagementService management,
        Guid sessionId,
        string bucket,
        string key,
        Func<string, Task>? copyToClipboard,
        Loc loc)
    {
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _sessionId = sessionId;
        Bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Title = _loc.Format("S3Obj_Title", key);
        CopyToClipboard = copyToClipboard;

        StorageClasses =
        [
            "STANDARD", "STANDARD_IA", "ONEZONE_IA", "INTELLIGENT_TIERING",
            "GLACIER_IR", "GLACIER", "DEEP_ARCHIVE", "REDUCED_REDUNDANCY",
        ];
        CannedAcls = ["private", "public-read", "public-read-write", "authenticated-read", "bucket-owner-read", "bucket-owner-full-control"];
        RestoreTiers = ["Standard", "Expedited", "Bulk"];
        SelectInputFormats = ["CSV", "JSON", "Parquet"];

        ReloadCommand = new AsyncCommand(LoadAsync);
        SaveTagsCommand = new AsyncCommand(SaveTagsAsync);
        AddTagCommand = new AsyncCommand(() => { Tags.Add(new()); return Task.CompletedTask; });
        RemoveTagCommand = new AsyncCommand<S3TagRowViewModel>(row => { Tags.Remove(row); return Task.CompletedTask; });
        ApplyAclCommand = new AsyncCommand(ApplyAclAsync);
        ApplyStorageClassCommand = new AsyncCommand(ApplyStorageClassAsync);
        ApplyRetentionCommand = new AsyncCommand(ApplyRetentionAsync);
        RequestRestoreCommand = new AsyncCommand(RequestRestoreAsync);
        RunSelectCommand = new AsyncCommand(RunSelectAsync);
        CopyPresignedCommand = new AsyncCommand(CopyPresignedAsync);
    }

    /// <summary>对象所在的桶。</summary>
    public string Bucket { get; }

    /// <summary>对象键。</summary>
    public string Key { get; }

    /// <summary>窗口标题。</summary>
    public string Title { get; }

    /// <summary>由视图注入的剪贴板写入回调。</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>可选的存储类别。</summary>
    public ObservableCollection<string> StorageClasses { get; }

    /// <summary>可选的预置 ACL。</summary>
    public ObservableCollection<string> CannedAcls { get; }

    /// <summary>可选的取回速度。</summary>
    public ObservableCollection<string> RestoreTiers { get; }

    /// <summary>S3 Select 的输入格式。</summary>
    public ObservableCollection<string> SelectInputFormats { get; }

    /// <summary>对象标签。</summary>
    public ObservableCollection<S3TagRowViewModel> Tags { get; } = [];

    /// <summary>重新加载。</summary>
    public AsyncCommand ReloadCommand { get; }

    /// <summary>保存标签。</summary>
    public AsyncCommand SaveTagsCommand { get; }

    /// <summary>新增一行标签。</summary>
    public AsyncCommand AddTagCommand { get; }

    /// <summary>删除一行标签。</summary>
    public AsyncCommand<S3TagRowViewModel> RemoveTagCommand { get; }

    /// <summary>应用预置 ACL。</summary>
    public AsyncCommand ApplyAclCommand { get; }

    /// <summary>更改存储类别。</summary>
    public AsyncCommand ApplyStorageClassCommand { get; }

    /// <summary>应用保留策略与合法保留。</summary>
    public AsyncCommand ApplyRetentionCommand { get; }

    /// <summary>请求归档取回。</summary>
    public AsyncCommand RequestRestoreCommand { get; }

    /// <summary>执行 S3 Select 查询。</summary>
    public AsyncCommand RunSelectCommand { get; }

    /// <summary>复制预签名分享链接。</summary>
    public AsyncCommand CopyPresignedCommand { get; }

    /// <summary>详情文本。</summary>
    public string DetailsText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>ACL 的 JSON 表示(只读展示)。</summary>
    public string AclJson
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>选中的预置 ACL。</summary>
    public string SelectedCannedAcl
    {
        get;
        // 拒绝 null 回写:ComboBox 在当前值不在候选列表里时会把 SelectedItem 推成 null,
        // 而这里是不可空的 string —— 不挡这一下,后面拿它去 Trim/比较就是 NRE。
        set => SetProperty(ref field, value ?? field);
    } = "private";

    /// <summary>选中的存储类别。</summary>
    public string SelectedStorageClass
    {
        get;
        // 拒绝 null 回写:ComboBox 在当前值不在候选列表里时会把 SelectedItem 推成 null,
        // 而这里是不可空的 string —— 不挡这一下,后面拿它去 Trim/比较就是 NRE。
        set => SetProperty(ref field, value ?? field);
    } = "STANDARD";

    /// <summary>保留模式(空 = 不设置)。</summary>
    public string RetentionMode
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>保留到期日(yyyy-MM-dd)。</summary>
    public string RetainUntil
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>是否处于合法保留。</summary>
    public bool LegalHold
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>取回副本保留天数。</summary>
    public string RestoreDays
    {
        get;
        set => SetProperty(ref field, value);
    } = "7";

    /// <summary>取回速度。</summary>
    public string SelectedRestoreTier
    {
        get;
        // 拒绝 null 回写:ComboBox 在当前值不在候选列表里时会把 SelectedItem 推成 null,
        // 而这里是不可空的 string —— 不挡这一下,后面拿它去 Trim/比较就是 NRE。
        set => SetProperty(ref field, value ?? field);
    } = "Standard";

    /// <summary>S3 Select 的 SQL 表达式。</summary>
    public string SelectExpression
    {
        get;
        set => SetProperty(ref field, value);
    } = "SELECT * FROM S3Object s LIMIT 100";

    /// <summary>S3 Select 的输入格式。</summary>
    public string SelectedSelectInput
    {
        get;
        // 拒绝 null 回写:ComboBox 在当前值不在候选列表里时会把 SelectedItem 推成 null,
        // 而这里是不可空的 string —— 不挡这一下,后面拿它去 Trim/比较就是 NRE。
        set => SetProperty(ref field, value ?? field);
    } = "CSV";

    /// <summary>CSV 输入首行是否为表头。</summary>
    public bool CsvHasHeader
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    /// <summary>S3 Select 的查询结果。</summary>
    public string SelectResult
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

    /// <summary>提示是否为错误。</summary>
    public bool IsError
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>是否忙。</summary>
    public bool IsBusy
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>首次打开时加载。</summary>
    public Task InitializeAsync() => LoadAsync();

    private async Task LoadAsync() =>
        await RunAsync(async () =>
        {
            S3ObjectDetails details = await _management.GetObjectDetailsAsync(_sessionId, Bucket, Key).ConfigureAwait(true);
            DetailsText = FormatDetails(details);
            SelectedStorageClass = details.StorageClass.Length > 0 ? details.StorageClass : "STANDARD";

            Tags.Clear();
            foreach (S3Tag tag in await _management.GetObjectTagsAsync(_sessionId, Bucket, Key).ConfigureAwait(true))
            {
                Tags.Add(new() { Key = tag.Key, Value = tag.Value });
            }

            AclJson = await _management.GetObjectAclAsync(_sessionId, Bucket, Key).ConfigureAwait(true);

            S3Retention retention = await _management.GetObjectRetentionAsync(_sessionId, Bucket, Key).ConfigureAwait(true);
            RetentionMode = retention.Mode;
            RetainUntil = retention.RetainUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
            LegalHold = await _management.GetObjectLegalHoldAsync(_sessionId, Bucket, Key).ConfigureAwait(true);
            return string.Empty;
        }).ConfigureAwait(true);

    private string FormatDetails(S3ObjectDetails details)
    {
        List<string> lines =
        [
            $"{_loc.Get("S3Ver_Key")}: {details.Key}",
            $"{_loc.Get("Size")}: {details.Size.ToString("N0", CultureInfo.CurrentCulture)}",
            $"{_loc.Get("Modified")}: {(details.LastModified == DateTime.MinValue ? string.Empty : details.LastModified.ToString("u", CultureInfo.CurrentCulture))}",
            $"ETag: {details.ETag}",
            $"{_loc.Get("S3Obj_StorageClass")}: {details.StorageClass}",
            $"Content-Type: {details.ContentType}",
        ];
        if (details.VersionId.Length > 0)
        {
            lines.Add($"{_loc.Get("S3Ver_VersionId")}: {details.VersionId}");
        }
        if (details.ServerSideEncryption.Length > 0)
        {
            lines.Add($"{_loc.Get("S3Cfg_Encryption")}: {details.ServerSideEncryption} {details.KmsKeyId}".TrimEnd());
        }
        if (details.Checksum.Length > 0)
        {
            lines.Add($"Checksum: {details.Checksum}");
        }
        if (details.PartCount > 0)
        {
            lines.Add($"Parts: {details.PartCount}");
        }
        if (details.RestoreStatus.Length > 0)
        {
            lines.Add($"{_loc.Get("S3Obj_Restore")}: {details.RestoreStatus}");
        }
        if (details.ExpiresOn is { } expiry)
        {
            lines.Add($"Expires: {expiry.ToString("u", CultureInfo.CurrentCulture)}");
        }
        foreach (S3Tag entry in details.Metadata)
        {
            lines.Add($"x-amz-meta-{entry.Key}: {entry.Value}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private async Task SaveTagsAsync() =>
        await RunAsync(async () =>
        {
            List<S3Tag> tags = [.. Tags.Where(t => t.Key.Trim().Length > 0).Select(t => new S3Tag(t.Key.Trim(), t.Value))];
            await _management.PutObjectTagsAsync(_sessionId, Bucket, Key, tags).ConfigureAwait(true);
            return _loc.Get("S3Obj_Applied");
        }).ConfigureAwait(true);

    private async Task ApplyAclAsync() =>
        await RunAsync(async () =>
        {
            await _management.PutObjectCannedAclAsync(_sessionId, Bucket, Key, SelectedCannedAcl).ConfigureAwait(true);
            AclJson = await _management.GetObjectAclAsync(_sessionId, Bucket, Key).ConfigureAwait(true);
            return _loc.Get("S3Obj_Applied");
        }).ConfigureAwait(true);

    private async Task ApplyStorageClassAsync() =>
        await RunAsync(async () =>
        {
            await _management.ChangeStorageClassAsync(_sessionId, Bucket, Key, SelectedStorageClass).ConfigureAwait(true);
            return _loc.Get("S3Obj_Applied");
        }).ConfigureAwait(true);

    private async Task ApplyRetentionAsync() =>
        await RunAsync(async () =>
        {
            await _management.PutObjectLegalHoldAsync(_sessionId, Bucket, Key, LegalHold).ConfigureAwait(true);
            if (RetentionMode.Trim().Length > 0)
            {
                DateTime? until = DateTime.TryParse(RetainUntil, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)
                    ? parsed
                    : null;
                await _management
                    .PutObjectRetentionAsync(_sessionId, Bucket, Key, new(RetentionMode.Trim(), until))
                    .ConfigureAwait(true);
            }
            return _loc.Get("S3Obj_Applied");
        }).ConfigureAwait(true);

    private async Task RequestRestoreAsync() =>
        await RunAsync(async () =>
        {
            int days = int.TryParse(RestoreDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 7;
            await _management
                .RestoreArchivedObjectAsync(_sessionId, Bucket, Key, new(days, SelectedRestoreTier))
                .ConfigureAwait(true);
            return _loc.Get("S3Obj_RestoreRequested");
        }).ConfigureAwait(true);

    private async Task RunSelectAsync() =>
        await RunAsync(async () =>
        {
            SelectResult = await _management.SelectObjectContentAsync(_sessionId, Bucket, Key,
                new(SelectExpression, SelectedSelectInput, "CSV", "NONE", CsvHasHeader)).ConfigureAwait(true);
            return string.Empty;
        }).ConfigureAwait(true);

    private async Task CopyPresignedAsync() =>
        await RunAsync(async () =>
        {
            string url = await _management
                .CreatePresignedUrlAsync(_sessionId, Bucket, Key, TimeSpan.FromDays(7))
                .ConfigureAwait(true);
            if (CopyToClipboard is { } copy)
            {
                await copy(url).ConfigureAwait(true);
            }
            // 链接本身自带凭据,不回显到界面上。
            return _loc.Get("S3_PresignedUrlCopied");
        }).ConfigureAwait(true);

    /// <summary>
    /// 统一的「忙 → 执行 → 报结果」外壳。对象级操作里失败是常态(权限、对象锁定、
    /// 服务端不支持),一律落在提示条上而不是让异常把窗口带崩。
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
}

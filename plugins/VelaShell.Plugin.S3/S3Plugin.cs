using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.S3;

/// <summary>
/// S3 兼容对象存储插件的入口。
/// <para>
/// 经 manifest 的 <c>onProtocol:velashell.s3</c> **惰性激活**:用户在连接配置页点到
/// S3 页签(或打开一条 S3 会话)才装载本程序集 —— 不用 S3 的人,连 AWSSDK 那两个
/// 程序集都不会进内存,启动路径零开销。
/// </para>
/// <para>
/// 激活时只做一件事:把协议注册进宿主。此后双栏浏览器、传输队列、限速、拖放、冲突策略
/// 全部由宿主原样复用,插件这边只实现 <see cref="IProtocolFileSystem" />。
/// </para>
/// </summary>
[VelaPlugin]
public sealed class S3Plugin : IVelaPlugin
{
    private IPluginContext? _context;
    private S3ProtocolFileSystem? _fileSystem;
    private S3ActionHandler? _actions;
    private IDisposable? _registration;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _actions = new(context);
        _fileSystem = new(context.Protocols, _actions);
        _actions.Attach(_fileSystem);
        _registration = context.Protocols.Register(BuildDescriptor(context), _fileSystem);
        // 语言切换后重注册:描述里的字段标签是插件自己的文案,宿主不会替我们翻。
        context.Events.LocaleChanged += _ => Reregister();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        _registration = null;
        if (_fileSystem is { } fileSystem)
        {
            // 会话里握着 HTTP 连接池与证书信任状态,必须显式收掉;
            // 留着不放,可收集 ALC 就回收不了这份程序集。
            await fileSystem.DisposeAsync().ConfigureAwait(false);
        }
        _fileSystem = null;
        _actions = null;
        _context = null;
    }

    private void Reregister()
    {
        if (_context is not { } context || _fileSystem is not { } fileSystem)
        {
            return;
        }
        // **先注册后释放**,顺序不能反:先 Dispose 会触发注册表的注销事件,
        // 宿主据此把该协议名下**全部活会话**当场关掉 —— 用户只是切了个界面语言,
        // 不该把正开着的 S3 标签页一起断掉。同 id 重注册按替换处理,替换不发注销事件;
        // 旧句柄随后 Dispose 时已不是表里那一份,是空操作。
        IDisposable next = context.Protocols.Register(BuildDescriptor(context), fileSystem);
        _registration?.Dispose();
        _registration = next;
    }

    /// <summary>
    /// 协议描述:页签、默认端口、连接表单与右键动作。宿主按这份声明渲染界面,
    /// 因此插件不需要写一行连接对话框的界面代码。
    /// </summary>
    private static ProtocolDescriptor BuildDescriptor(IPluginContext context)
    {
        var loc = new Loc(context.Host.Locale);
        return new()
        {
            Id = context.PluginId,
            DisplayName = "S3",
            DefaultPort = S3Settings.DefaultPort,
            HostLabel = loc["S3_Endpoint"],
            HostPlaceholder = "s3.amazonaws.com",
            UsernameLabel = loc["S3_AccessKeyId"],
            PasswordLabel = loc["S3_SecretAccessKey"],
            // 能力位决定宿主显示/隐藏哪些操作。**不含** Permissions(S3 没有 POSIX 权限位)
            // 与 ResumeUpload(S3 的写入是原子的,服务端不存在可续的半个对象)。
            Features = ProtocolFeatures.ServerSideCopy
                       | ProtocolFeatures.ResumeDownload
                       | ProtocolFeatures.AnonymousAccess
                       | ProtocolFeatures.CertificateTrust,
            TrustedThumbprintSettingKey = S3ProtocolFields.TrustedThumbprint,
            // 字段顺序即表单顺序。前四项决定「连不连得上」(区域、寻址方式、TLS、默认桶),
            // 其余是调优项 —— 标 IsAdvanced 收进宿主的「高级选项」里默认折叠:
            // 十来个字段一列铺开会把连接对话框顶出屏幕(用户反馈)。
            Fields =
            [
                new()
                {
                    Key = S3ProtocolFields.Region,
                    Label = loc["S3_Region"],
                    DefaultValue = S3Settings.DefaultRegion,
                    Placeholder = S3Settings.DefaultRegion,
                    Hint = loc["S3_RegionHint"],
                },
                new()
                {
                    Key = S3ProtocolFields.Addressing,
                    Label = loc["S3_Addressing"],
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "auto",
                    Hint = loc["S3_AddressingHint"],
                    Choices =
                    [
                        new("auto", loc["S3_AddressingAuto"]),
                        new("path", loc["S3_AddressingPath"]),
                        new("virtual", loc["S3_AddressingVirtual"]),
                    ],
                },
                new()
                {
                    Key = S3ProtocolFields.UseTls,
                    Label = loc["S3_UseTls"],
                    Kind = ProtocolSettingKind.Boolean,
                    DefaultValue = "true",
                    Hint = loc["S3_PlaintextWarn"],
                },
                new()
                {
                    Key = S3ProtocolFields.DefaultBucket,
                    Label = loc["S3_DefaultBucket"],
                    Placeholder = "my-bucket",
                    Hint = loc["S3_DefaultBucketHint"],
                },
                new()
                {
                    Key = S3ProtocolFields.SessionToken,
                    Label = loc["S3_SessionToken"],
                    Kind = ProtocolSettingKind.Password,
                    // 临时凭据同样是凭据:标成机密,宿主随口令一起加密落盘。
                    IsSecret = true,
                    // 只有 STS 临时凭据才填,长期密钥留空 —— 折叠;真填过的配置宿主会自动展开。
                    IsAdvanced = true,
                    Hint = loc["S3_SessionTokenHint"],
                },
                new()
                {
                    Key = S3ProtocolFields.PartSize,
                    Label = loc["S3_PartSize"],
                    Kind = ProtocolSettingKind.Integer,
                    IsAdvanced = true,
                    DefaultValue = S3Settings.DefaultPartSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                new()
                {
                    Key = S3ProtocolFields.Concurrency,
                    Label = loc["S3_Concurrency"],
                    Kind = ProtocolSettingKind.Integer,
                    IsAdvanced = true,
                    DefaultValue = "4",
                },
                new()
                {
                    Key = S3ProtocolFields.StorageClass,
                    Label = loc["S3_StorageClass"],
                    IsAdvanced = true,
                    Placeholder = "STANDARD",
                },
                new()
                {
                    Key = S3ProtocolFields.ServerSideEncryption,
                    Label = loc["S3_ServerSideEncryption"],
                    IsAdvanced = true,
                    Placeholder = "AES256",
                },
                new()
                {
                    Key = S3ProtocolFields.ShowFolderMarkers,
                    Label = loc["S3_ShowFolderMarkers"],
                    Kind = ProtocolSettingKind.Boolean,
                    IsAdvanced = true,
                    DefaultValue = "false",
                },
                // 隐藏字段:用户点了"信任该证书"之后由宿主写回,不出现在表单里。
                new()
                {
                    Key = S3ProtocolFields.TrustedThumbprint,
                    Label = "Trusted certificate thumbprint",
                    IsHidden = true,
                },
            ],
            Actions =
            [
                // 分享链接与对象检视只对文件有意义;桶管理在哪儿右键都成立
                // (根上是选中的那个桶,桶内是当前所在的桶)。
                new(S3Actions.CopyShareLink, loc["S3_PresignedUrl"], ProtocolActionScope.File),
                new(S3Actions.InspectObject, loc["S3Obj_Manage"], ProtocolActionScope.File),
                new(S3Actions.ManageBucket, loc["S3Obj_BucketManager"], ProtocolActionScope.Any),
            ],
        };
    }
}

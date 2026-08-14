using System.Collections.ObjectModel;
using System.Security;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.Presentation.Services;
using VelaShell.PluginSdk.Protocols;
using VelaShell.Security;

namespace VelaShell.ViewModels;

/// <summary>“会话分组”下拉的一项;Id 为 null 表示未分组。</summary>
public sealed record GroupOption(Guid? Id, string Name)
{
    /// <summary>下拉项以分组名称展示。</summary>
    public override string ToString() => Name;
}

/// <summary>新建/编辑连接配置对话框的视图模型:承载表单字段、认证方式切换、分组与跳板主机选择,并提供保存、连接、测试等命令。</summary>
public class ConnectionProfileViewModel : ReactiveObject, IDisposable
{
    /// <summary>
    /// “未分组”选项/输入的显示名;输入等于该名或留空即保存为未分组。
    /// 属性而非 static readonly:后者按类型加载时的语言冻结,换语言后新开的
    /// 对话框仍显示旧语言;VM 每次打开对话框新建,属性取值即当前语言。
    /// </summary>
    private static string UngroupedName => Strings.Get("Msg_Ungrouped");

    private readonly IConnectionWorkflowService? _connectionWorkflowService;

    private readonly Guid _profileId;
    private readonly ISessionRepository? _sessionRepository;
    private AuthMethod _authMethod = AuthMethod.Password;
    private ConnectionType _connectionType = ConnectionType.SSH;
    private FtpEncryptionMode _ftpEncryption = FtpEncryptionMode.Auto;
    private bool _ftpPassive = true;
    private bool _ftpAnonymous;
    private string? _ftpTrustedThumbprint;
    private int _ftpMaxConnections = new FtpSettings().MaxConnections;
    private Guid? _groupId;
    private string _host = string.Empty;
    private bool _isKeyAuth;
    private bool _isPasswordAuth = true;
    private Guid? _jumpHostProfileId;
    private string _name = string.Empty;
    private SecureString? _password;
    private int _port = 22;
    private string? _privateKeyPassphrase;
    private string? _privateKeyPath;
    private bool _rememberPassword = true;
    private GroupOption? _selectedGroup;
    private GroupOption? _selectedJumpHost;
    private string _tagsText = string.Empty;
    private string _username = string.Empty;

    // ---- 插件协议 ----
    private readonly PluginProtocolRegistry? _protocolRegistry;
    private string? _pluginProtocolId;
    private ProtocolDescriptor? _pluginDescriptor;

    /// <summary>编辑既有配置时读入的插件设置;表单还没渲染出来就保存的话原样带回。</summary>
    private Dictionary<string, string>? _pluginStored;
    private Dictionary<string, string>? _pluginStoredSecrets;

    /// <summary>
    /// 最近一次**真正渲染过表单**的协议 id。「换没换协议」必须以它为准而不是
    /// <see cref="PluginProtocolId" /> —— 后者会被切回内建协议时置 null。
    /// </summary>
    private string? _loadedProtocolId;

    /// <summary>创建视图模型;传入 <paramref name="existing" /> 时进入编辑模式回显字段,否则新建并应用默认端口/默认密钥路径。</summary>
    public ConnectionProfileViewModel(
        SessionProfile? existing = null,
        IConnectionWorkflowService? connectionWorkflowService = null,
        ISessionRepository? sessionRepository = null,
        int defaultPort = 22,
        string? defaultPrivateKeyPath = null,
        PluginProtocolRegistry? protocolRegistry = null)
    {
        _protocolRegistry = protocolRegistry;
        _connectionWorkflowService = connectionWorkflowService;
        _sessionRepository = sessionRepository;
        Groups = [new(null, UngroupedName)];
        _selectedGroup = Groups[0];
        JumpHostOptions = [new(null, Strings.Get("Msg_DirectConnection"))];
        _selectedJumpHost = JumpHostOptions[0];

        // 新建连接的默认值(设置 → 常规 → 连接默认值 / 密钥管理 → 默认认证密钥)。
        if (existing is null)
        {
            if (defaultPort is >= 1 and <= 65535)
            {
                _port = defaultPort;
            }
            if (!string.IsNullOrWhiteSpace(defaultPrivateKeyPath))
            {
                _privateKeyPath = defaultPrivateKeyPath;
            }
        }
        if (existing != null)
        {
            _profileId = existing.Id;
            _connectionType = existing.ConnectionType;
            _name = existing.Name;
            _host = existing.Host;
            _port = existing.Port;
            _username = existing.Username;
            _authMethod = existing.AuthMethod;
            _password = SecureStringConvert.FromPlaintext(existing.Password);
            _privateKeyPath = existing.PrivateKeyPath;
            _privateKeyPassphrase = existing.PrivateKeyPassphrase;
            _groupId = existing.GroupId;
            _isPasswordAuth = existing.AuthMethod == AuthMethod.Password;
            _isKeyAuth = existing.AuthMethod == AuthMethod.PrivateKey;
            _rememberPassword = existing.RememberPassword;
            _tagsText = string.Join(", ", existing.Tags);
            _jumpHostProfileId = existing.JumpHostProfileId;
            if (existing.Ftp is { } ftp)
            {
                _ftpEncryption = ftp.EncryptionMode;
                _ftpPassive = ftp.DataConnectionMode == FtpDataConnectionMode.Passive;
                _ftpAnonymous = ftp.Anonymous;
                _ftpTrustedThumbprint = ftp.TrustedCertificateThumbprint;
                _ftpMaxConnections = ftp.MaxConnections;
            }
            _pluginProtocolId = existing.PluginProtocolId;
            _pluginStored = existing.PluginSettings;
            _pluginStoredSecrets = existing.PluginSecrets;
        }
        else
        {
            _profileId = Guid.NewGuid();
        }
        this.WhenAnyValue(x => x.AuthMethod)
            .Subscribe(method =>
            {
                IsPasswordAuth = method == AuthMethod.Password;
                IsKeyAuth = method == AuthMethod.PrivateKey;
            });

        // Skip(1):WhenAnyValue 订阅时会立即用当前值(默认“未分组”/“直连”)触发一次,
        // 不跳过会把上面刚从 existing 读入的 _groupId/_jumpHostProfileId 冲成 null——
        // 表现为编辑窗口跳板机回显丢失、保存后配置被静默清掉(#1)。
        this.WhenAnyValue(x => x.SelectedGroup)
            .Skip(1)
            .Subscribe(option =>
            {
                _groupId = option?.Id;
                if (option is not null)
                {
                    GroupText = option.Name;
                }
            });
        this.WhenAnyValue(x => x.SelectedJumpHost)
            .Skip(1)
            .Subscribe(option => _jumpHostProfileId = option?.Id);
        // 「必须填用户名」这条并非对所有协议成立:FTP 匿名登录,以及声明了
        // AnonymousAccess 的插件协议(如 S3 的公开只读桶)都不需要。
        // 保存/连接按钮不能因为这个永远灰着(串口调研里踩过同一个坑)。
        IObservable<bool> canExecute = this.WhenAnyValue(x => x.Host,
            x => x.Username,
            x => x.Port,
            x => x.IsBusy,
            x => x.FtpAnonymous,
            x => x.ConnectionType,
            x => x.AllowsAnonymous,
            x => x.PluginUnavailable,
            (host, username, port, isBusy, anonymous, type, pluginAnonymous, pluginUnavailable) =>
                !isBusy &&
                !string.IsNullOrWhiteSpace(host) &&
                (!string.IsNullOrWhiteSpace(username) ||
                 (type == ConnectionType.FTP && anonymous) ||
                 // 插件不可用时也放行:否则一条「用户名为空的 S3 配置」在禁用插件后
                 // 保存/连接/测试三个按钮同时灰死,连改个名字都存不下去。
                 (type == ConnectionType.Plugin && (pluginAnonymous || pluginUnavailable))) &&
                port is >= 1 and <= 65535);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, canExecute);
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync, canExecute);
        CancelCommand = ReactiveCommand.Create(() => (SessionProfile?)null);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync, canExecute);
        SelectConnectionTypeCommand = ReactiveCommand.Create<ConnectionType>(SelectConnectionType);
        SelectPluginProtocolCommand = ReactiveCommand.CreateFromTask<string>(SelectPluginProtocolAsync);
        LoadPluginProtocols();
        // 页签不是一次性快照:插件发现跑在后台线程,对话框可能先于它打开;
        // 插件管理器又是非模态的,开着对话框也能启用/禁用插件。
        if (_protocolRegistry is not null)
        {
            _protocolRegistry.Changed += OnProtocolsChanged;
        }
        if (_connectionType == ConnectionType.Plugin && _pluginProtocolId is { Length: > 0 } existingProtocol)
        {
            // 编辑既有插件协议配置:进对话框就把表单渲染出来(会触发该插件的惰性激活)。
            _ = SelectPluginProtocolAsync(existingProtocol);
        }
        BrowseKeyFileCommand = ReactiveCommand.Create(() => { });
        ToggleAdvancedCommand = ReactiveCommand.Create(() => { IsAdvancedVisible = !IsAdvancedVisible; });
        TogglePasswordVisibilityCommand = ReactiveCommand.Create(() => { ShowPassword = !ShowPassword; });
    }

    /// <summary>连接显示名称;留空时保存时以 user@host 兜底。</summary>
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    /// <summary>
    /// 当前连接协议;SSH 为默认值,SFTP 复用 SSH 认证但不创建终端,FTP 走独立的 FTP/FTPS 栈。
    /// 未知值降级为 SSH(白名单同 <see cref="SessionProfile.ConnectionType" />)。
    /// </summary>
    public ConnectionType ConnectionType
    {
        get => _connectionType;
        private set
        {
            ConnectionType normalized = Enum.IsDefined(value) ? value : ConnectionType.SSH;
            if (_connectionType == normalized)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _connectionType, normalized);
            this.RaisePropertyChanged(nameof(IsSshSelected));
            this.RaisePropertyChanged(nameof(IsSftpSelected));
            this.RaisePropertyChanged(nameof(IsFtpSelected));
            this.RaisePropertyChanged(nameof(IsPluginSelected));
            this.RaisePropertyChanged(nameof(RequiresSshAuth));
            this.RaisePropertyChanged(nameof(ShowFtpPlaintextWarning));

            this.RaisePropertyChanged(nameof(ShowPasswordField));
            this.RaisePropertyChanged(nameof(HostLabel));
            this.RaisePropertyChanged(nameof(HostPlaceholder));
            this.RaisePropertyChanged(nameof(UsernameLabel));
            this.RaisePropertyChanged(nameof(PasswordLabel));
        }
    }

    /// <summary>协议标签是否选中 SSH。</summary>
    public bool IsSshSelected => ConnectionType == ConnectionType.SSH;

    /// <summary>协议标签是否选中 SFTP。</summary>
    public bool IsSftpSelected => ConnectionType == ConnectionType.SFTP;

    /// <summary>协议标签是否选中 FTP / FTPS。</summary>
    public bool IsFtpSelected => ConnectionType == ConnectionType.FTP;

    /// <summary>协议标签是否选中某个插件协议。</summary>
    public bool IsPluginSelected => ConnectionType == ConnectionType.Plugin;

    /// <summary>
    /// 是否需要 SSH 认证表单(私钥、口令、跳板)。FTP 只有用户名/口令与匿名登录,
    /// 插件协议(S3 之类)一般也只有一对密钥,把这些 SSH 专属项隐掉,
    /// 避免用户以为它们也能用私钥。
    /// </summary>
    public bool RequiresSshAuth => ConnectionType is not (ConnectionType.FTP or ConnectionType.Plugin);

    /// <summary>
    /// 「主机」输入框的标签。协议可以改写它:S3 填的是服务端点而不是一台主机,
    /// 沿用「主机名/IP」会让人以为要填某台服务器的地址 —— 同一个输入框,换个说法即可,
    /// 不必再开一个字段。文案由插件给(它才知道自己的领域词汇)。
    /// </summary>
    public string HostLabel => _pluginDescriptor?.HostLabel ?? Strings.Get("Profile_HostIp");

    /// <summary>「主机」输入框的占位提示。</summary>
    public string HostPlaceholder => _pluginDescriptor?.HostPlaceholder ?? "192.168.1.100";

    /// <summary>「用户名」输入框的标签(插件协议可改写,如 Access Key ID)。</summary>
    public string UsernameLabel => _pluginDescriptor?.UsernameLabel ?? Strings.Get("Username");

    /// <summary>「密码」输入框的标签(插件协议可改写,如 Secret Access Key)。</summary>
    public string PasswordLabel => _pluginDescriptor?.PasswordLabel ?? Strings.Get("Password");

    /// <summary>
    /// 当前协议是否允许不填凭据直接连(匿名访问)。
    /// **必须是可观察的存储属性**:它参与保存/连接按钮的可用性判定,而那个判定是
    /// <c>WhenAnyValue</c> 组合器 —— 纯计算属性发的 PropertyChanged 驱动不了它,
    /// 结果就是"编辑一条已存的匿名 S3 配置,按钮灰着,得先随便改个字段才亮"。
    /// </summary>
    public bool AllowsAnonymous
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>加密方式下拉的下标:0 自动、1 显式 FTPS、2 隐式 FTPS、3 不加密。</summary>
    public int FtpEncryptionIndex
    {
        get => _ftpEncryption switch
        {
            FtpEncryptionMode.Explicit => 1,
            FtpEncryptionMode.Implicit => 2,
            FtpEncryptionMode.None => 3,
            _ => 0
        };
        set => FtpEncryption = value switch
        {
            1 => FtpEncryptionMode.Explicit,
            2 => FtpEncryptionMode.Implicit,
            3 => FtpEncryptionMode.None,
            _ => FtpEncryptionMode.Auto
        };
    }

    /// <summary>是否提示「明文 FTP 不加密」。选了不加密才提示;FTPS 不提示。</summary>
    public bool ShowFtpPlaintextWarning => IsFtpSelected && FtpEncryption == FtpEncryptionMode.None;

    /// <summary>是否显示口令输入框:匿名 FTP 不需要口令。</summary>
    public bool ShowPasswordField => IsPasswordAuth && !(IsFtpSelected && FtpAnonymous);

    /// <summary>FTP 加密方式;仅 <see cref="IsFtpSelected" /> 时有意义。</summary>
    public FtpEncryptionMode FtpEncryption
    {
        get => _ftpEncryption;
        set
        {
            this.RaiseAndSetIfChanged(ref _ftpEncryption, value);
            this.RaisePropertyChanged(nameof(FtpEncryptionIndex));
            this.RaisePropertyChanged(nameof(ShowFtpPlaintextWarning));
            // 隐式 FTPS 与明文/显式的默认端口不同,用户没手动改过端口时跟着切,省一次踩坑。
            if (value == FtpEncryptionMode.Implicit && Port == FtpSettings.DefaultPort)
            {
                Port = FtpSettings.DefaultImplicitPort;
            }
            else if (value != FtpEncryptionMode.Implicit && Port == FtpSettings.DefaultImplicitPort)
            {
                Port = FtpSettings.DefaultPort;
            }
        }
    }

    /// <summary>FTP 是否使用被动模式(默认开;主动模式在客户端 NAT 后基本不可用)。</summary>
    public bool FtpPassive
    {
        get => _ftpPassive;
        set => this.RaiseAndSetIfChanged(ref _ftpPassive, value);
    }

    /// <summary>FTP 是否匿名登录;开启后不再要求填写用户名。</summary>
    public bool FtpAnonymous
    {
        get => _ftpAnonymous;
        set
        {
            this.RaiseAndSetIfChanged(ref _ftpAnonymous, value);
            this.RaisePropertyChanged(nameof(ShowPasswordField));
        }
    }

    /// <summary>可选的插件协议页签(来自已安装插件的清单声明;插件未激活时也在)。</summary>
    public ObservableCollection<PluginProtocolTabViewModel> PluginProtocols { get; } = [];

    /// <summary>当前插件协议的设置表单字段(选中协议并完成激活后才有内容)。</summary>
    public ObservableCollection<PluginProtocolFieldViewModel> PluginFields { get; } = [];

    /// <summary>当前选中的插件协议 id;非插件协议时为 null。</summary>
    public string? PluginProtocolId
    {
        get => _pluginProtocolId;
        private set => this.RaiseAndSetIfChanged(ref _pluginProtocolId, value);
    }

    /// <summary>
    /// 表单是否还在等插件激活。协议页签在发现期就画出来了(不装载程序集),
    /// 用户点到它才触发惰性激活 —— 这中间有一小段"页签在、字段还没到"的状态。
    /// </summary>
    public bool IsPluginLoading
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>目标主机地址(主机名或 IP)。</summary>
    public string Host
    {
        get => _host;
        set => this.RaiseAndSetIfChanged(ref _host, value);
    }

    /// <summary>SSH 端口,有效范围 1–65535,默认 22。</summary>
    public int Port
    {
        get => _port;
        set => this.RaiseAndSetIfChanged(ref _port, value);
    }

    /// <summary>登录用户名。</summary>
    public string Username
    {
        get => _username;
        set => this.RaiseAndSetIfChanged(ref _username, value);
    }

    /// <summary>认证方式(密码或私钥);变更时同步刷新 <see cref="AuthMethodIndex" />。</summary>
    public AuthMethod AuthMethod
    {
        get => _authMethod;
        set
        {
            this.RaiseAndSetIfChanged(ref _authMethod, value);
            this.RaisePropertyChanged(nameof(AuthMethodIndex));
        }
    }

    /// <summary>认证方式下拉的索引(0=密码认证,1=密钥认证)。</summary>
    public int AuthMethodIndex
    {
        get => AuthMethod == AuthMethod.PrivateKey ? 1 : 0;
        set => AuthMethod = value == 1 ? AuthMethod.PrivateKey : AuthMethod.Password;
    }

    /// <summary>密码以 SecureString 承载;ASCII 过滤由 <c>SecurePasswordBox</c> 输入行为负责。</summary>
    public SecureString? Password
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    /// <summary>私钥文件路径(密钥认证时使用)。</summary>
    public string? PrivateKeyPath
    {
        get => _privateKeyPath;
        set => this.RaiseAndSetIfChanged(ref _privateKeyPath, value);
    }

    /// <summary>私钥口令(私钥受密码保护时使用)。</summary>
    public string? PrivateKeyPassphrase
    {
        get => _privateKeyPassphrase;
        set => this.RaiseAndSetIfChanged(ref _privateKeyPassphrase, value);
    }

    /// <summary>所属分组的 Id;null 表示未分组。</summary>
    public Guid? GroupId
    {
        get => _groupId;
        set => this.RaiseAndSetIfChanged(ref _groupId, value);
    }

    /// <summary>跳板主机下拉:“直连” + 除自身外的全部已保存配置。</summary>
    public ObservableCollection<GroupOption> JumpHostOptions { get; }

    /// <summary>当前选中的跳板主机项;null 项表示直连。</summary>
    public GroupOption? SelectedJumpHost
    {
        get => _selectedJumpHost;
        set => this.RaiseAndSetIfChanged(ref _selectedJumpHost, value);
    }

    /// <summary>当前是否为密码认证;由认证方式派生,控制密码相关字段的可见性。</summary>
    public bool IsPasswordAuth
    {
        get => _isPasswordAuth;
        private set => this.RaiseAndSetIfChanged(ref _isPasswordAuth, value);
    }

    /// <summary>当前是否为密钥认证;由认证方式派生,控制私钥相关字段的可见性。</summary>
    public bool IsKeyAuth
    {
        get => _isKeyAuth;
        private set => this.RaiseAndSetIfChanged(ref _isKeyAuth, value);
    }

    /// <summary>是否正忙(保存/连接/测试进行中);用于禁用命令与显示进度。</summary>
    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>最近一次操作的错误信息;无错误时为 null。</summary>
    public string? ErrorMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>最近一次连接测试结果;null 表示尚未测试,变更时同步刷新 <see cref="ShowTestSuccess" />。</summary>
    public bool? LastTestSucceeded
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowTestSuccess));
        }
    }

    /// <summary>“连接测试成功”提示可见性。</summary>
    public bool ShowTestSuccess => LastTestSucceeded == true;

    /// <summary>记住密码(AES-256 加密存储);未勾选时密码只用于本次连接。</summary>
    public bool RememberPassword
    {
        get => _rememberPassword;
        set => this.RaiseAndSetIfChanged(ref _rememberPassword, value);
    }

    /// <summary>是否明文显示密码。</summary>
    public bool ShowPassword
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>高级选项区域是否展开。</summary>
    public bool IsAdvancedVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>标签,逗号分隔(高级选项)。</summary>
    public string TagsText
    {
        get => _tagsText;
        set => this.RaiseAndSetIfChanged(ref _tagsText, value);
    }

    /// <summary>分组下拉候选:“未分组” + 全部已保存分组。</summary>
    public ObservableCollection<GroupOption> Groups { get; }

    /// <summary>当前选中的分组项;null 项表示未分组。</summary>
    public GroupOption? SelectedGroup
    {
        get => _selectedGroup;
        set => this.RaiseAndSetIfChanged(ref _selectedGroup, value);
    }

    /// <summary>
    /// 分组框的可编辑文本:既可从下拉选已有分组,也可直接输入新分组名;
    /// 保存时由 <see cref="ResolveGroupFromTextAsync" /> 解析(不存在则建组归属)。
    /// </summary>
    public string GroupText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = UngroupedName;

    /// <summary>“连接”按钮关闭弹窗后,由宿主窗口发起连接。</summary>
    public bool ConnectAfterClose { get; private set; }

    /// <summary>保存配置命令;成功返回保存后的 <see cref="SessionProfile" />,失败返回 null。</summary>
    public ReactiveCommand<RxVoid, SessionProfile?> SaveCommand { get; }

    /// <summary>连接命令;先保存配置,再请求宿主窗口在弹窗关闭后立即连接。</summary>
    public ReactiveCommand<RxVoid, SessionProfile?> ConnectCommand { get; }

    /// <summary>取消命令;关闭弹窗并返回 null。</summary>
    public ReactiveCommand<RxVoid, SessionProfile?> CancelCommand { get; }

    /// <summary>连接测试命令;不落库,仅探测能否连通并回填结果。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TestConnectionCommand { get; }

    /// <summary>浏览私钥文件命令;由视图层挂接文件选择对话框。</summary>
    public ReactiveCommand<RxVoid, RxVoid> BrowseKeyFileCommand { get; }

    /// <summary>切换高级选项区域展开/收起的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleAdvancedCommand { get; }

    /// <summary>切换密码明文/掩码显示的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TogglePasswordVisibilityCommand { get; }

    /// <summary>选择 SSH 或 SFTP;Telnet/串口没有对应命令,因此仍保持禁用。</summary>
    public ReactiveCommand<ConnectionType, RxVoid> SelectConnectionTypeCommand { get; }

    /// <summary>切到某个插件协议页签(按需触发该插件的惰性激活并渲染它的设置表单)。</summary>
    public ReactiveCommand<string, RxVoid> SelectPluginProtocolCommand { get; }

    /// <summary>
    /// 从仓储加载分组下拉(“未分组” + 全部分组),并选中当前配置的分组;
    /// 同时装填跳板主机下拉(“直连” + 其余已保存配置)。
    /// </summary>
    public async Task LoadGroupsAsync()
    {
        if (_sessionRepository is null)
        {
            return;
        }
        try
        {
            List<ServerGroup> groups = await _sessionRepository.GetAllGroupsAsync();
            while (Groups.Count > 1)
            {
                Groups.RemoveAt(Groups.Count - 1);
            }
            foreach (ServerGroup group in groups)
            {
                Groups.Add(new(group.Id, group.Name));
            }
            SelectedGroup = Groups.FirstOrDefault(option => option.Id == _groupId) ?? Groups[0];
        }
        catch
        {
            // 分组加载失败时仍可保存为未分组。
        }
        await LoadJumpHostOptionsAsync();
    }

    /// <summary>跳板主机候选 = 除自身外的全部已保存配置(跳板需已存凭据才能免交互连上)。</summary>
    private async Task LoadJumpHostOptionsAsync()
    {
        if (_sessionRepository is null)
        {
            return;
        }
        try
        {
            List<SessionProfile> profiles = await _sessionRepository.GetAllSessionsAsync();
            while (JumpHostOptions.Count > 1)
            {
                JumpHostOptions.RemoveAt(JumpHostOptions.Count - 1);
            }
            foreach (SessionProfile profile in profiles
                                               .Where(p => p.Id != _profileId)
                                               .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                JumpHostOptions.Add(new(profile.Id, profile.Name));
            }
            SelectedJumpHost = JumpHostOptions.FirstOrDefault(option => option.Id == _jumpHostProfileId) ?? JumpHostOptions[0];
        }
        catch
        {
            // 跳板列表加载失败时仍可按直连保存。
        }
    }

    private async Task<SessionProfile?> SaveAsync()
    {
        try
        {
            BeginBusy();
            ErrorMessage = null;
            await ResolveGroupFromTextAsync();
            SessionProfile profile = BuildProfile();
            if (_connectionWorkflowService is null)
            {
                return profile;
            }
            return await _connectionWorkflowService.SaveProfileAsync(profile);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return null;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>“连接”:保存配置并请求宿主窗口在弹窗关闭后立即连接。</summary>
    private async Task<SessionProfile?> ConnectAsync()
    {
        SessionProfile? profile = await SaveAsync();
        if (profile is not null)
        {
            ConnectAfterClose = true;
        }
        return profile;
    }

    private async Task TestConnectionAsync()
    {
        if (_connectionWorkflowService is null)
        {
            LastTestSucceeded = null;
            return;
        }
        try
        {
            BeginBusy();
            ErrorMessage = null;
            ConnectionTestResult result = await _connectionWorkflowService.TestConnectionAsync(BuildProfile());
            LastTestSucceeded = result.Success;
            ErrorMessage = result.ErrorMessage;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>
    /// 把分组框文本解析为 GroupId:留空/“未分组”→ null;命中已有分组(不区分
    /// 大小写)→ 其 Id;否则新建分组落库并归属。无仓储(设计时)只回退未分组,
    /// 避免产生指向不存在分组的悬空 Id。
    /// </summary>
    private async Task ResolveGroupFromTextAsync()
    {
        string text = GroupText.Trim();
        if (text.Length == 0 || text == UngroupedName)
        {
            SelectedGroup = Groups[0];
            return;
        }
        GroupOption? existing = Groups.FirstOrDefault(option => option.Id is not null && string.Equals(option.Name, text, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedGroup = existing;
            return;
        }
        if (_sessionRepository is null)
        {
            SelectedGroup = Groups[0];
            return;
        }
        var group = new ServerGroup
        {
            Name = text,
            SortOrder = Groups.Count - 1 // 排在已有分组之后(下拉含“未分组”占位,故 -1)。
        };
        await _sessionRepository.SaveGroupAsync(group);
        var option = new GroupOption(group.Id, group.Name);
        Groups.Add(option);
        SelectedGroup = option;
    }

    private SessionProfile BuildProfile()
    {
        // 显示名称留空时用 user@host 兜底,保证列表/标签页有可读名称。
        string name = string.IsNullOrWhiteSpace(Name) ? $"{Username}@{Host}" : Name.Trim();
        return new()
        {
            Id = _profileId,
            ConnectionType = ConnectionType,
            Name = name,
            Host = Host.Trim(),
            Port = Port,
            Username = Username.Trim(),
            AuthMethod = AuthMethod,
            Password = SecureStringConvert.ToPlaintext(Password),
            RememberPassword = RememberPassword,
            PrivateKeyPath = PrivateKeyPath,
            PrivateKeyPassphrase = PrivateKeyPassphrase,
            GroupId = GroupId,
            Tags = [.. TagsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            JumpHostProfileId = _jumpHostProfileId,
            // 只有 FTP 才落这块设置:其余协议保持 null,旧数据与旧版本读取零影响。
            Ftp = ConnectionType == ConnectionType.FTP
                ? new FtpSettings
                {
                    EncryptionMode = FtpEncryption,
                    DataConnectionMode = FtpPassive ? FtpDataConnectionMode.Passive : FtpDataConnectionMode.Active,
                    Anonymous = FtpAnonymous,
                    TrustedCertificateThumbprint = _ftpTrustedThumbprint,
                    MaxConnections = _ftpMaxConnections,
                }
                : null,
            // 插件协议:只存协议 id 与它自己声明的那些字段。宿主对这些键一无所知,
            // 这正是把 S3 之类的协议移出宿主的前提。
            PluginProtocolId = ConnectionType == ConnectionType.Plugin ? PluginProtocolId : null,
            PluginSettings = ConnectionType == ConnectionType.Plugin ? CollectPluginValues(secrets: false) : null,
            // 机密与非机密分成两个字典:仓储层在落盘那一刻并不知道某个协议哪些字段是机密
            // (那在插件里),分开存才能做到「机密永远加密」这条不依赖任何查表的硬保证。
            PluginSecrets = ConnectionType == ConnectionType.Plugin ? CollectPluginValues(secrets: true) : null
        };
    }

    /// <summary>把表单字段收集成落盘字典;<paramref name="secrets" /> 决定收机密还是非机密那一半。</summary>
    private Dictionary<string, string>? CollectPluginValues(bool secrets)
    {
        // **以已存的那份为底**,再让表单里可见的字段覆盖上去。
        // 直接从空字典攒是不行的:隐藏字段(如用户确认信任后写回的证书指纹)压根不进表单,
        // 从空字典攒等于每保存一次就把它抹掉一次,下次连接又弹「证书不可信」。
        Dictionary<string, string>? stored = secrets ? _pluginStoredSecrets : _pluginStored;
        Dictionary<string, string> values = SessionProfile.CloneSettings(stored) ?? new(StringComparer.Ordinal);
        foreach (PluginProtocolFieldViewModel field in PluginFields.Where(f => f.IsSecret == secrets))
        {
            values[field.Key] = field.Text;
        }
        return values.Count == 0 ? stored : values;
    }

    /// <summary>
    /// 选中一个插件协议页签:切到 Plugin 类型、按需触发插件惰性激活、渲染它声明的表单。
    /// <para>
    /// 这个方法里挤着四件容易互相咬的事,注释逐条说明为什么这么写:
    /// 「换没换协议」的判定基准、旧值的保管、陈旧续体的作废、以及忙态的归属。
    /// </para>
    /// </summary>
    /// <param name="protocolId">协议 id。</param>
    public async Task SelectPluginProtocolAsync(string protocolId)
    {
        // 判定基准是 _loadedProtocolId(最近一次真正渲染过表单的协议),**不是** PluginProtocolId ——
        // 后者会被 SelectConnectionType 在切回内建协议时置 null,于是「点 SSH 再点回 S3」
        // 会被误判成换了协议,把 _pluginStored 一起清掉:隐藏字段(证书指纹)与机密
        // (sessionToken)就此静默永久丢失。
        bool switchedProtocol = _loadedProtocolId is { } loaded
                                && !string.Equals(loaded, protocolId, StringComparison.Ordinal);
        PluginProtocolId = protocolId;
        ConnectionType = ConnectionType.Plugin;
        foreach (PluginProtocolTabViewModel tabViewModel in PluginProtocols)
        {
            tabViewModel.IsSelected = tabViewModel.Id == protocolId;
        }
        // 插件协议同样没有私钥认证:与 SelectConnectionType 走同一套善后,
        // 否则从「私钥认证的 SSH」切过来后,认证方式下拉被隐藏、口令框也不显示,
        // 界面上再没有任何途径把它切回口令。
        NormalizeAuthMethodForProtocol();
        // 端口跟随用与内建协议**同一套**判定(它已把插件协议的默认端口一并算进「用户没手填过」)。
        PluginProtocolTabViewModel? tab = PluginProtocols.FirstOrDefault(p => p.Id == protocolId);
        if (tab is not null && IsProtocolDefaultPort(Port))
        {
            Port = tab.DefaultPort;
        }
        if (switchedProtocol)
        {
            // 真的换了协议:上一个协议的字段与已存值都不能留,否则等激活的这段时间里
            // 表单还是旧协议的样子,此时保存会把 A 的键值写进 B 的配置。
            PluginFields.Clear();
            _pluginDescriptor = null;
            _pluginStored = null;
            _pluginStoredSecrets = null;
        }
        else if (_pluginDescriptor is not null
                 && string.Equals(_loadedProtocolId, protocolId, StringComparison.Ordinal))
        {
            // 重复点当前页签:表单已经渲染好了,直接返回。
            // 走下去会 PluginFields.Clear() 把用户填了一半的内容复位。
            RaisePluginLabelsChanged();
            return;
        }
        if (_protocolRegistry is not { } registry)
        {
            return;
        }

        IsPluginLoading = true;
        // 忙态用引用计数:SaveAsync / TestConnectionAsync / 本方法共用同一个 IsBusy,
        // 裸布尔会让先结束的那个提前解除忙态 —— 于是用户能在 PluginFields 还空着时按保存,
        // 落盘一条 PluginSettings/PluginSecrets 全为 null 的配置。
        BeginBusy();
        bool applied = false;
        try
        {
            // 这一步可能真的去装载插件程序集(onProtocol 惰性激活),因此是异步的。
            PluginProtocolRegistration? registration = await registry.ResolveAsync(protocolId).ConfigureAwait(true);
            // **陈旧续体作废**:装载期间用户可能已经点去了 SSH 或另一个协议(协议页签不受
            // canExecute 约束)。不校验就会把 S3 的描述符盖到 SSH 表单上,
            // 主机那格显示「服务端点」、用户名显示「Access Key ID」。
            if (ConnectionType != ConnectionType.Plugin
                || !string.Equals(PluginProtocolId, protocolId, StringComparison.Ordinal))
            {
                return;
            }
            applied = true;
            _pluginDescriptor = registration?.Descriptor;
            _loadedProtocolId = protocolId;
            // 插件被禁用/装载失败:表单是空的,而 AllowsAnonymous 随之为 false,
            // 匿名配置的保存/连接会同时灰死。给一条能看懂的话,别让用户对着灰按钮猜。
            PluginUnavailable = registration is null;
            PluginFields.Clear();
            foreach (ProtocolSettingField field in _pluginDescriptor?.Fields ?? [])
            {
                if (field.IsHidden)
                {
                    // 隐藏字段(证书指纹之类)不进表单,但要原样带回落盘 —— 见 CollectPluginValues。
                    continue;
                }
                string? stored = (field.IsSecret ? _pluginStoredSecrets : _pluginStored) is { } bag
                                 && bag.TryGetValue(field.Key, out string? value)
                    ? value
                    : null;
                PluginFields.Add(new(field, stored));
            }
        }
        finally
        {
            IsPluginLoading = false;
            EndBusy();
            // return 也会走 finally,所以必须用 applied 兜住:陈旧续体不能去刷新标签。
            if (applied)
            {
                RaisePluginLabelsChanged();
                if (PluginUnavailable)
                {
                    ErrorMessage = Strings.Get("Plugin_ProtocolUnavailable");
                }
            }
        }
    }

    /// <summary>
    /// 当前插件协议不可用(插件未安装/被禁用/激活失败)。
    /// 界面据此放行保存(让用户至少能改名保存),并给出说明而不是三个一起灰死的按钮。
    /// </summary>
    public bool PluginUnavailable
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 忙态引用计数。三条流程(保存 / 测试连接 / 切插件协议)共用一个 <see cref="IsBusy" />,
    /// 裸布尔会让先结束的那条提前把忙态解除,另一条还在跑却已经允许用户操作。
    /// 全在 UI 线程,普通 int 足够。
    /// </summary>
    private int _busyDepth;

    private void BeginBusy()
    {
        if (++_busyDepth == 1)
        {
            IsBusy = true;
        }
    }

    private void EndBusy()
    {
        if (--_busyDepth <= 0)
        {
            _busyDepth = 0;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把当前表单里的值回存进 <c>_pluginStored*</c>。切走时调用,使「绕一圈回来」能复现已填内容;
    /// <c>_loadedProtocolId</c> 不动 —— 它标记的是「这份 stored 属于哪个协议」。
    /// </summary>
    private void StashPluginFieldValues()
    {
        if (PluginFields.Count == 0)
        {
            return;
        }
        Dictionary<string, string> plain = SessionProfile.CloneSettings(_pluginStored) ?? new(StringComparer.Ordinal);
        Dictionary<string, string> secrets = SessionProfile.CloneSettings(_pluginStoredSecrets) ?? new(StringComparer.Ordinal);
        foreach (PluginProtocolFieldViewModel field in PluginFields)
        {
            (field.IsSecret ? secrets : plain)[field.Key] = field.Text;
        }
        _pluginStored = plain;
        _pluginStoredSecrets = secrets;
    }

    /// <summary>
    /// 协议集合变化 → 重建页签。**必须封送回 UI 线程**:注册表的 Changed 可能在
    /// 发现期的后台线程或惰性激活的线程池线程上触发,直接改 ObservableCollection 会崩。
    /// </summary>
    private void OnProtocolsChanged() => Dispatcher.UIThread.Post(() =>
    {
        string? selected = PluginProtocolId;
        LoadPluginProtocols();
        foreach (PluginProtocolTabViewModel tab in PluginProtocols)
        {
            tab.IsSelected = tab.Id == selected;
        }
    });

    /// <summary>
    /// 退订注册表事件。注册表是宿主单例,不退订的话每开一次连接对话框就在它上面
    /// 挂一个永不释放的视图模型。由 <c>ConnectionProfileView</c> 在窗口关闭时调用。
    /// </summary>
    public void Dispose()
    {
        if (_protocolRegistry is not null)
        {
            _protocolRegistry.Changed -= OnProtocolsChanged;
        }
    }

    /// <summary>把注册表里的协议页签同步到界面(打开对话框时调用)。</summary>
    public void LoadPluginProtocols()
    {
        PluginProtocols.Clear();
        foreach (PluginProtocolTab tab in _protocolRegistry?.Tabs ?? [])
        {
            PluginProtocols.Add(new(tab.Id, tab.DisplayName, tab.DefaultPort));
        }
    }

    /// <summary>
    /// 切换协议标签。顺带把端口切到新协议的默认值(仅当当前端口还是别的协议的默认值时),
    /// 免得用户从 SSH 切到 FTP 后仍在 22 端口上连不上。
    /// </summary>
    private void SelectConnectionType(ConnectionType connectionType)
    {
        ConnectionType previous = ConnectionType;
        ConnectionType = connectionType;
        if (connectionType != ConnectionType.Plugin)
        {
            // 切回内建协议:插件页签的高亮要跟着灭,否则会同时亮两个。
            PluginProtocolId = null;
            foreach (PluginProtocolTabViewModel tabViewModel in PluginProtocols)
            {
                tabViewModel.IsSelected = false;
            }
            // 先把表单里已填的值回存,再清空 —— 否则「点 SSH 再点回 S3」时用户填了一半的内容没了。
            StashPluginFieldValues();
            // 描述符也必须清:三格标签只看它、不看 ConnectionType,不清的话
            // 「S3 → SSH」之后主机那格会一直写着「服务端点」、用户名写着「Access Key ID」。
            _pluginDescriptor = null;
            PluginFields.Clear();
            PluginUnavailable = false;
            RaisePluginLabelsChanged();
        }
        if (previous == ConnectionType)
        {
            return;
        }
        NormalizeAuthMethodForProtocol();
        // 端口跟随:仅当当前端口仍是**某个协议的默认值**时才改 —— 用户手填过的端口一律不动。
        if (IsProtocolDefaultPort(Port))
        {
            Port = DefaultPortFor(ConnectionType);
        }
    }

    /// <summary>
    /// FTP 与插件协议都没有私钥认证:切过去时把认证方式落回口令。
    /// 不做这一步,表单会停在一个用不上的私钥页,而那时认证方式下拉恰好是隐藏的 ——
    /// 界面上再没有任何途径把它切回来,保存下去的却仍是 PrivateKey。
    /// </summary>
    private void NormalizeAuthMethodForProtocol()
    {
        if (!RequiresSshAuth && IsKeyAuth)
        {
            AuthMethodIndex = 0;
        }
    }

    /// <summary>
    /// 三格标签与匿名能力都只看协议描述符,描述符一换就得整组补发通知。
    /// 抽出来是因为它有两个调用点(选中插件协议、切回内建协议),漏一处就会出现
    /// 「已经切回 SSH,主机那格还写着"服务端点"」。
    /// </summary>
    private void RaisePluginLabelsChanged()
    {
        this.RaisePropertyChanged(nameof(HostLabel));
        this.RaisePropertyChanged(nameof(HostPlaceholder));
        this.RaisePropertyChanged(nameof(UsernameLabel));
        this.RaisePropertyChanged(nameof(PasswordLabel));
        // 走属性 setter 而不是只发通知:它要驱动 canExecute 的组合器重算。
        AllowsAnonymous = _pluginDescriptor?.Features.HasFlag(ProtocolFeatures.AnonymousAccess) == true;
    }

    /// <summary>
    /// 该端口是否还是某个协议的默认值(即「用户没自己填过」)。
    /// 插件协议的默认端口来自它们各自的清单声明,因此这里要连它们一起看 ——
    /// 写死一张内建协议的表,会让"从 S3 切回 SSH"时端口停在 443 上。
    /// </summary>
    private bool IsProtocolDefaultPort(int port) =>
        port is 22 or FtpSettings.DefaultPort or FtpSettings.DefaultImplicitPort
        || PluginProtocols.Any(protocol => protocol.DefaultPort == port);

    private int DefaultPortFor(ConnectionType type) =>
        type switch
        {
            ConnectionType.FTP => FtpEncryption == FtpEncryptionMode.Implicit
                ? FtpSettings.DefaultImplicitPort
                : FtpSettings.DefaultPort,
            ConnectionType.Plugin => PluginProtocols.FirstOrDefault(p => p.Id == PluginProtocolId)?.DefaultPort ?? 443,
            _ => 22
        };
}

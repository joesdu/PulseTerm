using System.Globalization;
using System.Resources;

namespace VelaShell.Core.Resources;

/// <summary>
/// 集中管理本地化 UI 文案的强类型访问器,从 Strings.resx 资源按当前 UI 语言读取。
/// </summary>
public static class Strings
{
    private static readonly ResourceManager ResourceManager = new("VelaShell.Core.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>
    /// 按键名取当前 UI 语言的文案;缺失时回退键名(便于发现漏译)。
    /// 供 C# 侧的动态文案使用(状态栏消息、对话框、命令注册表等);
    /// axaml 用 {loc:Localize Key} 以获得换语言即时刷新。
    /// </summary>
    public static string Get(string key) =>
        ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>取文案并做 string.Format(占位符 {0}/{1}…)。</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>应用程序名称。</summary>
    public static string AppName => ResourceManager.GetString(nameof(AppName), CultureInfo.CurrentUICulture) ?? nameof(AppName);

    /// <summary>状态栏“就绪”提示文案。</summary>
    public static string Ready => ResourceManager.GetString(nameof(Ready), CultureInfo.CurrentUICulture) ?? nameof(Ready);

    /// <summary>“快速连接”标签/按钮文案。</summary>
    public static string QuickConnect => ResourceManager.GetString(nameof(QuickConnect), CultureInfo.CurrentUICulture) ?? nameof(QuickConnect);

    /// <summary>“最近连接”分组标题文案。</summary>
    public static string RecentConnections => ResourceManager.GetString(nameof(RecentConnections), CultureInfo.CurrentUICulture) ?? nameof(RecentConnections);

    /// <summary>“设置”标签/按钮文案。</summary>
    public static string Settings => ResourceManager.GetString(nameof(Settings), CultureInfo.CurrentUICulture) ?? nameof(Settings);

    /// <summary>“通知”标签/按钮文案。</summary>
    public static string Notifications => ResourceManager.GetString(nameof(Notifications), CultureInfo.CurrentUICulture) ?? nameof(Notifications);

    /// <summary>“插件”标签/按钮文案。</summary>
    public static string Plugins => ResourceManager.GetString(nameof(Plugins), CultureInfo.CurrentUICulture) ?? nameof(Plugins);

    /// <summary>“新建标签页”菜单/按钮文案。</summary>
    public static string NewTab => ResourceManager.GetString(nameof(NewTab), CultureInfo.CurrentUICulture) ?? nameof(NewTab);

    /// <summary>“关闭标签页”菜单/按钮文案。</summary>
    public static string CloseTab => ResourceManager.GetString(nameof(CloseTab), CultureInfo.CurrentUICulture) ?? nameof(CloseTab);

    /// <summary>“搜索”标签/按钮文案。</summary>
    public static string Search => ResourceManager.GetString(nameof(Search), CultureInfo.CurrentUICulture) ?? nameof(Search);

    /// <summary>“复制”菜单/按钮文案。</summary>
    public static string Copy => ResourceManager.GetString(nameof(Copy), CultureInfo.CurrentUICulture) ?? nameof(Copy);

    /// <summary>“拆分”窗格菜单/按钮文案。</summary>
    public static string Split => ResourceManager.GetString(nameof(Split), CultureInfo.CurrentUICulture) ?? nameof(Split);

    /// <summary>文件列表“文件名”列标题文案。</summary>
    public static string FileName => ResourceManager.GetString(nameof(FileName), CultureInfo.CurrentUICulture) ?? nameof(FileName);

    /// <summary>文件列表“大小”列标题文案。</summary>
    public static string Size => ResourceManager.GetString(nameof(Size), CultureInfo.CurrentUICulture) ?? nameof(Size);

    /// <summary>文件列表“权限”列标题文案。</summary>
    public static string Permissions => ResourceManager.GetString(nameof(Permissions), CultureInfo.CurrentUICulture) ?? nameof(Permissions);

    /// <summary>文件列表“修改时间”列标题文案。</summary>
    public static string Modified => ResourceManager.GetString(nameof(Modified), CultureInfo.CurrentUICulture) ?? nameof(Modified);

    /// <summary>“终端类型”设置项标签文案。</summary>
    public static string TerminalType => ResourceManager.GetString(nameof(TerminalType), CultureInfo.CurrentUICulture) ?? nameof(TerminalType);

    /// <summary>“终端编码”设置项标签文案。</summary>
    public static string TerminalEncoding => ResourceManager.GetString(nameof(TerminalEncoding), CultureInfo.CurrentUICulture) ?? nameof(TerminalEncoding);

    /// <summary>“上传”按钮文案。</summary>
    public static string Upload => ResourceManager.GetString(nameof(Upload), CultureInfo.CurrentUICulture) ?? nameof(Upload);

    /// <summary>“下载”按钮文案。</summary>
    public static string Download => ResourceManager.GetString(nameof(Download), CultureInfo.CurrentUICulture) ?? nameof(Download);

    /// <summary>“刷新”按钮文案。</summary>
    public static string Refresh => ResourceManager.GetString(nameof(Refresh), CultureInfo.CurrentUICulture) ?? nameof(Refresh);

    /// <summary>“本地转发”隧道类型文案。</summary>
    public static string LocalForward => ResourceManager.GetString(nameof(LocalForward), CultureInfo.CurrentUICulture) ?? nameof(LocalForward);

    /// <summary>“远程转发”隧道类型文案。</summary>
    public static string RemoteForward => ResourceManager.GetString(nameof(RemoteForward), CultureInfo.CurrentUICulture) ?? nameof(RemoteForward);

    /// <summary>“本地端口”输入项标签文案。</summary>
    public static string LocalPort => ResourceManager.GetString(nameof(LocalPort), CultureInfo.CurrentUICulture) ?? nameof(LocalPort);

    /// <summary>“新建隧道”按钮文案。</summary>
    public static string NewTunnel => ResourceManager.GetString(nameof(NewTunnel), CultureInfo.CurrentUICulture) ?? nameof(NewTunnel);

    /// <summary>“网络”分类/面板标题文案。</summary>
    public static string Network => ResourceManager.GetString(nameof(Network), CultureInfo.CurrentUICulture) ?? nameof(Network);

    /// <summary>“Docker”分类/面板标题文案。</summary>
    public static string Docker => ResourceManager.GetString(nameof(Docker), CultureInfo.CurrentUICulture) ?? nameof(Docker);

    /// <summary>“自定义”分类文案。</summary>
    public static string Custom => ResourceManager.GetString(nameof(Custom), CultureInfo.CurrentUICulture) ?? nameof(Custom);

    /// <summary>“快捷命令”面板标题文案。</summary>
    public static string QuickCommands => ResourceManager.GetString(nameof(QuickCommands), CultureInfo.CurrentUICulture) ?? nameof(QuickCommands);

    /// <summary>“添加命令”按钮文案。</summary>
    public static string AddCommand => ResourceManager.GetString(nameof(AddCommand), CultureInfo.CurrentUICulture) ?? nameof(AddCommand);

    /// <summary>“描述”输入项标签文案。</summary>
    public static string Description => ResourceManager.GetString(nameof(Description), CultureInfo.CurrentUICulture) ?? nameof(Description);

    /// <summary>“命令”输入项标签文案。</summary>
    public static string Command => ResourceManager.GetString(nameof(Command), CultureInfo.CurrentUICulture) ?? nameof(Command);

    /// <summary>“分类”输入项标签文案。</summary>
    public static string Category => ResourceManager.GetString(nameof(Category), CultureInfo.CurrentUICulture) ?? nameof(Category);

    /// <summary>“系统”分类文案。</summary>
    public static string System => ResourceManager.GetString(nameof(System), CultureInfo.CurrentUICulture) ?? nameof(System);

    /// <summary>连接状态“已连接”文案。</summary>
    public static string Connected => ResourceManager.GetString(nameof(Connected), CultureInfo.CurrentUICulture) ?? nameof(Connected);

    /// <summary>连接状态“连接中”文案。</summary>
    public static string Connecting => ResourceManager.GetString(nameof(Connecting), CultureInfo.CurrentUICulture) ?? nameof(Connecting);

    /// <summary>连接状态“已断开”文案。</summary>
    public static string Disconnected => ResourceManager.GetString(nameof(Disconnected), CultureInfo.CurrentUICulture) ?? nameof(Disconnected);

    /// <summary>“延迟”指标标签文案。</summary>
    public static string Latency => ResourceManager.GetString(nameof(Latency), CultureInfo.CurrentUICulture) ?? nameof(Latency);

    /// <summary>“会话”列表标题文案。</summary>
    public static string Sessions => ResourceManager.GetString(nameof(Sessions), CultureInfo.CurrentUICulture) ?? nameof(Sessions);

    /// <summary>“移动到分组”菜单文案。</summary>
    public static string MoveToGroup => ResourceManager.GetString(nameof(MoveToGroup), CultureInfo.CurrentUICulture) ?? nameof(MoveToGroup);

    /// <summary>“连接”按钮文案。</summary>
    public static string Connect => ResourceManager.GetString(nameof(Connect), CultureInfo.CurrentUICulture) ?? nameof(Connect);

    /// <summary>“断开”按钮文案。</summary>
    public static string Disconnect => ResourceManager.GetString(nameof(Disconnect), CultureInfo.CurrentUICulture) ?? nameof(Disconnect);

    /// <summary>“保存”按钮文案。</summary>
    public static string Save => ResourceManager.GetString(nameof(Save), CultureInfo.CurrentUICulture) ?? nameof(Save);

    /// <summary>“取消”按钮文案。</summary>
    public static string Cancel => ResourceManager.GetString(nameof(Cancel), CultureInfo.CurrentUICulture) ?? nameof(Cancel);

    /// <summary>“删除”按钮文案。</summary>
    public static string Delete => ResourceManager.GetString(nameof(Delete), CultureInfo.CurrentUICulture) ?? nameof(Delete);

    /// <summary>“编辑”按钮文案。</summary>
    public static string Edit => ResourceManager.GetString(nameof(Edit), CultureInfo.CurrentUICulture) ?? nameof(Edit);

    /// <summary>“确定”按钮文案。</summary>
    public static string OK => ResourceManager.GetString(nameof(OK), CultureInfo.CurrentUICulture) ?? nameof(OK);

    /// <summary>“错误”提示标题文案。</summary>
    public static string Error => ResourceManager.GetString(nameof(Error), CultureInfo.CurrentUICulture) ?? nameof(Error);

    /// <summary>“警告”提示标题文案。</summary>
    public static string Warning => ResourceManager.GetString(nameof(Warning), CultureInfo.CurrentUICulture) ?? nameof(Warning);

    /// <summary>“语言”设置项标签文案。</summary>
    public static string Language => ResourceManager.GetString(nameof(Language), CultureInfo.CurrentUICulture) ?? nameof(Language);

    /// <summary>“主题”设置项标签文案。</summary>
    public static string Theme => ResourceManager.GetString(nameof(Theme), CultureInfo.CurrentUICulture) ?? nameof(Theme);

    /// <summary>“字体”设置项标签文案。</summary>
    public static string Font => ResourceManager.GetString(nameof(Font), CultureInfo.CurrentUICulture) ?? nameof(Font);

    /// <summary>“字号”设置项标签文案。</summary>
    public static string FontSize => ResourceManager.GetString(nameof(FontSize), CultureInfo.CurrentUICulture) ?? nameof(FontSize);

    /// <summary>“回滚行数”设置项标签文案。</summary>
    public static string ScrollbackLines => ResourceManager.GetString(nameof(ScrollbackLines), CultureInfo.CurrentUICulture) ?? nameof(ScrollbackLines);

    /// <summary>“密码”输入项标签文案。</summary>
    public static string Password => ResourceManager.GetString(nameof(Password), CultureInfo.CurrentUICulture) ?? nameof(Password);

    /// <summary>“私钥”认证方式/输入项标签文案。</summary>
    public static string PrivateKey => ResourceManager.GetString(nameof(PrivateKey), CultureInfo.CurrentUICulture) ?? nameof(PrivateKey);

    /// <summary>“用户名”输入项标签文案。</summary>
    public static string Username => ResourceManager.GetString(nameof(Username), CultureInfo.CurrentUICulture) ?? nameof(Username);

    /// <summary>“主机”输入项标签文案。</summary>
    public static string Host => ResourceManager.GetString(nameof(Host), CultureInfo.CurrentUICulture) ?? nameof(Host);

    /// <summary>“端口”输入项标签文案。</summary>
    public static string Port => ResourceManager.GetString(nameof(Port), CultureInfo.CurrentUICulture) ?? nameof(Port);

    /// <summary>“主机密钥验证”对话框标题文案。</summary>
    public static string HostKeyVerification => ResourceManager.GetString(nameof(HostKeyVerification), CultureInfo.CurrentUICulture) ?? nameof(HostKeyVerification);

    /// <summary>“浏览密钥文件”按钮文案。</summary>
    public static string BrowseKeyFile => ResourceManager.GetString(nameof(BrowseKeyFile), CultureInfo.CurrentUICulture) ?? nameof(BrowseKeyFile);

    /// <summary>“连接配置”标题文案。</summary>
    public static string ConnectionProfile => ResourceManager.GetString(nameof(ConnectionProfile), CultureInfo.CurrentUICulture) ?? nameof(ConnectionProfile);

    /// <summary>“认证方式”标签文案。</summary>
    public static string AuthMethodLabel => ResourceManager.GetString(nameof(AuthMethodLabel), CultureInfo.CurrentUICulture) ?? nameof(AuthMethodLabel);

    /// <summary>“默认端口”提示文案。</summary>
    public static string DefaultPort => ResourceManager.GetString(nameof(DefaultPort), CultureInfo.CurrentUICulture) ?? nameof(DefaultPort);

    /// <summary>“主机密钥已变更”告警文案。</summary>
    public static string HostKeyChanged => ResourceManager.GetString(nameof(HostKeyChanged), CultureInfo.CurrentUICulture) ?? nameof(HostKeyChanged);

    /// <summary>“主机密钥未知”提示文案。</summary>
    public static string HostKeyUnknown => ResourceManager.GetString(nameof(HostKeyUnknown), CultureInfo.CurrentUICulture) ?? nameof(HostKeyUnknown);

    /// <summary>“指纹”标签文案。</summary>
    public static string Fingerprint => ResourceManager.GetString(nameof(Fingerprint), CultureInfo.CurrentUICulture) ?? nameof(Fingerprint);

    /// <summary>“密钥类型”标签文案。</summary>
    public static string KeyType => ResourceManager.GetString(nameof(KeyType), CultureInfo.CurrentUICulture) ?? nameof(KeyType);

    /// <summary>“拒绝”按钮文案。</summary>
    public static string Reject => ResourceManager.GetString(nameof(Reject), CultureInfo.CurrentUICulture) ?? nameof(Reject);

    /// <summary>“永久信任”选项文案。</summary>
    public static string TrustPermanently => ResourceManager.GetString(nameof(TrustPermanently), CultureInfo.CurrentUICulture) ?? nameof(TrustPermanently);

    /// <summary>“仅此一次信任”选项文案。</summary>
    public static string TrustOnce => ResourceManager.GetString(nameof(TrustOnce), CultureInfo.CurrentUICulture) ?? nameof(TrustOnce);

    /// <summary>“名称”输入项标签文案。</summary>
    public static string Name => ResourceManager.GetString(nameof(Name), CultureInfo.CurrentUICulture) ?? nameof(Name);

    /// <summary>“分组”输入项标签文案。</summary>
    public static string Group => ResourceManager.GetString(nameof(Group), CultureInfo.CurrentUICulture) ?? nameof(Group);

    /// <summary>“私钥口令”输入项标签文案。</summary>
    public static string Passphrase => ResourceManager.GetString(nameof(Passphrase), CultureInfo.CurrentUICulture) ?? nameof(Passphrase);

    // 文件浏览器 / SFTP(§6)
    /// <summary>“传输历史”面板标题文案。</summary>
    public static string TransferHistory => ResourceManager.GetString(nameof(TransferHistory), CultureInfo.CurrentUICulture) ?? nameof(TransferHistory);

    /// <summary>“上传到此处”菜单文案(文件与文件夹共用一个入口)。</summary>
    public static string UploadHere => ResourceManager.GetString(nameof(UploadHere), CultureInfo.CurrentUICulture) ?? nameof(UploadHere);

    /// <summary>“新建文件夹”菜单/按钮文案。</summary>
    public static string NewFolder => ResourceManager.GetString(nameof(NewFolder), CultureInfo.CurrentUICulture) ?? nameof(NewFolder);

    /// <summary>“新建文件”菜单/按钮文案。</summary>
    public static string NewFile => ResourceManager.GetString(nameof(NewFile), CultureInfo.CurrentUICulture) ?? nameof(NewFile);

    /// <summary>“重命名”菜单/按钮文案。</summary>
    public static string Rename => ResourceManager.GetString(nameof(Rename), CultureInfo.CurrentUICulture) ?? nameof(Rename);

    /// <summary>“移动到”菜单文案。</summary>
    public static string MoveTo => ResourceManager.GetString(nameof(MoveTo), CultureInfo.CurrentUICulture) ?? nameof(MoveTo);

    /// <summary>“移动到”对话框输入提示文案。</summary>
    public static string MoveToPrompt => ResourceManager.GetString(nameof(MoveToPrompt), CultureInfo.CurrentUICulture) ?? nameof(MoveToPrompt);

    /// <summary>“复制到”上下文菜单项文案。</summary>
    public static string CopyTo => ResourceManager.GetString(nameof(CopyTo), CultureInfo.CurrentUICulture) ?? nameof(CopyTo);

    /// <summary>“复制到”目标路径输入提示文案。</summary>
    public static string SftpCopyToPrompt => ResourceManager.GetString(nameof(SftpCopyToPrompt), CultureInfo.CurrentUICulture) ?? nameof(SftpCopyToPrompt);

    /// <summary>“复制文件路径”菜单文案。</summary>
    public static string CopyFilePath => ResourceManager.GetString(nameof(CopyFilePath), CultureInfo.CurrentUICulture) ?? nameof(CopyFilePath);

    /// <summary>“复制文件名”菜单文案。</summary>
    public static string CopyFileName => ResourceManager.GetString(nameof(CopyFileName), CultureInfo.CurrentUICulture) ?? nameof(CopyFileName);

    /// <summary>“属性”菜单文案。</summary>
    public static string Properties => ResourceManager.GetString(nameof(Properties), CultureInfo.CurrentUICulture) ?? nameof(Properties);

    /// <summary>权限矩阵“所有者”列标签文案。</summary>
    public static string PermissionOwner => ResourceManager.GetString(nameof(PermissionOwner), CultureInfo.CurrentUICulture) ?? nameof(PermissionOwner);

    /// <summary>权限矩阵“所属组”列标签文案。</summary>
    public static string PermissionGroup => ResourceManager.GetString(nameof(PermissionGroup), CultureInfo.CurrentUICulture) ?? nameof(PermissionGroup);

    /// <summary>权限矩阵“其他人”列标签文案。</summary>
    public static string PermissionOthers => ResourceManager.GetString(nameof(PermissionOthers), CultureInfo.CurrentUICulture) ?? nameof(PermissionOthers);

    /// <summary>权限矩阵“读”行标签文案。</summary>
    public static string PermissionRead => ResourceManager.GetString(nameof(PermissionRead), CultureInfo.CurrentUICulture) ?? nameof(PermissionRead);

    /// <summary>权限矩阵“写”行标签文案。</summary>
    public static string PermissionWrite => ResourceManager.GetString(nameof(PermissionWrite), CultureInfo.CurrentUICulture) ?? nameof(PermissionWrite);

    /// <summary>权限矩阵“执行”行标签文案。</summary>
    public static string PermissionExecute => ResourceManager.GetString(nameof(PermissionExecute), CultureInfo.CurrentUICulture) ?? nameof(PermissionExecute);

    /// <summary>“显示隐藏文件”开关文案。</summary>
    public static string ShowHiddenFiles => ResourceManager.GetString(nameof(ShowHiddenFiles), CultureInfo.CurrentUICulture) ?? nameof(ShowHiddenFiles);

    /// <summary>删除确认对话框标题文案。</summary>
    public static string ConfirmDeleteTitle => ResourceManager.GetString(nameof(ConfirmDeleteTitle), CultureInfo.CurrentUICulture) ?? nameof(ConfirmDeleteTitle);

    /// <summary>确认删除单个文件的提示文案(含占位符)。</summary>
    public static string ConfirmDeleteFile => ResourceManager.GetString(nameof(ConfirmDeleteFile), CultureInfo.CurrentUICulture) ?? nameof(ConfirmDeleteFile);

    /// <summary>确认删除单个文件夹的提示文案(含占位符)。</summary>
    public static string ConfirmDeleteFolder => ResourceManager.GetString(nameof(ConfirmDeleteFolder), CultureInfo.CurrentUICulture) ?? nameof(ConfirmDeleteFolder);

    /// <summary>确认批量删除的提示文案(含占位符)。</summary>
    public static string ConfirmDeleteMultiple => ResourceManager.GetString(nameof(ConfirmDeleteMultiple), CultureInfo.CurrentUICulture) ?? nameof(ConfirmDeleteMultiple);

    /// <summary>“加载中”进度提示文案。</summary>
    public static string Loading => ResourceManager.GetString(nameof(Loading), CultureInfo.CurrentUICulture) ?? nameof(Loading);

    /// <summary>“删除中”进度提示文案。</summary>
    public static string Deleting => ResourceManager.GetString(nameof(Deleting), CultureInfo.CurrentUICulture) ?? nameof(Deleting);

    /// <summary>删除进度文案(含进度占位符)。</summary>
    public static string DeletingProgress => ResourceManager.GetString(nameof(DeletingProgress), CultureInfo.CurrentUICulture) ?? nameof(DeletingProgress);

    /// <summary>“选择下载目录”对话框标题文案。</summary>
    public static string SelectDownloadFolder => ResourceManager.GetString(nameof(SelectDownloadFolder), CultureInfo.CurrentUICulture) ?? nameof(SelectDownloadFolder);

    /// <summary>“保存到本地”菜单/按钮文案。</summary>
    public static string SaveToLocal => ResourceManager.GetString(nameof(SaveToLocal), CultureInfo.CurrentUICulture) ?? nameof(SaveToLocal);

    /// <summary>“文件类型”标签文案。</summary>
    public static string FileType => ResourceManager.GetString(nameof(FileType), CultureInfo.CurrentUICulture) ?? nameof(FileType);

    /// <summary>“文件”类型标签文案。</summary>
    public static string File => ResourceManager.GetString(nameof(File), CultureInfo.CurrentUICulture) ?? nameof(File);

    /// <summary>“文件夹”类型标签文案。</summary>
    public static string Folder => ResourceManager.GetString(nameof(Folder), CultureInfo.CurrentUICulture) ?? nameof(Folder);

    /// <summary>“文件路径”标签文案。</summary>
    public static string FilePath => ResourceManager.GetString(nameof(FilePath), CultureInfo.CurrentUICulture) ?? nameof(FilePath);

    /// <summary>“重新连接”按钮文案。</summary>
    public static string Reconnect => ResourceManager.GetString(nameof(Reconnect), CultureInfo.CurrentUICulture) ?? nameof(Reconnect);

    /// <summary>终端连接断开时显示的提示文案。</summary>
    public static string TerminalDisconnectedNotice => ResourceManager.GetString(nameof(TerminalDisconnectedNotice), CultureInfo.CurrentUICulture) ?? nameof(TerminalDisconnectedNotice);

    /// <summary>终端重连操作的引导提示文案。</summary>
    public static string TerminalReconnectHint => ResourceManager.GetString(nameof(TerminalReconnectHint), CultureInfo.CurrentUICulture) ?? nameof(TerminalReconnectHint);
}

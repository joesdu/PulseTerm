using VelaShell.PluginSdk.Clipboard;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Events;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.RemoteTunnel;
using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Storage;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.PluginSdk;

/// <summary>
/// 插件上下文:插件访问宿主能力的唯一入口,由宿主在激活时提供。
/// 全部能力接口为传输无关设计(仅异步方法、DTO 与不透明 id)——
/// 当前实现为进程内直调;未来若迁移到进程外 PluginHost,插件源码保持不变。
/// </summary>
public interface IPluginContext
{
    /// <summary>插件 id(即 <c>plugin.json</c> 的 <c>id</c>)。</summary>
    string PluginId { get; }

    /// <summary>插件版本(即 <c>plugin.json</c> 的 <c>version</c>)。</summary>
    string PluginVersion { get; }

    /// <summary>
    /// 插件私有数据目录(绝对路径,已创建)。插件的一切本地写入都应限于此目录;
    /// 卸载插件时该目录可被整体删除。
    /// </summary>
    string DataDirectory { get; }

    /// <summary>宿主信息(版本、apiLevel、当前语言与主题)。</summary>
    IHostInfo Host { get; }

    /// <summary>结构化日志:写入宿主日志管道(带插件 id 前缀)。</summary>
    IPluginLogger Log { get; }

    /// <summary>键值存储:持久化于 <see cref="DataDirectory" />,插件间互不可见。</summary>
    IPluginStorage Storage { get; }

    /// <summary>
    /// 时序能力:插件私有的嵌入式时序库(按时间追加 + 按标签检索),
    /// 适合会话记录、指标采样、事件流;卸载插件时整体删除。
    /// </summary>
    TimeSeries.ITimeSeriesApi TimeSeries { get; }

    /// <summary>会话能力:枚举当前 SSH 会话(脱敏,不含任何凭据)。</summary>
    ISessionsApi Sessions { get; }

    /// <summary>远程文件能力:基于既有会话的 SFTP 读写。</summary>
    IRemoteFsApi RemoteFs { get; }

    /// <summary>远程执行能力:在既有会话上执行一次性命令(独立通道,不进用户终端)。</summary>
    IRemoteExecApi RemoteExec { get; }

    /// <summary>
    /// 远程隧道能力:在既有会话上开一条到远端 unix socket / TCP 端点的**裸字节双工流**。
    /// 用于承载文本行模型装不下的协议(Docker Engine API 的分块传输与 tar 流等)。
    /// 仅 <c>inProcess</c> 宿主模式可用,见 <see cref="IRemoteTunnelApi" />。
    /// </summary>
    IRemoteTunnelApi RemoteTunnel { get; }

    /// <summary>
    /// 终端视图能力:向插件出借宿主的终端仿真器,拿到一个可嵌进自己界面的终端控件。
    /// 与 <see cref="Terminal" />(旁路宿主已有会话)不同,这里插件拥有一个自己的终端。
    /// 仅 <c>inProcess</c> 宿主模式可用,见 <see cref="TerminalView.ITerminalViewApi" />。
    /// </summary>
    TerminalView.ITerminalViewApi TerminalView { get; }

    /// <summary>命令能力:向命令面板/菜单注册命令,或执行宿主命令。</summary>
    ICommandsApi Commands { get; }

    /// <summary>宿主事件:会话连接/断开、主题与语言切换。</summary>
    IHostEvents Events { get; }

    /// <summary>界面能力:呈现插件自建的 Avalonia 面板(停靠标签页或独立窗口)。</summary>
    IUiApi Ui { get; }

    /// <summary>机密存储:加密落盘的插件私有键值(隔离模式下只存宿主侧)。</summary>
    ISecretsApi Secrets { get; }

    /// <summary>系统剪贴板(文本)。</summary>
    IClipboardApi Clipboard { get; }

    /// <summary>终端能力:读取/搜索会话输出;回写输入需用户授权。</summary>
    Terminal.ITerminalApi Terminal { get; }

    /// <summary>
    /// 协议能力:注册插件自带的远程文件协议(与 SSH/SFTP/FTP 同为连接配置页的一等公民)。
    /// 仅 <c>inProcess</c> 宿主模式可用,见 <see cref="IProtocolsApi" />。
    /// </summary>
    IProtocolsApi Protocols { get; }

    /// <summary>
    /// 工作台能力:注册插件自建的**非文件型**连接类型(Redis、MySQL、…),
    /// 由插件全权渲染会话文档。仅 <c>inProcess</c> 宿主模式可用,见
    /// <see cref="Workspaces.IWorkspacesApi" />。
    /// </summary>
    Workspaces.IWorkspacesApi Workspaces { get; }

    /// <summary>
    /// 宿主要求停机时触发的令牌。插件的后台循环必须监听它;
    /// 触发后上下文的能力调用可能开始失败,应尽快收尾。
    /// </summary>
    CancellationToken Shutdown { get; }
}

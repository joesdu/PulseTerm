using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.TestPlugin;

/// <summary>
/// 插件运行时用例的通用夹具。**不是示例代码** —— 示例在工具链仓库的 HelloWorld,
/// 这里只提供用例断言得到的那几个可观测事实:
/// <list type="bullet">
///   <item>激活时写一次存储 —— 数据目录里出现 <c>storage.json</c> 是"真的激活过了"的
///     磁盘证据,惰性激活用例正是靠它区分"发现了"与"装载并激活了"。</item>
///   <item>注册几条命令 —— 命令 id 进得了宿主的命令表,且
///     <c>onCommand:</c> 惰性激活触发得到。</item>
///   <item>停用时把状态清干净 —— 隔离进程回收与启停用例会连着激活/停用好几轮。</item>
/// </list>
/// 刻意零第三方依赖(只引 SDK):用例大量使用"只复制入口 dll 到临时插件根"的铺法。
/// </summary>
[VelaPlugin]
public sealed class TestFixturePlugin : IVelaPlugin
{
    /// <summary>本夹具的插件 id,与 <c>plugin.json</c> 一致。用例直接引用这个常量,免得散落字面量。</summary>
    public const string Id = "velashell.test-fixture";

    /// <summary>入口程序集文件名。用例铺临时插件目录时要写进 manifest 的 <c>entry</c>。</summary>
    public const string EntryFileName = "VelaShell.TestPlugin.dll";

    private IPluginContext? _context;

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        // 存储写入 = 激活的磁盘证据(见类型注释)。顺带记激活次数,
        // 隔离进程回收用例靠它确认"是重新激活而不是从没停过"。
        int activations = await context.Storage.GetAsync<int>("activations", cancellationToken) + 1;
        await context.Storage.SetAsync("activations", activations, cancellationToken);
        context.Log.Info($"Test fixture activated (#{activations}) under host {context.Host.AppVersion}.");

        context.Commands.Register(new(
            $"{context.PluginId}.list-sessions", "Test Fixture: List Sessions", "Test Fixture",
            ListSessionsAsync));
        context.Commands.Register(new(
            $"{context.PluginId}.noop", "Test Fixture: No-op", "Test Fixture",
            _ => Task.CompletedTask));
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        // 命令与事件订阅由宿主自动清理;这里只收尾自己的引用,
        // 免得停用后仍握着上下文,把可收集 ALC 钉在内存里。
        _context?.Log.Info("Test fixture deactivated.");
        _context = null;
        return Task.CompletedTask;
    }

    private async Task ListSessionsAsync(CancellationToken cancellationToken)
    {
        IPluginContext context = _context!;
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken);
        context.Log.Info($"Sessions: {sessions.Count}");
    }
}

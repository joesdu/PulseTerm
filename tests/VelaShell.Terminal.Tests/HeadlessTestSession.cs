using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 全程序集共用的 headless 会话。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是每程序集一个,而不是每测试类一个。</b>本项目的 12 个 headless UI 测试类原先
/// 各自 <c>StartNew</c> + <c>Dispose</c> 一个会话。Avalonia 的平台注册与调度器线程是进程级的,
/// 多个会话的建立/拆除交错进行时,<c>HeadlessUnitTestSession.Dispose()</c> 会在
/// <c>[ClassCleanup]</c> 里抛 <see cref="NullReferenceException" /> —— 某个会话拆掉了另一个
/// 还在用的全局状态。表现为"整套跑偶发挂一两条 ClassCleanup,单独跑某个类却永远是绿的",
/// 三次全量里能中两次。
/// </para>
/// <para>
/// 改成全程序集一个之后,建立与拆除各只发生一次,这条竞争彻底消失;顺带也省掉了 11 次
/// Avalonia 应用启动。各测试类保留 <c>_session</c> 这个名字(指向本类),故用法不变。
/// </para>
/// <para>
/// 注意:headless UI 测试共用同一条 UI 线程,窗口用完必须关,异步准备要放在 Dispatch 体内
/// (见各测试类)。这条纪律与本会话是否共享无关,但共享之后一处泄漏会影响全程序集,更要守住。
/// </para>
/// </remarks>
[TestClass]
public class HeadlessTestSession
{
    private static HeadlessUnitTestSession? _shared;

    /// <summary>共用会话;在 <see cref="Init" /> 之后、<see cref="Cleanup" /> 之前有效。</summary>
    public static HeadlessUnitTestSession Current =>
        _shared ?? throw new InvalidOperationException(
            "headless 会话尚未建立 —— [AssemblyInitialize] 没跑到,通常是测试宿主没有加载本程序集的初始化。");

    /// <summary>建立全程序集唯一的 headless 会话。</summary>
    [AssemblyInitialize]
    public static void Init(TestContext _) => _shared = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    /// <summary>拆除会话。</summary>
    [AssemblyCleanup]
    public static void Cleanup()
    {
        _shared?.Dispose();
        _shared = null;
    }
}

/// <summary>headless 测试宿主应用。</summary>
public class HeadlessTestApp : Application
{
    /// <summary>菜单的模板由主题提供:缺了它 MenuItem 无模板、无命中区,真实点击测不了。</summary>
    public override void Initialize() => Styles.Add(new FluentTheme());

    /// <summary>供 <see cref="HeadlessUnitTestSession" /> 反射调用。</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

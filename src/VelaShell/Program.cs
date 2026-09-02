using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using ReactiveUI.Avalonia;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Startup;
using VelaShell.Services;
using VelaShell.Services.Update;

// ReSharper disable InconsistentNaming

namespace VelaShell;

internal static partial class Program
{
    // 整个进程生命周期内持有,以便第二次启动能检测到我们。退出时释放。
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 外置换版模式必须排在最前:此时跑的是暂存目录里解包出来的新版应用,它的活儿是
        // 无界面的几秒钟文件搬运,起 Avalonia 既没意义又会拖慢用户等待(见 UpdateRunner)。
        // 它等主进程退出后换版、拉起应用,然后本进程结束——不走下面任何一行。
        if (UpdateRunner.TryRun(args))
        {
            return;
        }

        // 插件开发用的启动参数(--dev-root / --wait-debugger / --data-root / --dev-watch)。
        // 必须排在任何一次存储路径解析之前:--data-root 连带切换单实例互斥键、数据库位置与
        // 全部数据文件,晚一步设就会有半套路径指向旧根。
        var startup = VelaShellStartupArguments.Parse(args);
        VelaShellStartupArguments.Current = startup;
        if (startup.DataRoot is { } dataRoot)
        {
            VelaShellStoragePaths.RootDirectoryOverride = dataRoot;
        }

        NormalizeWorkingDirectory();

        // 启用旧代码页(GBK、Big5、Shift_JIS 等)以支持终端编码选项。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InstallGlobalExceptionGuards();

        // Xshell 兼容登录(-url / -f / ssh:// 协议关联):第三方安全软件、堡垒机网页是按 Xshell 的
        // 调用约定拉起终端的。解析放在这里,是因为下面的单实例分支要用它决定「转发还是提示」。
        ExternalLaunchRequest? launch = XshellLaunchParser.TryParse(args);

        // 每个用户只允许运行一个实例:SonnetDB 对其 WAL 持有独占锁,否则第二个进程会在启动时
        // 因文件被占用而抛出 IOException 崩溃。改为在启动前检测运行中的实例,并以友好提示干净退出。
        // 自更新后重启(--after-update)时,前一个进程仍在关闭中,因此等待其释放锁,而非立即退出。
        bool afterUpdate = args.Contains("--after-update", StringComparer.Ordinal);
        if (!TryAcquireSingleInstanceLock(afterUpdate ? TimeSpan.FromSeconds(15) : TimeSpan.Zero))
        {
            // 已经有实例在跑:把这次拉起交给它(开标签页/唤到前台),自己干净退出。
            // 只有转发确实失败了才退回提示框 —— 否则用户在网页上点了登录,这边毫无反应。
            if (TryForwardToRunningInstance(launch))
            {
                return;
            }
            ShowMessage(Strings.Get("Boot_AlreadyRunning"), "VelaShell");
            return;
        }
        if (launch is not null)
        {
            // 主窗口还没有,先入队;App 起来后 Attach 时一并放行。
            ExternalLaunchInbox.Publish(launch);
        }
        try
        {
            // 首次运行新版时，先完整迁移旧 LocalAppData 数据。失败则中止启动，绝不能在
            // 新目录另建一个空数据库，让用户误以为原数据丢失。
            VelaShellDataMigration.MigrateIfNeeded(new VelaShellStoragePaths());
            FinalizePendingUpdate();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (IsDatabaseLockedFailure(ex))
        {
            // 数据库被别的进程占着,是可预期且用户自己能处理的情况,不该以崩溃收场。
            // 上面的单实例守卫挡不住这一类:持锁者可能压根不是一个正常实例(Avalonia 预览器 /
            // VS 设计器曾经就会 —— 见 App.Initialize 的设计期守卫),也可能是上一个实例
            // 尚未退干净;两种情况下互斥锁都是空的。给一句说得清的提示后干净退出,不再 rethrow。
            Trace.WriteLine($"[VelaShell] Database locked at startup: {ex}");
            ShowMessage(Strings.Get("Boot_DatabaseLocked"), Strings.Get("Boot_StartupErrorTitle"));
        }
        catch (Exception ex)
        {
            // 最后手段:向测试人员弹出可读对话框,而非原始的 .NET 崩溃框。
            Trace.WriteLine($"[VelaShell] Fatal startup error: {ex}");
            ShowMessage(Strings.Format("Boot_StartupFailed", ex.Message), Strings.Get("Boot_StartupErrorTitle"));
            throw;
        }
        finally
        {
            ReleaseSingleInstanceLock();
        }
    }

    /// <summary>
    /// 把进程的工作目录钉在应用自身目录上。
    /// </summary>
    /// <remarks>
    /// 开机自启走的是 <c>HKCU\...\Run</c>,而 Explorer 拉起 Run 键里的程序时会把子进程的工作目录
    /// 设成 <c>C:\Windows\System32</c>(继承它自己的 CWD)。应用本身从不依赖 CWD——所有持久化路径
    /// 都以 <c>~/.velashell</c> 或 <see cref="Environment.ProcessPath" /> 为根——但
    /// 系统层面仍有两处会踩到它:没指定起始目录的文件对话框会停在 CWD,设置里若填了相对路径也按
    /// CWD 解析。二者都会让 System32 莫名其妙地冒出来(#120)。这里定死一次即可根除。
    /// 外置换版进程不走这条路径(它在上面就返回了),其临时目录语义不受影响。
    /// </remarks>
    private static void NormalizeWorkingDirectory()
    {
        try
        {
            if (Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } appDirectory
                && Directory.Exists(appDirectory))
            {
                Directory.SetCurrentDirectory(appDirectory);
            }
        }
        catch
        {
            // 目录不可访问(受限环境/只读介质)时保持原样:CWD 不参与任何功能路径。
        }
    }

    /// <summary>
    /// 完成上一轮留下的自更新:清掉换版备份与解包内容,或回滚中途中断的换版,并顺带清扫
    /// 更新器临时目录与历史版本遗留的 *.old。刚退出的旧进程或更新器进程可能仍持有某些文件,
    /// 因此失败会在后台按退避节奏重试约两分钟,仍不成的留待下次启动。永不抛异常。
    /// </summary>
    private static void FinalizePendingUpdate()
    {
        if (AppPackaging.IsPackaged)
        {
            // 商店版从不自更新,安装目录(WindowsApps)也只读:没有换版现场要收拾,
            // 更不该每次启动都去递归枚举一遍安装目录。
            return;
        }
        string appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        UpdateApplier applier = new(appDir);
        if (applier.TryFinalizeStartup())
        {
            return;
        }
        // 退避而非固定 1 秒 ×10:占用方多半是正在退出的进程或杀软扫描,几秒内就放手;
        // 真拖久了(旧机械盘上的全盘扫描)也得给够时间,否则残留就要多留一整轮启动。
        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(1);
            TimeSpan elapsed = TimeSpan.Zero;
            while (elapsed < TimeSpan.FromMinutes(2))
            {
                await Task.Delay(delay);
                elapsed += delay;
                if (applier.TryFinalizeStartup())
                {
                    return;
                }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        });
    }

    /// <summary>
    /// 获取一个以本地数据目录为键、作用域限于会话的命名互斥体。当已有其他实例持有时返回 false
    /// —— 常见情形是应用已打开时的双击启动。以存储路径为键,使不同的 Windows 用户
    /// (不同的用户主目录)各自独立运行。使用 Local 命名空间(无需 Global 那样的
    /// SeCreateGlobalPrivilege);罕见的同用户跨会话冲突,会在之后由 SonnetDB 的文件锁与启动错误
    /// 对话框捕获,而非静默继续直至崩溃。
    /// </summary>
    /// <summary>
    /// 判断启动失败是否源于「数据库文件被其他进程占用」(SonnetDB 对其 WAL 持独占锁)。
    /// </summary>
    /// <remarks>
    /// 按 <c>.SDBWAL</c> 这个扩展名匹配而不是比对异常文本:共享冲突的 IOException 消息本身
    /// 会随系统语言变化,但消息里带的那个文件路径不会 —— 用路径判断才跨语言可靠。
    /// 逐层看 InnerException:DI 是在工厂委托里构造引擎的,异常常被包了一层。
    /// </remarks>
    private static bool IsDatabaseLockedFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException
                && current.Message.Contains(".SDBWAL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryAcquireSingleInstanceLock(TimeSpan waitTimeout)
    {
        try
        {
            string root = new VelaShellStoragePaths().RootDirectory;
            // 互斥体与拉起管道同键(都以数据根为源),两者才能指向同一个「实例」。
            _singleInstanceMutex = new(false, $"Local\\VelaShell-{SingleInstanceLaunchChannel.KeyFor(root)}");
            try
            {
                if (!_singleInstanceMutex.WaitOne(waitTimeout))
                {
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // 前持有者未释放即终止(例如崩溃)。现在归我们所有 —— 继续。
            }
            return true;
        }
        catch
        {
            // 绝不让该守卫自身阻塞启动;退路为允许启动。
            return true;
        }
    }

    /// <summary>
    /// 把这次启动交给已在运行的实例:带目标的转成一次连接请求,不带的(双击图标、托盘里已隐藏)
    /// 转成一次「唤到前台」。对方确认收下才返回 true。
    /// </summary>
    private static bool TryForwardToRunningInstance(ExternalLaunchRequest? launch)
    {
        try
        {
            string root = new VelaShellStoragePaths().RootDirectory;
            ExternalLaunchRequest request = launch ?? new ExternalLaunchRequest { Kind = ExternalLaunchKind.Activate };
            // 5 秒:对方可能正忙于启动(插件激活、数据库打开),给足握手时间;
            // 真连不上时这点等待也不至于让用户觉得点了个死链接。
            return SingleInstanceLaunchChannel.TrySend(root, request, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Forwarding to running instance failed: {ex}");
            return false;
        }
    }

    private static void ReleaseSingleInstanceLock()
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // 尽力而为:进程卸载时无论如何都会释放句柄。
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>在 Windows 上显示原生消息框;其他平台退路为 Trace。</summary>
    private static void ShowMessage(string text, string caption)
    {
        if (OperatingSystem.IsWindows())
        {
            const uint MB_OK = 0x0, MB_ICONINFORMATION = 0x40;
            MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONINFORMATION);
        }
        else
        {
            Trace.WriteLine($"[VelaShell] {caption}: {text}");
        }
    }

    /// <summary>
    /// 最后手段的守卫:使后台/响应式失败(例如命令触发的 SSH 认证异常)被记录,
    /// 而非终止整个客户端。
    /// </summary>
    private static void InstallGlobalExceptionGuards()
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Trace.WriteLine($"[VelaShell] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Trace.WriteLine($"[VelaShell] Unhandled domain exception: {e.ExceptionObject}");
    }

    /// <summary>
    /// 解析渲染后端(设置 → 外观 → 硬件加速)。渲染模式必须在 Avalonia 初始化之前定下来,
    /// 那时 DI 与 SonnetDB 都还没起来,因此读的是设置保存时镜像出来的单行小文件,
    /// 而不是数据库 —— 启动路径上只有一次 File.ReadAllText。
    /// </summary>
    /// <remarks>
    /// 关掉硬件加速能省下约 170MB 常驻内存:GPU 后端会把显卡驱动的一整套模块映射进本进程
    /// (Intel 核显上 igc64.dll 一个就 82MB)。代价是绘制交给 CPU。
    /// </remarks>
    private static IReadOnlyList<Win32RenderingMode> ResolveRenderingMode()
    {
        // 软件渲染兜底始终留在列表末尾:GPU 初始化失败(远程桌面、驱动异常)时不至于起不来。
        IReadOnlyList<Win32RenderingMode> gpu = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software];
        IReadOnlyList<Win32RenderingMode> software = [Win32RenderingMode.Software];
        if (Environment.GetEnvironmentVariable("VELASHELL_SOFTWARE_RENDER") == "1")
        {
            return software; // 测量与排障用的强制开关
        }
        try
        {
            string path = new VelaShellStoragePaths().RenderModeFile;
            return File.Exists(path) && File.ReadAllText(path).Trim() is "software" ? software : gpu;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return gpu;
        }
    }

    // Avalonia 配置,勿删除;可视化设计器也用到。
    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .With(new Win32PlatformOptions { RenderingMode = ResolveRenderingMode() })
#if LINUX
                  .UseWayland()
#endif
                  .WithInterFont()
                  // 内置 Cascadia Mono(fonts:VelaShell 键,四静态字重):Linux/macOS 不自带,
                  // 内置才能三平台一致的终端字形。CJK 走系统回退(YaHei/PingFang/Noto)。
                  // 刻意不内置 Cascadia Next SC/TC/JP:它目前是 pre-release 且只发布变量字体,
                  // fvar 默认字重 200(极细),而 Avalonia 只按默认轴位置渲染、不枚举命名实例——
                  // 不改字体文件就没法用;等微软发布静态字重后再内置。
                  .ConfigureFonts(fontManager => fontManager.AddFontCollection(
                      new Avalonia.Media.Fonts.EmbeddedFontCollection(
                          new Uri("fonts:VelaShell"),
                          new Uri("avares://VelaShell.Controls/Assets/Fonts"))))
                  .LogToTrace()
                  .UseReactiveUI(_ => { });
}

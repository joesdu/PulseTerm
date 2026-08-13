using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Pty;

/// <summary>
/// 本地终端传输层(§12 P1-1):Windows ConPTY(CreatePseudoConsole)实现
/// <see cref="IShellStreamWrapper" />,把 PowerShell/CMD/WSL/Git Bash 等本地 shell
/// 接进既有的 SshTerminalBridge → VT 引擎 → 自绘控件管线,SSH 侧零改动。
/// 管道拓扑:我们写 _input(子进程 stdin ← ConPTY),读 _output(子进程 stdout → ConPTY,
/// 输出为 UTF-8 的 VT 序列)。子进程退出时关闭伪控制台,读端得到断管 → 归一化为 EOF(返回 0),
/// 桥的读循环据此走远端关闭路径(标签变为已断开,可重开)。
/// </summary>
[SupportedOSPlatform(nameof(OSPlatform.Windows))]
public sealed partial class ConPtyShellStream : IShellStreamWrapper
{
    private readonly IntPtr _console;
    private readonly FileStream _input;
    private readonly IntPtr _job;
    private readonly FileStream _output;
    private readonly Process _process;
    private readonly Lock _teardownGate = new();
    private volatile bool _closed;
    private bool _disposed;

    private ConPtyShellStream(IntPtr console, FileStream input, FileStream output, Process process, IntPtr job)
    {
        _console = console;
        _input = input;
        _output = output;
        _process = process;
        _job = job;
    }

    /// <summary>桥不轮询数据可用性(读循环阻塞在 <see cref="ReadAsync" /> 上),故恒为 <c>false</c>。</summary>
    public bool DataAvailable => false; // 桥不轮询;读循环阻塞在 ReadAsync 上。

    /// <summary>流是否可读:未关闭且未释放时为 <c>true</c>。</summary>
    public bool CanRead => !_closed && !_disposed;

    /// <summary>流是否可写:未关闭且未释放时为 <c>true</c>。</summary>
    public bool CanWrite => !_closed && !_disposed;

    /// <summary>本地 shell 无登录握手,故恒返回 <c>null</c>(不参与 Expect 匹配)。</summary>
    public string? Expect(string regex, TimeSpan timeout) => null; // 本地 shell 无登录握手。

    /// <summary>向子进程 stdin 写入一行文本(以回车结尾)。</summary>
    public void WriteLine(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\r");
        WriteAsync(bytes, 0, bytes.Length, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>从子进程输出读取字节;断管或句柄关闭均归一化为 EOF(返回 0)。</summary>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            return await _output.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 断管/句柄关闭 = 会话结束,归一化为 EOF。
            return 0;
        }
    }

    /// <summary>异步向子进程 stdin 写入并刷新;流已关闭或子进程已退出时静默丢弃。</summary>
    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_closed || _disposed)
        {
            return;
        }
        try
        {
            await _input.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 子进程已退出:丢弃输入。
        }
    }

    /// <summary>刷新输入缓冲区,把待写数据推给子进程 stdin。</summary>
    public void Flush()
    {
        try
        {
            _input.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 忽略
        }
    }

    /// <summary>调整伪控制台的列/行尺寸(数值钳制在 2–500)。</summary>
    public void Resize(int columns, int rows)
    {
        if (_closed || _disposed)
        {
            return;
        }
        _ = NativeMethods.ResizePseudoConsole(_console, new()
        {
            X = (short)Math.Clamp(columns, 2, 500),
            Y = (short)Math.Clamp(rows, 2, 500)
        });
    }

    /// <summary>释放会话:优先经 Job Object 秒杀整棵进程树,关闭伪控制台与管道句柄。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // 用户关标签:优先关掉 Job Object —— 内核一次性终止整棵进程树(O(1),不枚举系统进程),
        // 避免 Process.Kill(true) 遍历全部进程带来的 UI 卡顿与成批 Win32Exception。
        // job 创建失败(极少数环境)时退回杀直接子进程(Kill() 亦不枚举进程树)。
        try
        {
            if (_job != IntPtr.Zero)
            {
                NativeMethods.TerminateJobObject(_job, 0);
            }
            else if (!_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch
        {
            // 进程可能已自行退出。
        }
        CloseConsole();
        try
        {
            _output.Dispose();
        }
        catch
        {
            // 忽略
        }
        if (_job != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_job);
        }
        _process.Dispose();
    }

    /// <summary>启动本地 shell 并挂上伪控制台。commandLine 含参数(如 "bash.exe --login -i")。</summary>
    public static ConPtyShellStream Start(string commandLine, string? workingDirectory, int columns, int rows)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("本地终端目前仅支持 Windows(ConPTY)。");
        }
        columns = Math.Clamp(columns, 2, 500);
        rows = Math.Clamp(rows, 2, 500);

        // 不指定工作目录时落到用户主目录,而不是让 CreateProcess 继承本进程的工作目录:
        // 后者由外部环境决定(开机自启时曾是 C:\Windows\System32),绝不该成为 shell 的起点(#120)。
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        // 两对匿名管道:input(child ← us)、output(us ← child)。
        if (!NativeMethods.CreatePipe(out IntPtr inputRead, out IntPtr inputWrite, IntPtr.Zero, 0))
        {
            throw new InvalidOperationException($"CreatePipe(input) failed: {Marshal.GetLastWin32Error()}");
        }
        if (!NativeMethods.CreatePipe(out IntPtr outputRead, out IntPtr outputWrite, IntPtr.Zero, 0))
        {
            NativeMethods.CloseHandle(inputRead);
            NativeMethods.CloseHandle(inputWrite);
            throw new InvalidOperationException($"CreatePipe(output) failed: {Marshal.GetLastWin32Error()}");
        }
        int hr = NativeMethods.CreatePseudoConsole(new() { X = (short)columns, Y = (short)rows },
            inputRead, outputWrite, 0, out IntPtr console);
        // 伪控制台会复制管道端点(官方文档),我们这侧的子进程端句柄立即关闭;
        // 关掉 outputWrite 也是读端在 conhost 退出后能拿到 EOF 的前提。
        NativeMethods.CloseHandle(inputRead);
        NativeMethods.CloseHandle(outputWrite);
        if (hr != 0)
        {
            NativeMethods.CloseHandle(inputWrite);
            NativeMethods.CloseHandle(outputRead);
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");
        }
        var startupInfo = new NativeMethods.STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();
        IntPtr attrListSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        startupInfo.lpAttributeList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref attrListSize) ||
                !NativeMethods.UpdateProcThreadAttribute(startupInfo.lpAttributeList, 0,
                    NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException($"ProcThreadAttribute setup failed: {Marshal.GetLastWin32Error()}");
            }
            if (!NativeMethods.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    NativeMethods.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero,
                    string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                    ref startupInfo, out NativeMethods.PROCESS_INFORMATION processInfo))
            {
                throw new InvalidOperationException($"无法启动本地 shell(CreateProcess {Marshal.GetLastWin32Error()}):{commandLine}");
            }

            // 把刚启动的 shell 收进「随句柄关闭而整树终止」的 Job,关标签时据此秒杀进程树。
            // 需在关闭进程句柄前分配(AssignProcessToJobObject 要拿有效的进程句柄)。
            IntPtr job = CreateKillOnCloseJob(processInfo.hProcess);

            NativeMethods.CloseHandle(processInfo.hThread);
            NativeMethods.CloseHandle(processInfo.hProcess);
            var input = new FileStream(new(inputWrite, true), FileAccess.Write, 4096);
            var output = new FileStream(new(outputRead, true), FileAccess.Read, 16384);
            var process = Process.GetProcessById((int)processInfo.dwProcessId);
            var stream = new ConPtyShellStream(console, input, output, process, job);

            // 子进程退出(exit / 崩溃)→ 关伪控制台,读端断管 → EOF → 标签断开。
            // 注意:ClosePseudoConsole 会立刻终止 conhost 的输出泵,退出瞬间可能还有
            // 未写入管道的尾部输出(如 `cmd /c echo` 的结果),留一个短排空窗口再关。
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ = Task.Run(async () =>
            {
                await Task.Delay(300).ConfigureAwait(false);
                stream.CloseConsole();
            });
            return stream;
        }
        catch
        {
            NativeMethods.ClosePseudoConsole(console);
            NativeMethods.CloseHandle(inputWrite);
            NativeMethods.CloseHandle(outputRead);
            throw;
        }
        finally
        {
            NativeMethods.DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
        }
    }

    /// <summary>关闭伪控制台并断开读端(幂等):由子进程退出回调与 Dispose 共用。</summary>
    private void CloseConsole()
    {
        lock (_teardownGate)
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            NativeMethods.ClosePseudoConsole(_console);
            try
            {
                _input.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>把进程收进一个 <c>KILL_ON_JOB_CLOSE</c> 的 Job Object;失败(如受限环境)返回 <see cref="IntPtr.Zero" />。</summary>
    private static IntPtr CreateKillOnCloseJob(IntPtr processHandle)
    {
        IntPtr job = NativeMethods.CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int length = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr infoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            if (!NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformation, infoPtr, (uint)length) ||
                !NativeMethods.AssignProcessToJobObject(job, processHandle))
            {
                NativeMethods.CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }
        return job;
    }

    private static partial class NativeMethods
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        public const int JobObjectExtendedLimitInformation = 9;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, IntPtr lpName);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(IntPtr hObject);

        [LibraryImport("kernel32.dll")]
        public static partial int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [LibraryImport("kernel32.dll")]
        public static partial int ResizePseudoConsole(IntPtr hPC, COORD size);

        [LibraryImport("kernel32.dll")]
        public static partial void ClosePseudoConsole(IntPtr hPC);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [LibraryImport("kernel32.dll")]
        public static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        // EntryPoint 必须写全:LibraryImport 不做 DllImport 那套 CharSet 自动加 "W" 后缀,
        // 漏了就是运行期 EntryPointNotFoundException(编译期无感)。
        [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD
        {
            public short X;
            public short Y;
        }

        // 三个字符串字段本进程从不设置(始终 null),声明成指针即可让整个结构保持 blittable
        // —— LibraryImport 的源生成封送要求结构可直接按位传递,含 string 字段会拒绝生成。
        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}

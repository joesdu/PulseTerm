namespace VelaShell.PluginSdk.RemoteExec;

/// <summary>远程命令执行选项。</summary>
public sealed record ExecOptions
{
    /// <summary>执行超时,默认 30 秒,上限 10 分钟(宿主强制截断)。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>输出来自哪条流。</summary>
public enum ExecStream
{
    /// <summary>标准输出。</summary>
    StandardOutput,

    /// <summary>标准错误。</summary>
    StandardError
}

/// <summary>流式执行时回调的一行输出。</summary>
/// <param name="Stream">这一行来自哪条流。</param>
/// <param name="Line">一行文本(**不含**行尾换行)。</param>
public readonly record struct ExecOutput(ExecStream Stream, string Line);

/// <summary>流式执行选项。</summary>
public sealed record ExecStreamOptions
{
    /// <summary>
    /// 整体超时;<see langword="null" />(默认)表示**不限时**,由取消令牌决定何时收尾。
    /// <para>
    /// 与 <see cref="ExecOptions.Timeout" /> 的 10 分钟上限不同:流式执行的正常形态就是
    /// <c>docker logs -f</c> / <c>tail -F</c> 这类"跑到你不想看为止"的命令,给它一个
    /// 死线只会让界面在第 10 分钟莫名其妙地断流。代价是插件**必须**持有取消令牌并在
    /// 不再需要时触发它 —— 宿主同时按 <see cref="IRemoteExecApi.MaxConcurrentStreams" /> 兜底。
    /// </para>
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>是否也回调标准错误,默认是。</summary>
    public bool IncludeStandardError { get; init; } = true;
}

/// <summary>流式执行的收尾结果。</summary>
/// <param name="ExitCode">远端进程的退出码。</param>
/// <param name="Lines">总共回调了多少行。</param>
public sealed record ExecStreamResult(int ExitCode, long Lines);

/// <summary>远程命令执行结果。</summary>
/// <param name="Output">命令的标准输出(UTF-8 解码)。</param>
public sealed record ExecResult(string Output)
{
    /// <summary>
    /// 命令的标准错误(UTF-8 解码)。
    /// <para>
    /// **单独一条流,不并进 <see cref="Output" />。** 绝大多数命令行工具把错误写在这里
    /// (<c>docker</c>、<c>systemctl</c>、<c>git</c> 都是),把两条流拌在一起会让解析
    /// <c>--format json</c> 这类结构化输出的插件被一行警告噎死。
    /// </para>
    /// </summary>
    public string Error { get; init; } = "";

    /// <summary>远端进程的退出码。</summary>
    public int ExitCode { get; init; }

    /// <summary>退出码为 0。</summary>
    public bool IsSuccess => ExitCode == 0;

    /// <summary>
    /// 给人看的一行失败原因:优先取标准错误的第一行非空文本,没有就退回标准输出,
    /// 再没有就报退出码。用于把失败**如实**呈现到界面上,而不是一句"操作失败"。
    /// </summary>
    public string FailureText
    {
        get
        {
            foreach (string candidate in new[] { Error, Output })
            {
                foreach (string line in candidate.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length > 0)
                    {
                        return trimmed;
                    }
                }
            }
            return $"exit {ExitCode}";
        }
    }
}

/// <summary>
/// 远程执行能力:在既有会话上经独立 exec 通道执行命令 ——
/// 不进用户终端、不污染用户 shell 历史与环境变量。
/// 会话无效时抛 <see cref="PluginSessionNotFoundException" />。
/// <para>
/// 两种形态:<see cref="RunAsync" /> 跑完拿结果(探测类命令,<c>docker ps</c> /
/// <c>systemctl status</c>);<see cref="StreamAsync" /> 边跑边按行回调
/// (<c>docker logs -f</c> / <c>docker events</c> / <c>tail -F</c> 这类长驻命令)。
/// 交互式命令(要伪终端、要键盘输入的)两者都不适用,那是终端标签的事。
/// </para>
/// </summary>
public interface IRemoteExecApi
{
    /// <summary>
    /// 同时在飞的流式执行上限(每插件)。超过时 <see cref="StreamAsync" /> 抛
    /// <see cref="InvalidOperationException" />。
    /// </summary>
    /// <remarks>
    /// 流是不限时的,而每条流占着一个 SSH 通道。没有上限的话,一个忘了取消的插件
    /// 能在几分钟内把对端的 <c>MaxSessions</c> 耗光 —— 那时坏掉的不是插件,是用户的连接。
    /// </remarks>
    public const int MaxConcurrentStreams = 4;

    /// <summary>
    /// 执行命令并等待完成。超时抛 <see cref="TimeoutException" />。
    /// <para>
    /// **不会**因为退出码非 0 而抛异常 —— 命令跑失败是一种正常结果,不是一个异常事件。
    /// 判成败请看 <see cref="ExecResult.IsSuccess" />。
    /// </para>
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="command">命令行。</param>
    /// <param name="options">执行选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标准输出、标准错误与退出码。</returns>
    Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式执行:边跑边把输出**按行**回调,进程结束后返回退出码。
    /// <para>
    /// 用于长驻命令。取消令牌触发时宿主向远端进程发 <c>TERM</c> 并关闭通道,
    /// 方法抛 <see cref="OperationCanceledException" />;
    /// <see cref="ExecStreamOptions.Timeout" /> 到点则抛 <see cref="TimeoutException" />。
    /// </para>
    /// <para>
    /// <paramref name="output" /> 在**非 UI 线程**被回调,且可能非常频繁
    /// (宿主不做节流 —— 日志的价值就在于即时);插件自己决定要不要攒批。
    /// </para>
    /// <para>
    /// <b>不要传 <see cref="Progress{T}" />。</b> 宿主是在读行的那个线程上按顺序直接调
    /// <see cref="IProgress{T}.Report" /> 的,所以**只要你的实现是同步的,行序就是保证的**;
    /// 而 <see cref="Progress{T}" /> 会把每次回调 <c>Post</c> 到捕获的同步上下文或线程池,
    /// 既丢掉顺序也可能并发进入 —— 对进度百分比无所谓,对一屏日志则是灾难。
    /// 请自己写一个直接转发的 <see cref="IProgress{T}" />(几行而已)。
    /// </para>
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="command">命令行。</param>
    /// <param name="options">流式选项;为 null 用默认值(不限时、含标准错误)。</param>
    /// <param name="output">逐行输出的接收器。</param>
    /// <param name="cancellationToken">取消令牌(**必须**持有并在不再需要时触发)。</param>
    /// <returns>退出码与行数。</returns>
    /// <exception cref="NotSupportedException">宿主实现不支持流式执行时。</exception>
    Task<ExecStreamResult> StreamAsync(
        string sessionId,
        string command,
        ExecStreamOptions? options,
        IProgress<ExecOutput> output,
        CancellationToken cancellationToken = default)
        // 默认实现只为**源兼容**:第三方的测试替身在 SDK 加了这个方法之后仍然能编译。
        // 刻意不退化成"跑完再一次性回调" —— 那会让 `docker logs -f` 挂到超时后
        // 把整段丢掉,一个比"不支持"难查得多的现象。
        => throw new NotSupportedException("This host does not implement streaming remote execution.");
}

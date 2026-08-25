using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.Infrastructure.Startup;

/// <summary>
/// 单实例之间的拉起转发通道。第二个进程被拉起时(网页点了 <c>ssh://</c>、堡垒机执行了
/// <c>VelaShell.exe -url …</c>)不能只弹一句「已在运行」了事 —— 那样用户点了半天没反应;
/// 它必须把请求交给正在运行的那个实例,由后者开标签页,然后自己干净退出。
/// <para>
/// 管道两端都带 <see cref="PipeOptions.CurrentUserOnly" />:Windows 上把 ACL 收成仅当前用户,
/// Unix 上把 socket 文件权限收成 0700,并且客户端会核对服务端属主 —— 同机的另一个用户既连不上、
/// 也伪造不了一个假服务端来钓走一次性密码。同用户下的进程本就等价于用户自己,那道边界靠的是
/// 应用内的确认弹窗,不是这里。
/// </para>
/// </summary>
public sealed class SingleInstanceLaunchChannel : IDisposable
{
    /// <summary>一次请求的上限;超出直接断开。防的是同用户下某个跑飞的进程把内存灌满。</summary>
    private const int MaxPayloadBytes = 64 * 1024;

    private readonly CancellationTokenSource _cts = new();
    private readonly string _pipeName;
    private readonly Action<ExternalLaunchRequest> _handler;

    private SingleInstanceLaunchChannel(string pipeName, Action<ExternalLaunchRequest> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    /// <summary>
    /// 单实例标识:以数据根目录为键(与 <c>Program</c> 的互斥体同源),
    /// 这样 <c>--data-root</c> 起的开发实例与正式实例互不打扰。
    /// </summary>
    public static string KeyFor(string storageRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storageRoot.ToLowerInvariant())))[..16];

    /// <summary>该数据根对应的管道名。</summary>
    public static string PipeNameFor(string storageRoot) => $"VelaShell-{KeyFor(storageRoot)}-launch";

    /// <summary>在当前(持锁的)实例里起监听。失败返回 <see langword="null" />,不影响应用启动。</summary>
    public static SingleInstanceLaunchChannel? StartServer(string storageRoot, Action<ExternalLaunchRequest> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var channel = new SingleInstanceLaunchChannel(PipeNameFor(storageRoot), handler);
        try
        {
            _ = Task.Run(() => channel.ListenAsync(channel._cts.Token));
            return channel;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Launch channel unavailable: {ex.Message}");
            channel.Dispose();
            return null;
        }
    }

    /// <summary>
    /// 把请求投给正在运行的实例。**返回 true 才代表对方确认收下了**:
    /// 调用方据此决定是静默退出,还是退回到「已在运行」提示 —— 绝不能投失败了还假装成功,
    /// 那就成了点了没反应。
    /// </summary>
    public static bool TrySend(string storageRoot, ExternalLaunchRequest request, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeNameFor(storageRoot), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect((int)Math.Max(timeout.TotalMilliseconds, 1));
            byte[] payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(request, LaunchJsonContext.Default.ExternalLaunchRequest) + "\n");
            if (payload.Length > MaxPayloadBytes)
            {
                return false;
            }
            client.Write(payload);
            client.Flush();
            // 等一个字节的回执:确认对方**读到并接下了**这次请求。管道流不支持 ReadTimeout
            // (PipeStream.CanTimeout 恒为 false,赋值直接抛),所以超时只能靠取消令牌。
            using var deadline = new CancellationTokenSource(timeout);
            byte[] ack = new byte[1];
            int read = client.ReadAsync(ack, deadline.Token).AsTask().GetAwaiter().GetResult();
            return read == 1 && ack[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
                                      or ObjectDisposedException or InvalidOperationException
                                      or OperationCanceledException)
        {
            Trace.WriteLine($"[VelaShell] Forwarding launch request failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>停止监听。</summary>
    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放:无事可做。
        }
        _cts.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await AcceptOneAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 单次会话出错不能让通道就此哑掉(否则之后每次外部拉起都石沉大海);
                // 歇一下再开下一轮,避免出错时空转烧 CPU。
                Trace.WriteLine($"[VelaShell] Launch channel error: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task AcceptOneAsync(CancellationToken cancellationToken)
    {
        await using var server = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        string? json = await ReadLineAsync(server, cancellationToken).ConfigureAwait(false);
        ExternalLaunchRequest? request = null;
        if (json is { Length: > 0 })
        {
            try
            {
                request = JsonSerializer.Deserialize(json, LaunchJsonContext.Default.ExternalLaunchRequest);
            }
            catch (JsonException)
            {
                request = null; // 垃圾数据:回一个「没收下」,让对方退回提示框。
            }
        }
        try
        {
            server.WriteByte(request is null ? (byte)0 : (byte)1);
            server.Flush();
        }
        catch (IOException)
        {
            // 对方等不及先走了:请求照样处理(它已经交到我们手上了)。
        }
        if (request is not null)
        {
            _handler(request);
        }
    }

    /// <summary>读到换行为止,最多 <see cref="MaxPayloadBytes" /> 字节。</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        using var accumulated = new MemoryStream();
        while (accumulated.Length < MaxPayloadBytes)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            accumulated.Write(buffer, 0, newline >= 0 ? newline : read);
            if (newline >= 0)
            {
                break;
            }
        }
        return accumulated.Length == 0 ? null : Encoding.UTF8.GetString(accumulated.ToArray());
    }
}

/// <summary>拉起请求的 System.Text.Json 源生成上下文(不依赖反射,发布形态怎么变都稳)。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExternalLaunchRequest))]
internal sealed partial class LaunchJsonContext : JsonSerializerContext;

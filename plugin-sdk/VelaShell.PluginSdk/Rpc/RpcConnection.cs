using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;

namespace VelaShell.PluginSdk.Rpc;

/// <summary>
/// 命名管道(或任意双工流)上的轻量双向 RPC:请求/应答/通知,并发不排队,
/// 单读循环 + 写锁串行化写入。两端对称 —— 谁都能发请求与通知。
/// 断开时全部未决请求以 <see cref="RpcDisconnectedException" /> 失败并触发 <see cref="Disconnected" />。
/// </summary>
public sealed class RpcConnection : IAsyncDisposable
{
    /// <summary>单帧上限:fs/readAll 的 16MB 内容经 base64 后约 22MB,64MB 足够并挡住异常帧。</summary>
    private const int MaxFrameBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcMessage>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Func<string, JsonElement?, CancellationToken, Task<object?>>? _requestHandler;
    private Action<string, JsonElement?>? _notificationHandler;
    private Task? _readLoop;
    private int _disposed;
    private int _disconnectRaised;

    /// <summary>包装一条已连接的双工流(通常为 NamedPipe)。调用 <see cref="Start" /> 后才开始收发。</summary>
    public RpcConnection(Stream stream) => _stream = stream;

    /// <summary>连接断开(对端关闭/进程退出/协议错误)时触发一次。</summary>
    public event Action? Disconnected;

    /// <summary>设置请求处理器:返回值序列化为应答;抛出折叠为错误码应答。必须在 <see cref="Start" /> 前设置。</summary>
    public void SetRequestHandler(Func<string, JsonElement?, CancellationToken, Task<object?>> handler)
        => _requestHandler = handler;

    /// <summary>设置通知处理器(在线程池上调用,异常吞掉不断连)。必须在 <see cref="Start" /> 前设置。</summary>
    public void SetNotificationHandler(Action<string, JsonElement?> handler)
        => _notificationHandler = handler;

    /// <summary>启动读循环。</summary>
    public void Start() => _readLoop = Task.Run(ReadLoopAsync);

    /// <summary>发出请求并等待应答;应答载荷反序列化为 <typeparamref name="TResult" />。</summary>
    /// <exception cref="TimeoutException">超过 <paramref name="timeout" /> 未收到应答。</exception>
    /// <exception cref="RpcDisconnectedException">等待期间连接断开。</exception>
    public async Task<TResult?> RequestAsync<TResult>(string method, object? payload, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        long id = Random.Shared.NextInt64(); // 双端各自发号:随机 64 位避免两侧撞号,又无需协商号段
        var completion = new TaskCompletionSource<RpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            try
            {
                await WriteAsync(new()
                {
                    Type = "req",
                    Id = id,
                    Method = method,
                    Payload = Serialize(payload)
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 发送即失败 = 连接已断:与等待期断开同一语义,调用方只需处理一种异常。
                throw new RpcDisconnectedException("RPC connection closed.");
            }
            RpcMessage response;
            try
            {
                response = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"RPC request '{method}' timed out after {timeout.TotalSeconds:0}s.");
            }
            if (response.ErrorCode is not null || response.Error is not null)
            {
                throw RpcErrorCodes.ToException(response.ErrorCode, response.Error ?? "Remote call failed.");
            }
            return response.Payload is { } result ? result.Deserialize<TResult>(JsonOptions) : default;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>发出无应答通知(尽力而为:断开时静默丢弃)。</summary>
    public async Task NotifyAsync(string method, object? payload)
    {
        try
        {
            await WriteAsync(new() { Type = "evt", Method = method, Payload = Serialize(payload) }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or RpcDisconnectedException)
        {
            // 通知语义:对端不在就算了。
        }
    }

    private static JsonElement? Serialize(object? payload)
        => payload is null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions);

    private async Task WriteAsync(RpcMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        byte[] header = new byte[4];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                // 帧边界上的 EOF 是对端的正常关闭:安静退出,不制造异常
                //(进程停用/应用退出属于常规路径,调试输出不该被首发异常刷屏)。
                if (!await TryFillAsync(header, allowCleanEndOfStream: true).ConfigureAwait(false))
                {
                    return;
                }
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaxFrameBytes)
                {
                    throw new IOException($"Invalid RPC frame length {length}.");
                }
                byte[] body = new byte[length];
                // false = 本端正在关闭(帧读到一半),此时 body 是半截的,不能拿去反序列化。
                if (!await TryFillAsync(body, allowCleanEndOfStream: false).ConfigureAwait(false))
                {
                    return;
                }
                RpcMessage? message = JsonSerializer.Deserialize<RpcMessage>(body, JsonOptions);
                if (message is null)
                {
                    continue;
                }
                Dispatch(message);
            }
        }
        catch
        {
            // 帧中 EOF / 管道断开 / 协议错误:统一走断开路径。
        }
        finally
        {
            FailPendingAndNotify();
        }
    }

    /// <summary>
    /// 读满缓冲区。帧边界上的 0 字节(对端干净关闭)在 <paramref name="allowCleanEndOfStream" />
    /// 时返回 false;本端主动关闭同样返回 false;帧中途 EOF 是协议破损,抛 <see cref="IOException" />。
    /// </summary>
    /// <remarks>
    /// 这里【刻意不把 <c>_lifetime.Token</c> 传给 ReadAsync】。传了的话,
    /// <see cref="DisposeAsync" /> 取消令牌会把待决的读撕成 OperationCanceledException ——
    /// 虽然被读循环的 catch 吞掉、退出码照样是 0,但每次停用插件/退出应用都在调试器里
    /// 刷一条首发异常,而主动关闭是这条路径上最常规的分支,不该长成异常的样子。
    /// 实测(字节模式 + PipeOptions.Asynchronous,与 PluginProcessClient 同配置):
    /// 释放本端流会让待决的 ReadAsync 返回 0 字节,与对端关闭的表现一致,全程无异常。
    /// 因此收尾改由 <see cref="DisposeAsync" /> 的 <c>_stream.DisposeAsync()</c> 负责,
    /// 令牌只留给循环条件、请求处理器与限时等待。
    /// </remarks>
    private async Task<bool> TryFillAsync(Memory<byte> buffer, bool allowCleanEndOfStream)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            // 关闭是先取消令牌、后释放流:这一句挡住"取消之后还去读已释放的流",
            // 否则会拿到一条 ObjectDisposedException —— 换个类型的首发异常,等于没治。
            if (_lifetime.IsCancellationRequested)
            {
                return false;
            }
            int read = await _stream.ReadAsync(buffer[offset..]).ConfigureAwait(false);
            if (read == 0)
            {
                // 本端正在关闭:流被释放导致的 0 字节不是协议破损,安静收摊。
                if (_lifetime.IsCancellationRequested || (allowCleanEndOfStream && offset == 0))
                {
                    return false;
                }
                throw new IOException("RPC stream ended mid-frame.");
            }
            offset += read;
        }
        return true;
    }

    private void Dispatch(RpcMessage message)
    {
        switch (message.Type)
        {
            case "res" when message.Id is { } id && _pending.TryRemove(id, out TaskCompletionSource<RpcMessage>? pending):
                pending.TrySetResult(message);
                break;
            case "req" when message is { Id: { } requestId, Method: { } method }:
                // 请求在线程池处理:慢处理器不阻塞读循环,天然并发(排序敏感的域由处理器内部串行化)。
                _ = Task.Run(() => HandleRequestAsync(requestId, method, message.Payload));
                break;
            case "evt" when message.Method is { } eventMethod:
                _ = Task.Run(() =>
                {
                    try
                    {
                        _notificationHandler?.Invoke(eventMethod, message.Payload);
                    }
                    catch
                    {
                        // 通知处理器异常不影响连接。
                    }
                });
                break;
        }
    }

    private async Task HandleRequestAsync(long id, string method, JsonElement? payload)
    {
        RpcMessage response;
        try
        {
            Func<string, JsonElement?, CancellationToken, Task<object?>> handler = _requestHandler
                ?? throw new InvalidOperationException("No request handler configured.");
            object? result = await handler(method, payload, _lifetime.Token).ConfigureAwait(false);
            response = new() { Type = "res", Id = id, Payload = Serialize(result) };
        }
        catch (Exception ex)
        {
            response = new()
            {
                Type = "res",
                Id = id,
                ErrorCode = RpcErrorCodes.FromException(ex),
                Error = ex.Message
            };
        }
        try
        {
            await WriteAsync(response, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 应答写不出去 = 连接已断,读循环会收尾。
        }
    }

    private void FailPendingAndNotify()
    {
        foreach (long id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out TaskCompletionSource<RpcMessage>? pending))
            {
                pending.TrySetException(new RpcDisconnectedException("RPC connection closed."));
            }
        }
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
        {
            try
            {
                Disconnected?.Invoke();
            }
            catch
            {
                // 断开回调异常不再扩散。
            }
            Disconnected = null;
        }
    }

    /// <summary>关闭连接:停止读循环、断开底层流、失败全部未决请求(幂等)。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 关闭路径尽力而为。
        }
        if (_readLoop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 读循环收不了尾也不阻塞释放。
            }
        }
        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}

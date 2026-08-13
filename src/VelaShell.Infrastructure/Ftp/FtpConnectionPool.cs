using System.Collections.Concurrent;
using FluentFTP;
using VelaShell.Core.Ftp;

namespace VelaShell.Infrastructure.Ftp;

/// <summary>
/// 一条 FTP 会话持有的连接池。
/// <para>
/// **为什么必须有池**:FTP 一条控制连接同一时刻只能跑一条命令(FluentFTP 内部也是这么加锁的),
/// 而 SFTP 是在一条 SSH 连接上多路复用、天然可并发 —— <c>SerializedSftpService</c> 正是基于后者
/// 才敢让传输不占串行闸。若 FTP 也共用一条连接,「传输期间刷新目录」会直接报错,
/// 用户设置的最大并发传输数也会被悄悄压回 1。
/// </para>
/// <para>
/// 池的形状很简单:一个总量信号量 + 一袋空闲连接。租借时优先复用空闲连接,
/// 发现掉线就地重连;归还时放回袋子。上限取 <see cref="Core.Models.FtpSettings.MaxConnections" />。
/// </para>
/// </summary>
internal sealed class FtpConnectionPool(
    FtpConnectionInfo info,
    Func<CancellationToken, Task<AsyncFtpClient>> clientFactory) : IAsyncDisposable
{
    private readonly ConcurrentBag<AsyncFtpClient> _idle = [];
    private readonly List<AsyncFtpClient> _all = [];
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _slots = new(Math.Max(1, info.Settings.MaxConnections), Math.Max(1, info.Settings.MaxConnections));
    private bool _disposed;

    /// <summary>该会话的连接参数。</summary>
    public FtpConnectionInfo Info { get; } = info;

    /// <summary>租借一条可用连接;释放 <see cref="Lease" /> 即归还。</summary>
    public async Task<Lease> RentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        AsyncFtpClient? client = null;
        try
        {
            // 新建连接同样要翻译异常:服务器没了的时候这里抛的是裸 SocketException,
            // 不翻译就会越过 Infrastructure 边界直接甩到界面上。
            client = _idle.TryTake(out AsyncFtpClient? pooled)
                ? pooled
                : await CreateAsync(cancellationToken).ConfigureAwait(false);

            // 空闲期间可能被服务器按 idle timeout 踢掉(FTP 服务器普遍很短),这里就地重连。
            if (!client.IsConnected)
            {
                await client.Connect(cancellationToken).ConfigureAwait(false);
            }
            return new Lease(this, client);
        }
        catch (Exception ex)
        {
            if (client is not null)
            {
                Forget(client);
            }
            _slots.Release();
            throw FluentFtpInterop.Translate(ex, "connect");
        }
    }

    /// <summary>释放全部连接。</summary>
    public async ValueTask DisposeAsync()
    {
        List<AsyncFtpClient> clients;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            clients = [.. _all];
            _all.Clear();
        }
        foreach (AsyncFtpClient client in clients)
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.Disconnect().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 关闭期的失败无人可报,忽略即可。
            }
            finally
            {
                client.Dispose();
            }
        }
        _slots.Dispose();
    }

    private async Task<AsyncFtpClient> CreateAsync(CancellationToken cancellationToken)
    {
        AsyncFtpClient client = await clientFactory(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (_disposed)
            {
                client.Dispose();
                throw new ObjectDisposedException(nameof(FtpConnectionPool));
            }
            _all.Add(client);
        }
        return client;
    }

    /// <summary>
    /// 归还连接。已经掉线的连接**不放回池子** —— 否则它会被反复租出去、每次都要先失败再重连,
    /// 断线后的每一个操作都得多等一个连接超时。
    /// </summary>
    private void Return(AsyncFtpClient client)
    {
        if (_disposed || !client.IsConnected)
        {
            Forget(client);
        }
        else
        {
            _idle.Add(client);
        }
        _slots.Release();
    }

    /// <summary>把一条已经不可用的连接从池中剔除(不再归还给 <see cref="_idle" />)。</summary>
    private void Forget(AsyncFtpClient client)
    {
        lock (_sync)
        {
            _all.Remove(client);
        }
        client.Dispose();
    }

    /// <summary>一次连接租借;释放即归还池中。</summary>
    internal sealed class Lease(FtpConnectionPool pool, AsyncFtpClient client) : IDisposable
    {
        private bool _returned;

        /// <summary>租到的连接。</summary>
        public AsyncFtpClient Client { get; } = client;

        /// <summary>归还连接。</summary>
        public void Dispose()
        {
            if (_returned)
            {
                return;
            }
            _returned = true;
            pool.Return(Client);
        }
    }
}

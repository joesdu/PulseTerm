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
/// 池的形状很简单:一个可收紧的并发闸(<see cref="AdjustableConcurrencyGate" />)+ 一袋空闲连接。
/// 租借时优先复用空闲连接,发现掉线就地重连;归还时放回袋子。
/// 初始上限取 <see cref="Core.Models.FtpSettings.MaxConnections" />。
/// </para>
/// <para>
/// **上限会自己往下调**:不少 FTP 服务器(尤其是设了 <c>MaxClientsPerIP=1</c> 或"一次只允许一个传输"
/// 的那类)只肯给一条连接。这时第一条连接好端端的,第二条却在建立/登录时被拒 ——
/// 用户看到的就是"批量上传,第一个成功,其余全失败"。既然第一条连接已经证明地址、凭据、TLS 都没问题,
/// 第二条建不起来就该退化成**排队复用第一条**,而不是把错误甩给用户。
/// 判据刻意不去猜服务器的措辞(421/530/425 各家都不一样,还分英文中文),
/// 而是看"池里已经有活连接却开不出新连接"这个事实,这比匹配错误码稳得多。
/// </para>
/// </summary>
internal sealed class FtpConnectionPool(
    FtpConnectionInfo info,
    Func<CancellationToken, Task<AsyncFtpClient>> clientFactory) : IAsyncDisposable
{
    private readonly ConcurrentBag<AsyncFtpClient> _idle = [];
    private readonly List<AsyncFtpClient> _all = [];
    private readonly Lock _sync = new();
    private readonly AdjustableConcurrencyGate _gate = new(Math.Max(1, info.Settings.MaxConnections));
    private bool _disposed;

    /// <summary>一次租借最多退化重排几次(见 <see cref="RentAsync" />)。</summary>
    private const int MaxFallbackAttempts = 3;

    /// <summary>该会话的连接参数。</summary>
    public FtpConnectionInfo Info { get; } = info;

    /// <summary>当前生效的并发连接上限(可能已被自适应下调)。</summary>
    public int ConnectionLimit => _gate.Limit;

    /// <summary>
    /// 把并发上限收到 1 并关掉多余的空闲连接:服务器表示"一次只能跑一个传输"时由上层调用
    /// (数据通道被拒的场景,控制连接本身开得出来,自适应租借那条路看不到)。
    /// </summary>
    /// <returns>此调用是否真的改变了上限(已经是 1 则为 false)。</returns>
    public bool LimitToSingleConnection()
    {
        if (_gate.Limit <= 1)
        {
            return false;
        }
        _gate.LimitTo(1);
        DropIdleConnections();
        System.Diagnostics.Trace.WriteLine(
            $"[VelaShell] FTP {Info.Host}: server rejected a concurrent transfer; falling back to a single connection.");
        return true;
    }

    /// <summary>
    /// 租借一条可用连接;释放 <see cref="Lease" /> 即归还。
    /// <para>
    /// 名额的账在本方法里收口:循环顶上取一个,失败路径上无条件交回 ——
    /// 退化重排也走这条路(交回后重新排队),因此不会出现"多还一个名额"把上限撑破的情况。
    /// </para>
    /// </summary>
    public async Task<Lease> RentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 退化重排的次数上限。正常只会用掉一次(收紧后就排队等已有连接);留几次余量是防着
        // "刚被叫醒、那条连接又被别人抢先拿走"这类罕见交错,免得一次抖动就把传输判死。
        int fallbacksLeft = MaxFallbackAttempts;
        while (true)
        {
            await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await OpenLeaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReleaseAfterFailure();
                if (fallbacksLeft > 0 && ShouldQueueOnExistingConnection(ex, cancellationToken))
                {
                    fallbacksLeft--;
                    continue;
                }
                throw FluentFtpInterop.Translate(ex, "connect");
            }
        }
    }

    /// <summary>
    /// 失败路径上交回名额。**不叫醒**排队的人 —— 这次失败并没有让任何一条连接空出来,
    /// 叫醒下一个只会让他重复同一次失败(再去开一条注定被顶回来的连接)。
    /// 叫醒交给真正归还连接的 <see cref="Return" />。
    /// <para>
    /// 例外:池里已经一条连接都不剩时照常移交 —— 此时没有"归还"这回事了,
    /// 排队的人必须被叫醒去自己重连(失败了也该由他把错误报出来),否则就是干等。
    /// </para>
    /// </summary>
    private void ReleaseAfterFailure()
    {
        if (_idle.IsEmpty && LiveConnectionCount() > 0)
        {
            _gate.ReleaseWithoutHandoff();
        }
        else
        {
            _gate.Release();
        }
    }

    /// <summary>持名额取一条连接:优先复用空闲的,没有就新建;掉线的就地重连。</summary>
    private async Task<Lease> OpenLeaseAsync(CancellationToken cancellationToken)
    {
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
        catch
        {
            if (client is not null)
            {
                Forget(client);
            }
            throw;
        }
    }

    /// <summary>
    /// 这次失败该不该退化成"排队复用已有连接"。
    /// 判据是事实而非措辞:池里**还有活着的连接**,却开不出这一条 —— 第一条能连上就说明
    /// 地址、凭据、TLS 都没问题,那么开不出第二条基本只有一个解释:服务器的连接数限制。
    /// 真是网络抖动被误判也不亏:代价只是这一次操作排队等已有连接,而不是直接失败。
    /// </summary>
    private bool ShouldQueueOnExistingConnection(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _disposed
            || ex is ObjectDisposedException or OperationCanceledException
            || LiveConnectionCount() <= 0)
        {
            return false;
        }
        int limit = _gate.LimitTo(LiveConnectionCount());
        DropIdleConnections();
        System.Diagnostics.Trace.WriteLine(
            $"[VelaShell] FTP {Info.Host}: could not open an extra connection ({ex.GetType().Name}); "
            + $"queueing on the existing one (limit={limit}).");
        return true;
    }

    /// <summary>池中还活着的连接数(含租出去的与空闲的)。</summary>
    private int LiveConnectionCount()
    {
        lock (_sync)
        {
            return _all.Count;
        }
    }

    /// <summary>关掉暂时用不到的空闲连接:上限收紧后它们只是白占服务器的名额。</summary>
    private void DropIdleConnections()
    {
        while (_idle.TryTake(out AsyncFtpClient? spare))
        {
            if (LiveConnectionCount() <= 1)
            {
                // 最后一条活连接留着,否则下一次租借还要从头连一遍。
                _idle.Add(spare);
                return;
            }
            Forget(spare);
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
        _gate.Release();
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

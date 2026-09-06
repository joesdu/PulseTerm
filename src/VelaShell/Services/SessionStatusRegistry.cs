using System.Collections.Concurrent;
using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>
/// 记着每条连接配置名下都有谁活着,并把它们合并成树上那一个圆点该显示的状态。
/// </summary>
/// <remarks>
/// <para>
/// 从 <c>MainWindowViewModel</c> 拆出来的一簇(Q-01)。这里是**两个已修过的同形 bug**
/// 的原产地(§24 / §39 / #321),值得单独立起来并钉住:
/// </para>
/// <list type="bullet">
/// <item>一条配置可以同时开着多个终端标签(复制会话、对同一台机器再开一个),
/// 而树上只有一个节点。按"最后一次变更"更新会留下走不出去的状态 —— 在已连上的会话上
/// 再开一个标签、趁它还在握手时立刻关掉,节点会永远停在「连接中」(#321)。</item>
/// <item>参与合并的不只有终端标签,还有该配置名下**活着的文档型会话**
/// (独立 SFTP / FTP / S3 等插件文件系统 / Redis 等工作台)。它们原先各自直接往树上写、
/// 最后一次说了算 —— 于是"点快了开出两个 FTP 标签,关掉一个,圆点就灭了"。</item>
/// </list>
/// <para>
/// 本类型只管账与合并,**不碰界面**:算出来的状态交给调用方去写树。
/// </para>
/// </remarks>
public sealed class SessionStatusRegistry
{
    private readonly ConcurrentDictionary<Guid, (Guid ProfileId, SessionStatus Status)> _documents = new();

    /// <summary>
    /// 登记一条刚建好的文档型会话。
    /// </summary>
    /// <param name="sessionId">会话标识(摘除与状态更新都按它定位)。</param>
    /// <param name="profileId">
    /// 树上节点对应的配置标识。刻意由调用方给出**原始** profile 的 Id:登录弹窗可能换过
    /// 文档里那份配置的字段,而树上的节点始终是按最初那条配置的 Id 建的。
    /// </param>
    /// <param name="status">初始状态(建出文档即已连上)。</param>
    /// <returns>需要刷新的配置 id;参数不合法时为 null。</returns>
    public Guid? Track(Guid sessionId, Guid profileId, SessionStatus status)
    {
        if (sessionId == Guid.Empty || profileId == Guid.Empty)
        {
            return null;
        }
        _documents[sessionId] = (profileId, status);
        return profileId;
    }

    /// <summary>
    /// 更新一条在册文档型会话的状态(掉线 / 重新连上)。
    /// </summary>
    /// <remarks>
    /// <b>不在册的会话不复活</b>:文档已经关掉之后仍可能收到一条迟到的状态事件
    /// (FTP 的失效是在下一次操作时才暴露的),照单全收会把刚灭掉的圆点重新点亮。
    /// </remarks>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="status">新状态。</param>
    /// <returns>需要刷新的配置 id;不在册时为 null。</returns>
    public Guid? Update(Guid sessionId, SessionStatus status)
    {
        if (!_documents.TryGetValue(sessionId, out (Guid ProfileId, SessionStatus Status) tracked))
        {
            return null;
        }
        _documents[sessionId] = (tracked.ProfileId, status);
        return tracked.ProfileId;
    }

    /// <summary>
    /// 摘掉一条已关闭的文档型会话。
    /// </summary>
    /// <remarks>
    /// 幂等 —— 关闭路径与协议自己的 <c>Closed</c> 事件都会走到这里,谁先到都行。
    /// </remarks>
    /// <param name="sessionId">会话标识。</param>
    /// <returns>需要刷新的配置 id;不在册时为 null。</returns>
    public Guid? Forget(Guid sessionId) =>
        _documents.TryRemove(sessionId, out (Guid ProfileId, SessionStatus Status) tracked)
            ? tracked.ProfileId
            : null;

    /// <summary>
    /// 把某配置名下全部终端标签与文档型会话的状态合并成一个。
    /// </summary>
    /// <remarks>
    /// 合并优先级 <c>Connected &gt; Connecting &gt; Error &gt; Disconnected</c>:一条已经连上的
    /// 会话不该因为旁边多了个正在握手或握手失败的标签而被写成「连接中」/「离线」。
    /// 没有任何标签或文档属于该配置时归零为 <c>Disconnected</c> —— 最后一个关掉即回到未连接。
    /// </remarks>
    /// <param name="profileId">配置标识。</param>
    /// <param name="terminalStatuses">该配置名下全部终端标签的状态。</param>
    /// <returns>合并后的状态。</returns>
    public SessionStatus Merge(Guid profileId, IEnumerable<SessionStatus> terminalStatuses)
    {
        ArgumentNullException.ThrowIfNull(terminalStatuses);
        return terminalStatuses
               .Concat(_documents.Values
                                 .Where(session => session.ProfileId == profileId)
                                 .Select(session => session.Status))
               .Aggregate(
                   SessionStatus.Disconnected,
                   (best, candidate) => Rank(candidate) > Rank(best) ? candidate : best);
    }

    /// <summary>多标签合并时的状态优先级,数值越大越"活跃"。</summary>
    private static int Rank(SessionStatus status) =>
        status switch
        {
            SessionStatus.Connected => 3,
            SessionStatus.Connecting => 2,
            SessionStatus.Error => 1,
            _ => 0,
        };
}

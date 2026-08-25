using Tmds.Ssh;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 把 <c>KeyExchangeFailed</c> 翻译成用户能照着办的一段话:哪一类算法没有交集、对端提供了什么、
/// 本客户端支持什么。
/// <para>
/// 底层库给的是一句 "No common encryption algorithm." —— 它没说对端提供的是什么,也没说我们
/// 支持什么,用户拿到手里做不了任何事。补上这两个名单,才谈得上"去把服务端配置改成 X"或者
/// "这台设备太老,本客户端连不了"。
/// </para>
/// <para>
/// "本客户端支持什么"刻意从 <see cref="SshClientSettings" /> 现读而不是写死:底层库升级增删算法时,
/// 写死的那份会悄悄变成假话 —— 而这段话恰恰是用户唯一的判断依据。
/// </para>
/// </summary>
internal static class SshAlgorithmDiagnostics
{
    /// <summary>
    /// 5 秒:用户已经在等一个失败的结果了,诊断不能再让他多等太久;
    /// 而能走到协商这一步说明 TCP 是通的,正常对端一个来回远用不了这么久。
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 探一次对端并给出差异说明;探不到、或每一类算法其实都有交集(失败另有原因)时返回
    /// <see langword="null" />,由调用方保持原样的错误消息。
    /// </summary>
    public static async Task<string?> TryDescribeAsync(SshClientSettings settings, CancellationToken cancellationToken)
    {
        SshPeerAlgorithms? peer = await SshAlgorithmProbe
                                        .TryProbeAsync(settings.HostName, settings.Port, ProbeTimeout, cancellationToken)
                                        .ConfigureAwait(false);
        if (peer is null)
        {
            return null;
        }

        List<string> lines = [];
        AddIfDisjoint(lines, Strings.Get("Ssh_AlgoKindKex"), peer.KeyExchange, settings.KeyExchangeAlgorithms);
        AddIfDisjoint(lines, Strings.Get("Ssh_AlgoKindHostKey"), peer.HostKey, settings.ServerHostKeyAlgorithms);
        AddDirectional(lines, Strings.Get("Ssh_AlgoKindEncryption"),
                       peer.EncryptionServerToClient, settings.EncryptionAlgorithmsServerToClient,
                       peer.EncryptionClientToServer, settings.EncryptionAlgorithmsClientToServer);
        AddDirectional(lines, Strings.Get("Ssh_AlgoKindMac"),
                       peer.MacServerToClient, settings.MacAlgorithmsServerToClient,
                       peer.MacClientToServer, settings.MacAlgorithmsClientToServer);
        if (lines.Count == 0)
        {
            return null;
        }
        lines.Insert(0, Strings.Format("Ssh_AlgoMismatchTitle", peer.ServerVersion));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// 收发两个方向各有一份名单,但现实里两份几乎总是一样。一样时只报一行,免得同一件事说两遍;
    /// 真不一样时两行都报 —— 那种服务器已经够反常,不该再被我们合并掉细节。
    /// </summary>
    private static void AddDirectional(
        List<string> lines, string kind,
        IReadOnlyList<string> peerServerToClient, IEnumerable<string> oursServerToClient,
        IReadOnlyList<string> peerClientToServer, IEnumerable<string> oursClientToServer)
    {
        AddIfDisjoint(lines, kind, peerServerToClient, oursServerToClient);
        if (!peerClientToServer.SequenceEqual(peerServerToClient, StringComparer.Ordinal))
        {
            AddIfDisjoint(lines, kind, peerClientToServer, oursClientToServer);
        }
    }

    /// <summary>
    /// 两份名单没有任何交集时记三行(类别 / 对端提供 / 本端支持);有交集(或任一份为空)
    /// 说明这一类不是失败原因。
    /// </summary>
    /// <remarks>
    /// 拆成三行而不是挤成一行:名单动辄六七个算法名,挤在一行里换行之后就成了一团,
    /// 用户要对着找"我这边有没有对端要的那个"根本看不下去。
    /// </remarks>
    private static void AddIfDisjoint(List<string> lines, string kind, IReadOnlyList<string> peer, IEnumerable<string> ours)
    {
        string[] mine = [.. ours];
        if (peer.Count == 0 || mine.Length == 0 || peer.Intersect(mine, StringComparer.Ordinal).Any())
        {
            return;
        }
        lines.Add(kind);
        lines.Add(Strings.Format("Ssh_AlgoMismatchPeer", string.Join(", ", peer)));
        lines.Add(Strings.Format("Ssh_AlgoMismatchOurs", string.Join(", ", mine.Where(IsWorthShowing))));
    }

    /// <summary>
    /// 证书变体(<c>*-cert-v01@openssh.com</c>)不列进"本客户端支持"。
    /// </summary>
    /// <remarks>
    /// 它们只在对端出示 OpenSSH **证书**时才用得上,而会走到这条诊断的对端出示的是普通主机密钥 ——
    /// 列出来既不能指导用户改配置,还把六个可用算法淹没在十二行里(实测那台堡垒机就是这样)。
    /// 交集判断仍用完整名单,只是显示时不摆出来:少显示不会改变判断结果,证书算法真能匹配上时
    /// 压根不会走到这里。
    /// </remarks>
    private static bool IsWorthShowing(string algorithm) =>
        !algorithm.Contains("-cert-", StringComparison.Ordinal);
}

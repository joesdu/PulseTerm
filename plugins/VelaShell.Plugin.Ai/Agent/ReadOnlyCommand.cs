namespace VelaShell.Plugin.Ai.Agent;

/// <summary>
/// 判断一条 shell 命令是否"一望即知没有副作用",供
/// <see cref="Configuration.ApprovalMode.ReadOnlyAuto" /> 决定要不要免审批。
/// </summary>
/// <remarks>
/// <b>这是个安全判断,所以刻意写得胆小</b>:只有<i>确定</i>无害才返回 true,
/// 但凡有一点看不准就返回 false 去问用户。放过一条该问的命令,代价可能是删掉生产数据;
/// 多问一次的代价只是多点一下鼠标。两者不对称,所以规则宁可过严。
///
/// 具体地:命令名必须在白名单里,而且整条命令不许出现任何能改变状态的构造 ——
/// 重定向、管道、命令分隔符、命令替换,以及那些能让只读命令变成写命令的参数
/// (<c>find -delete</c>、<c>sed -i</c>、<c>xargs</c> 之类)。
/// </remarks>
public static class ReadOnlyCommand
{
    /// <summary>公认没有副作用的命令名。没列进来的一律当作可能有副作用。</summary>
    private static readonly HashSet<string> Safe = new(StringComparer.Ordinal)
    {
        "ls", "ll", "dir", "cat", "bat", "head", "tail", "less", "more", "wc", "nl",
        "stat", "file", "readlink", "realpath", "basename", "dirname", "pwd",
        "df", "du", "free", "uptime", "uname", "hostname", "whoami", "id", "groups", "date",
        "ps", "top", "htop", "pgrep", "lsof", "netstat", "ss", "ip", "ifconfig", "route",
        "dmesg", "journalctl", "who", "w", "last", "lastlog",
        "grep", "egrep", "fgrep", "rg", "diff", "cmp", "md5sum", "sha256sum",
        "env", "printenv", "echo", "which", "whereis", "type", "locale",
        "lscpu", "lsblk", "lsusb", "lspci", "mount", "vmstat", "iostat", "sar",
        "getent", "dig", "nslookup", "host", "ping", "traceroute", "curl", "wget"
    };

    /// <summary>
    /// 出现其中任何一个片段就直接判定"看不准"。
    /// 前面几个是 shell 构造(能把只读命令接成写操作),后面几个是特定命令的危险参数。
    /// </summary>
    private static readonly string[] Suspicious =
    [
        ">", "<", "|", ";", "&", "$(", "`", "\n",
        " -delete", " -exec", " -execdir", " -ok",
        " -i ", " -i\t", " --in-place", " -o ", " --output"
    ];

    /// <summary>某些命令名虽然常见,但带上特定子命令就会改状态,单独挡掉。</summary>
    private static readonly string[] SuspiciousPrefixes =
    [
        "systemctl start", "systemctl stop", "systemctl restart", "systemctl enable",
        "systemctl disable", "systemctl mask", "systemctl daemon-reload",
        "curl -o", "curl --output", "curl -O", "wget -O", "wget --output-document"
    ];

    /// <summary>这条命令能不能免审批直接跑。</summary>
    public static bool IsSafe(string? command)
    {
        string text = command?.Trim() ?? "";
        if (text.Length == 0)
        {
            return false;
        }
        foreach (string marker in Suspicious)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return false;
            }
        }
        foreach (string prefix in SuspiciousPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("sudo " + prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int index = 0;
        // sudo 只是前缀,真正要看的是它后面那个命令 —— 但 sudo 本身抬高了风险,
        // 所以只在后面确实是白名单命令时才放行。
        if (words[index] == "sudo")
        {
            index++;
            if (index >= words.Length)
            {
                return false;
            }
        }
        // 带路径的调用(./script、/usr/local/bin/x)一律不认:名字看不出它干什么
        string name = words[index];
        return !name.Contains('/') && Safe.Contains(name);
    }
}

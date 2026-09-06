using System.Collections;
using System.Reflection;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// <see cref="SessionProfile.Clone" /> 必须拷到每一个属性,而且不与源共享可变对象。
/// </summary>
/// <remarks>
/// <para>
/// 这条用例的存在理由就是「加字段只改一处」这句话得有人守。此前全仓有三处逐字段手写拷贝
/// (仓储加密、会话树复制配置、连接工作流剥密码),加一个字段要三处同步补;
/// 漏一处的表现是"某个设置在复制配置之后莫名丢了",三处的症状还各不相同。
/// </para>
/// <para>
/// 手写一份"期望字段清单"没有意义 —— 那份清单本身也会忘记更新。这里改用<b>反射逐属性比对</b>:
/// 新加的属性自动进入检查范围,忘了在 <c>Clone</c> 里带上它就直接红。
/// </para>
/// </remarks>
[TestClass]
public sealed class SessionProfileCloneTests
{
    /// <summary>造一条每个字段都不是默认值的配置 —— 默认值相等分不出"拷了"和"没拷"。</summary>
    private static SessionProfile FullyPopulated() =>
        new()
        {
            ConnectionType = ConnectionType.FTP,
            Id = Guid.NewGuid(),
            Name = "prod-bastion",
            Host = "10.0.0.9",
            Port = 2222,
            Username = "ops",
            AuthMethod = AuthMethod.PrivateKey,
            Password = "pw",
            RememberPassword = false,
            PrivateKeyPath = @"C:\keys\id_ed25519",
            PrivateKeyPassphrase = "phrase",
            GroupId = Guid.NewGuid(),
            LastConnectedAt = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc),
            Tags = ["prod", "bastion"],
            JumpHostProfileId = Guid.NewGuid(),
            PostAuthCommand = "sudo su -",
            PostAuthCommandDelaySeconds = 3,
            Ftp = new() { Anonymous = true, MaxConnections = 4, InitialRemotePath = "/srv" },
            PluginProtocolId = "velashell.s3",
            PluginSettings = new() { ["region"] = "cn-north-1" },
            PluginSecrets = new() { ["secretKey"] = "s3cr3t" },
            Terminal = new()
            {
                Encoding = "GBK",
                TerminalType = "vt220",
                ColorScheme = "Nord",
                TabColor = "#E05252",
                StartupDirectory = "/var/log",
                KeepAliveSeconds = 15
            },
            AutoStartTunnelIds = [Guid.NewGuid(), Guid.NewGuid()]
        };

    [TestMethod]
    public void CloneCopiesEveryProperty()
    {
        SessionProfile source = FullyPopulated();
        SessionProfile copy = source.Clone();

        List<string> missed = [];
        foreach (PropertyInfo property in typeof(SessionProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead)
            {
                continue;
            }
            object? a = property.GetValue(source);
            object? b = property.GetValue(copy);
            if (!ValueEquals(a, b))
            {
                missed.Add($"  {property.Name}:源 = {Describe(a)},副本 = {Describe(b)}");
            }
        }

        Assert.IsEmpty(
            missed,
            "以下属性没有被 SessionProfile.Clone 拷过去 —— 复制配置、保存到数据库、"
            + "剥密码这几条路径都会悄悄丢掉它:" + Environment.NewLine + string.Join(Environment.NewLine, missed));
    }

    /// <summary>
    /// 每一个可变对象都必须是独立的一份。
    /// </summary>
    /// <remarks>
    /// 浅拷贝的表现比漏拷更隐蔽:副本改一下,源跟着变 —— 而"源"往往是仓储缓存里那一条,
    /// 于是界面上没保存的编辑凭空生效了。
    /// </remarks>
    [TestMethod]
    public void CloneSharesNoMutableObjectWithTheSource()
    {
        SessionProfile source = FullyPopulated();
        SessionProfile copy = source.Clone();

        Assert.AreNotSame(source.Tags, copy.Tags);
        Assert.AreNotSame(source.Ftp, copy.Ftp);
        Assert.AreNotSame(source.PluginSettings, copy.PluginSettings);
        Assert.AreNotSame(source.PluginSecrets, copy.PluginSecrets);
        Assert.AreNotSame(source.Terminal, copy.Terminal);
        Assert.AreNotSame(source.AutoStartTunnelIds, copy.AutoStartTunnelIds);

        copy.Tags.Add("touched");
        copy.PluginSettings!["region"] = "changed";
        copy.Terminal!.Encoding = "Big5";
        copy.AutoStartTunnelIds!.Clear();

        Assert.HasCount(2, source.Tags);
        Assert.AreEqual("cn-north-1", source.PluginSettings!["region"]);
        Assert.AreEqual("GBK", source.Terminal!.Encoding);
        Assert.HasCount(2, source.AutoStartTunnelIds!);
    }

    [TestMethod]
    public void CloningAProfileWithoutOverridesKeepsThemNull()
    {
        // 没有覆盖项时落盘的就该是一个 null,而不是一个全空对象 ——
        // 后者会让"有没有覆盖"这件事有了两种表示,也给每条老配置的 JSON 平白多一段。
        SessionProfile bare = new() { Name = "plain" };

        SessionProfile copy = bare.Clone();

        Assert.IsNull(copy.Terminal);
        Assert.IsNull(copy.AutoStartTunnelIds);
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        // 集合按元素比:List/Dictionary 的 Equals 是引用相等,直接比会把每个集合属性都报成"没拷"。
        if (a is IEnumerable left and not string && b is IEnumerable right)
        {
            return left.Cast<object>().SequenceEqual(right.Cast<object>());
        }
        // FtpSettings / TerminalOverrides 是普通类(引用相等),逐属性下探一层。
        if (a.GetType() is { IsClass: true } type && type != typeof(string) && !type.IsValueType)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                       .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                       .All(p => Equals(p.GetValue(a), p.GetValue(b)));
        }
        return a.Equals(b);
    }

    private static string Describe(object? value) =>
        value switch
        {
            null => "<null>",
            string s => $"\"{s}\"",
            IEnumerable items => $"[{string.Join(", ", items.Cast<object>())}]",
            _ => value.ToString() ?? "<?>"
        };
}

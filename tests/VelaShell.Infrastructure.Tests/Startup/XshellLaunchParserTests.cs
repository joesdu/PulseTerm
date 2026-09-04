using System.Text;
using System.Text.Json;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Startup;

namespace VelaShell.Infrastructure.Tests.Startup;

/// <summary>
/// Xshell 兼容登录的入口解析。这些写法不是我们定的 —— 是堡垒机/SSO 客户端**已经在发**的,
/// 所以每一条都对应一种真实调用形态,尤其是一次性密码里带 <c>@ : /</c> 这类未转义字符的情况:
/// 那正是把 URL 丢给 <see cref="Uri" /> 会解错主机、而用户只会看到"连不上"的地方。
/// </summary>
[TestClass]
[TestCategory("Startup")]
public class XshellLaunchParserTests
{
    [TestMethod]
    public void Parse_NoLaunchArguments_ReturnsNull()
    {
        Assert.IsNull(XshellLaunchParser.TryParse([]));
        // 插件开发参数与 Avalonia 自己的参数都不该被误读成一次登录请求。
        Assert.IsNull(XshellLaunchParser.TryParse(["--dev-root", @"C:\work\plugin", "--dev-watch"]));
    }

    [TestMethod]
    public void Parse_UrlOption_ReadsEveryField()
    {
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "ssh://root:s3cret@10.0.3.21:2222"])!;

        Assert.AreEqual(ConnectionType.SSH, request.ConnectionType);
        Assert.AreEqual("10.0.3.21", request.Host);
        Assert.AreEqual(2222, request.Port);
        Assert.AreEqual("root", request.Username);
        Assert.AreEqual("s3cret", request.Password);
        Assert.IsTrue(request.IsSupported);
    }

    [TestMethod]
    public void Parse_BareUrlArgument_IsTreatedAsProtocolInvocation()
    {
        // 协议关联注册的是 `exe -url "%1"`,但有的调用方直接把 URL 甩在第一个参数上。
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["sftp://ops@files.example.com"])!;

        Assert.AreEqual(ConnectionType.SFTP, request.ConnectionType);
        Assert.AreEqual("files.example.com", request.Host);
        Assert.AreEqual(22, request.Port, "SFTP 未给端口时按 SSH 的 22,而不是 FTP 的 21。");
        Assert.AreEqual("ops", request.Username);
        Assert.IsNull(request.Password);
        Assert.AreEqual(ExternalLaunchOrigin.UrlProtocol, request.Origin);
    }

    [TestMethod]
    public void Parse_PasswordWithAtSign_SplitsOnLastAt()
    {
        // 堡垒机现发的一次性口令里出现未转义的 @ 是常态;按最后一个 @ 切才不会把主机截错。
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "ssh://root:p@ss:w0rd@10.0.3.21:22"])!;

        Assert.AreEqual("10.0.3.21", request.Host);
        Assert.AreEqual("root", request.Username);
        Assert.AreEqual("p@ss:w0rd", request.Password, "只在第一个冒号处切用户名与密码,其余原样保留。");
    }

    [TestMethod]
    public void Parse_PercentEncodedCredentials_AreDecoded()
    {
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "ssh://ops%40corp:a%2Fb%23c@host:22"])!;

        Assert.AreEqual("ops@corp", request.Username);
        Assert.AreEqual("a/b#c", request.Password);
    }

    [TestMethod]
    public void Parse_UrlWithPathOrQuery_IgnoresEverythingAfterAuthority()
    {
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "ssh://root@10.0.3.21:22/?folder=prod"])!;

        Assert.AreEqual("10.0.3.21", request.Host);
        Assert.AreEqual(22, request.Port);
    }

    [TestMethod]
    public void Parse_SsoClientCommandLine_SurvivesHashOnlyUserName()
    {
        // 地面真值:某 SSO 客户端(D:\SsoTool)对 SSH/SFTP 资源固定把用户名换成字面量 "#sso"、
        // 口令换成 sessionId,再按 Xshell 的约定拉起:
        //     VelaShell.exe ssh://#sso:<sessionId>@<堡垒机IP>:<代理端口> -newtab CBH_<资源>
        // 那个开头的 # 曾把整个 authority 截没,解析返回 null —— 网页点了登录,终端开了却没连。
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(
            ["ssh://#sso:1f4b9c2e7a@10.20.30.40:9527", "-newtab", "CBH_10.1.2.3_22_root"])!;

        Assert.AreEqual("10.20.30.40", request.Host);
        Assert.AreEqual(9527, request.Port);
        Assert.AreEqual("#sso", request.Username);
        Assert.AreEqual("1f4b9c2e7a", request.Password);
        Assert.IsTrue(request.IsSupported);
    }

    [TestMethod]
    public void Parse_CredentialsWithHashSlashOrQuestionMark_DoNotTruncateTheHost()
    {
        // 一次性口令是现发的随机串,# / ? 都可能原样出现在里面(调用方不会替我们转义)。
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "sftp://ops#dev:a/b?c#d@10.0.3.21:2222"])!;

        Assert.AreEqual("10.0.3.21", request.Host);
        Assert.AreEqual(2222, request.Port);
        Assert.AreEqual("ops#dev", request.Username);
        Assert.AreEqual("a/b?c#d", request.Password);
    }

    [TestMethod]
    public void Parse_AtSignInsidePath_IsNotMistakenForCredentials()
    {
        // 反过来也得站得住:没带凭据时,路径里的 @ 不能被当成主机分界。
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "sftp://files.example.com:2222/home/ops@corp"])!;

        Assert.AreEqual("files.example.com", request.Host);
        Assert.AreEqual(2222, request.Port);
        Assert.AreEqual(string.Empty, request.Username);
    }

    [TestMethod]
    public void Parse_IPv6Host_KeepsAddressWithoutBrackets()
    {
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "ssh://root@[fe80::1]:2222"])!;

        Assert.AreEqual("fe80::1", request.Host);
        Assert.AreEqual(2222, request.Port);
    }

    [TestMethod]
    public void Parse_ExplicitOptions_OverrideUrlFields()
    {
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(
            ["-url", "ssh://guest@10.0.3.21:22", "-l", "root", "-p", "2222", "-pw", "one-time"])!;

        Assert.AreEqual("root", request.Username);
        Assert.AreEqual(2222, request.Port);
        Assert.AreEqual("one-time", request.Password);
    }

    [TestMethod]
    public void Parse_EqualsForm_IsAcceptedToo()
    {
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url=ssh://root@10.0.3.21", "-p=2200"])!;

        Assert.AreEqual("10.0.3.21", request.Host);
        Assert.AreEqual(2200, request.Port);
    }

    [TestMethod]
    public void Parse_FtpsScheme_MapsToFtpOnPort21()
    {
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "ftps://ops@files.example.com"])!;

        Assert.AreEqual(ConnectionType.FTP, request.ConnectionType);
        Assert.AreEqual(FtpSettings.DefaultPort, request.Port);
        Assert.AreEqual("ftps", request.Scheme);
    }

    [TestMethod]
    public void Parse_UnsupportedScheme_IsReportedNotDropped()
    {
        // 丢掉的话用户在网页上点了半天没反应;标成不支持才能给出一句可读的提示。
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "telnet://ops@10.0.3.21"])!;

        Assert.IsFalse(request.IsSupported);
        Assert.AreEqual("telnet", request.Scheme);
    }

    [TestMethod]
    public void Parse_SessionFile_ReadsHostAndUserFromUtf16Ini()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vela-{Guid.NewGuid():N}.xsh");
        File.WriteAllText(path, """
            [CONNECTION]
            Protocol=SSH
            Host=10.0.3.21
            Port=2222

            [CONNECTION:AUTHENTICATION]
            UserName=deploy

            """, Encoding.Unicode);
        try
        {
            ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-f", path])!;

            Assert.AreEqual("10.0.3.21", request.Host);
            Assert.AreEqual(2222, request.Port);
            Assert.AreEqual("deploy", request.Username);
            Assert.AreEqual(ExternalLaunchOrigin.SessionFile, request.Origin);
            Assert.IsNull(request.Password, "会话文件里的密码是 Xshell 按它自己的身份加密的,不该被当作凭据带出来。");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Parse_MissingSessionFile_ReturnsNullInsteadOfThrowing() => Assert.IsNull(XshellLaunchParser.TryParse(["-f", Path.Combine(Path.GetTempPath(), "no-such-file.xsh")]));

    [TestMethod]
    public void TrustKey_IncludesUserAndPort_AndIgnoresHostCase()
    {
        ExternalLaunchRequest root = XshellLaunchParser.TryParse(["-url", "ssh://root@Host.Example.com:22"])!;
        ExternalLaunchRequest deploy = XshellLaunchParser.TryParse(["-url", "ssh://deploy@host.example.com:22"])!;
        ExternalLaunchRequest otherPort = XshellLaunchParser.TryParse(["-url", "ssh://root@host.example.com:2222"])!;

        // 信任 deploy@host 绝不能顺带放行 root@host —— 那是两个完全不同的权限。
        Assert.AreNotEqual(root.TrustKey, deploy.TrustKey);
        Assert.AreNotEqual(root.TrustKey, otherPort.TrustKey);
        Assert.AreEqual("ssh://root@host.example.com:22", root.TrustKey, "主机名大小写不该分裂出第二条信任记录。");
    }

    [TestMethod]
    public void ToString_NeverLeaksThePassword()
    {
        // 这条串随时可能被 Trace 抄进日志文件,一次性口令进去就等于落盘。
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "ssh://root:s3cret@10.0.3.21:22"])!;

        Assert.DoesNotContain("s3cret", request.ToString());
    }

    [TestMethod]
    public void JsonRoundTrip_KeepsEveryFieldTheRunningInstanceNeeds()
    {
        // 单实例转发就是靠这份 JSON 过管道的:少一个字段,外部登录到了对面就变成"缺凭据"。
        ExternalLaunchRequest request = XshellLaunchParser.TryParse(["-url", "ssh://root:s3cret@10.0.3.21:2222"])!;

        string json = JsonSerializer.Serialize(request, LaunchJsonContext.Default.ExternalLaunchRequest);
        ExternalLaunchRequest restored = JsonSerializer.Deserialize(json, LaunchJsonContext.Default.ExternalLaunchRequest)!;

        Assert.AreEqual(request.Host, restored.Host);
        Assert.AreEqual(request.Port, restored.Port);
        Assert.AreEqual(request.Username, restored.Username);
        Assert.AreEqual(request.Password, restored.Password);
        Assert.AreEqual(request.ConnectionType, restored.ConnectionType);
        Assert.AreEqual(request.Scheme, restored.Scheme);
        Assert.AreEqual(request.Kind, restored.Kind);
    }
}

using System.Reflection;
using Tmds.Ssh;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 底层 SSH 库是否支持键盘交互式认证(2FA / OTP)。
/// </summary>
/// <remarks>
/// <para>
/// F-11 的结论是「做不了,只能把话说清楚」:堡垒机上的 Google Authenticator、Duo 一类走的是
/// SSH 的 <c>keyboard-interactive</c> 方法,而 Tmds.Ssh 0.24 压根没实现它 —— 凭据类型只有
/// 密码 / 私钥 / 证书 / Kerberos / ssh-agent / 无。于是那种服务器上认证必然失败,
/// 而失败文案曾经直接断言「用户名、密码或密钥不正确」,把用户引向一条永远改不对的路。
/// </para>
/// <para>
/// <b>这条用例是给未来的引信。</b>它断言的是「现在还不支持」——
/// Tmds.Ssh 哪天加上了,这里会红,那时就该去实现真正的两步验证流程,并把
/// <c>Msg_AuthFailedTwoFactorHint</c> 那句说明撤掉。没有这条引信,那句说明会一直留着,
/// 在早已支持之后继续误导人。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public sealed class KeyboardInteractiveSupportTests
{
    [TestMethod]
    public void TheSshLibraryStillOffersNoKeyboardInteractiveCredential()
    {
        Type[] credentials =
        [
            .. typeof(Credential).Assembly
                                 .GetExportedTypes()
                                 .Where(t => typeof(Credential).IsAssignableFrom(t) && t != typeof(Credential))
        ];

        Assert.IsNotEmpty(credentials, "一个凭据类型都没找到 —— 反射扫描失效了,这条用例等于没测。");
        Assert.IsFalse(
            credentials.Any(t => t.Name.Contains("KeyboardInteractive", StringComparison.OrdinalIgnoreCase)),
            "Tmds.Ssh 现在提供了键盘交互式凭据:请实现真正的 2FA / OTP 流程,"
            + "并撤掉 Msg_AuthFailedTwoFactorHint 那句「本版无法连接」的说明。"
            + $"当前凭据类型:{string.Join("、", credentials.Select(t => t.Name))}");
    }

    /// <summary>
    /// 公开凭据类型之外再验一道:库内部实现了哪几种认证方式。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tmds.Ssh 把每种认证方式实现成 <c>UserAuthentication</c> 下的一个嵌套类型
    /// (<c>NoneAuth</c> / <c>PasswordAuth</c> / <c>PublicKeyAuth</c> / <c>CertificateAuth</c>
    /// / <c>GssApiAuth</c> / <c>SshAgentAuth</c>)。这一层比公开凭据类更贴近事实 ——
    /// 万一哪天它加了键盘交互式支持却没配套加公开凭据类,上面那条会漏,这条不会。
    /// </para>
    /// <para>
    /// <b>不要改回按字符串扫程序集。</b>那样会误判:库里确实存在 <c>keyboard-interactive</c>、
    /// <c>kbdinteractiveauthentication</c>、<c>kbdinteractivedevices</c> 这几个字面量,
    /// 但它们是它**解析** ssh_config 时认识的选项名,不是它实现了那套认证。
    /// 第一版就是这么写的,当场误报。
    /// </para>
    /// </remarks>
    [TestMethod]
    public void TheLibraryImplementsNoKeyboardInteractiveAuthMethod()
    {
        Type? userAuth = typeof(Credential).Assembly.GetType("Tmds.Ssh.UserAuthentication");
        Assert.IsNotNull(userAuth, "找不到 Tmds.Ssh.UserAuthentication —— 库的内部结构变了,这条用例要重写。");

        string[] methods =
        [
            .. userAuth.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                       .Select(t => t.Name)
                       .Where(n => n.EndsWith("Auth", StringComparison.Ordinal))
                       .OrderBy(n => n, StringComparer.Ordinal)
        ];

        Assert.IsNotEmpty(methods, "一种认证方式都没扫到 —— 反射失效了,这条用例等于没测。");
        Assert.Contains("PublicKeyAuth", methods, "连 PublicKeyAuth 都没扫到,说明扫描方式不对。");
        Assert.IsFalse(
            methods.Any(n => n.Contains("Interactive", StringComparison.OrdinalIgnoreCase)),
            "Tmds.Ssh 实现了键盘交互式认证:请实现真正的 2FA / OTP 流程,"
            + $"并撤掉 Msg_AuthFailedTwoFactorHint。当前实现的方式:{string.Join("、", methods)}");
    }
}

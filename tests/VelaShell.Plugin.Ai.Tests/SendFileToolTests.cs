using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// <c>send_file</c>:把已下载的文件当附件发进 IM 聊天。
/// </summary>
/// <remarks>
/// <b>这整个文件都是安全用例。</b>一个"能读本机文件并推进群里"的工具,就是一条完整的
/// 外泄通道 —— 群里任何一个人说一句"帮我看看 id_rsa",模型没有理由怀疑这句话。
/// 挡住它的不是黑名单(数不完),是<b>白名单一个目录</b>:只能发
/// <c>download_remote_file</c> 自己下下来的那些,于是发得出去的东西必然来自一台
/// 这次授权允许操作的服务器 —— 那一关在下载的时候就过了。
/// </remarks>
[TestClass]
public sealed class SendFileToolTests
{
    private static async Task<string> InvokeAsync(AgentToolbox toolbox, string path)
    {
        AIFunction function = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Single(f => f.Name == "send_file");
        object? result = await function.InvokeAsync(
            [with(new Dictionary<string, object?> { ["path"] = path })], CancellationToken.None);
        return result?.ToString() ?? "";
    }

    private static AgentToolbox WithSink(TestPluginContext context, List<string> sent)
        => new(context)
        {
            Approval = ApprovalMode.Bypass,
            FileSender = (path, _) =>
            {
                sent.Add(path);
                return Task.FromResult<string?>(null);
            }
        };

    /// <summary>下载目录里的文件照常发得出去。</summary>
    [TestMethod]
    public async Task AFileInTheDownloadsFolder_IsSent()
    {
        using var context = new TestPluginContext();
        string path = Downloaded(context, "2026-09-03.txt");
        List<string> sent = [];

        string result = await InvokeAsync(WithSink(context, sent), path);

        Assert.Contains("Sent", result);
        Assert.Contains(path, sent);
    }

    /// <summary>
    /// <b>下载目录之外的一律拒绝。</b>用户自己的密钥、桌面、浏览器数据,一个都够不着。
    /// </summary>
    [TestMethod]
    public async Task AFileOutsideTheDownloadsFolder_IsRefused()
    {
        using var context = new TestPluginContext();
        string outside = Path.Combine(context.DataDirectory, "id_rsa");
        File.WriteAllText(outside, "PRIVATE KEY");
        List<string> sent = [];

        string result = await InvokeAsync(WithSink(context, sent), outside);

        Assert.Contains("Refused", result);
        Assert.IsEmpty(sent, "范围之外的文件一个字节都不该发出去");
    }

    /// <summary>
    /// <c>..</c> 穿不出去 —— 路径先规范化再比。
    /// </summary>
    /// <remarks>
    /// 这是路径白名单最典型的绕法:名义上还在 <c>downloads/</c> 下面,
    /// 拼出来却落到了用户的主目录里。
    /// </remarks>
    [TestMethod]
    public async Task ATraversalOutOfTheDownloadsFolder_IsRefused()
    {
        using var context = new TestPluginContext();
        string secret = Path.Combine(context.DataDirectory, "secrets.txt");
        File.WriteAllText(secret, "token");
        string traversal = Path.Combine(context.DataDirectory, "downloads", "..", "secrets.txt");
        List<string> sent = [];

        string result = await InvokeAsync(WithSink(context, sent), traversal);

        Assert.Contains("Refused", result);
        Assert.IsEmpty(sent);
    }

    /// <summary>
    /// 前缀比对必须带目录分隔符,否则 <c>downloads-x/</c> 会被当成 <c>downloads/</c> 里面。
    /// </summary>
    [TestMethod]
    public async Task ASiblingFolderWithTheSamePrefix_IsRefused()
    {
        using var context = new TestPluginContext();
        string folder = Path.Combine(context.DataDirectory, "downloads-secret");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "x.txt");
        File.WriteAllText(path, "nope");
        List<string> sent = [];

        string result = await InvokeAsync(WithSink(context, sent), path);

        Assert.Contains("Refused", result);
        Assert.IsEmpty(sent);
    }

    /// <summary>路径对但文件不在,说的是"先下下来",而不是一句转义失败。</summary>
    [TestMethod]
    public async Task AMissingFile_SaysToDownloadItFirst()
    {
        using var context = new TestPluginContext();
        Directory.CreateDirectory(Path.Combine(context.DataDirectory, "downloads"));
        string path = Path.Combine(context.DataDirectory, "downloads", "not-there.txt");

        string result = await InvokeAsync(WithSink(context, []), path);

        Assert.Contains("No such local file", result);
    }

    /// <summary>审批被拒就不发,而且不该换个姿势再试。</summary>
    [TestMethod]
    public async Task ADeniedApproval_StopsTheSend()
    {
        using var context = new TestPluginContext();
        string path = Downloaded(context, "log.txt");
        List<string> sent = [];
        var toolbox = new AgentToolbox(context)
        {
            Approval = ApprovalMode.Ask,
            ApprovalHandler = _ => Task.FromResult(false),
            FileSender = (p, _) =>
            {
                sent.Add(p);
                return Task.FromResult<string?>(null);
            }
        };

        string result = await InvokeAsync(toolbox, path);

        Assert.Contains("DENIED", result);
        Assert.IsEmpty(sent);
    }

    /// <summary>
    /// <b>没有落点就不注册这个工具。</b>聊天面板与对外 MCP 走的就是这条路。
    /// </summary>
    /// <remarks>
    /// 摆一个永远失败的工具比不摆更糟:模型看得见它,就会反复去试,
    /// 然后把这次失败当成自己的问题,绕着弯子重来。
    /// </remarks>
    [TestMethod]
    public void WithNoChatToSendInto_TheToolIsNotEvenRegistered()
    {
        using var context = new TestPluginContext();

        string[] names = [.. new AgentToolbox(context).CreateTools(ChatMode.Agent)
            .OfType<AIFunction>().Select(f => f.Name)];

        Assert.DoesNotContain("send_file", names);
    }

    /// <summary>渠道那头失败时,原话回给模型,而不是假装发出去了。</summary>
    [TestMethod]
    public async Task AChannelFailure_IsReportedNotSwallowed()
    {
        using var context = new TestPluginContext();
        string path = Downloaded(context, "big.tar.gz");
        var toolbox = new AgentToolbox(context)
        {
            Approval = ApprovalMode.Bypass,
            FileSender = (_, _) => Task.FromResult<string?>("Feishu accepts at most 30 MB.")
        };

        string result = await InvokeAsync(toolbox, path);

        Assert.Contains("30 MB", result);
        Assert.DoesNotContain("Sent", result);
    }

    private static string Downloaded(TestPluginContext context, string name)
    {
        string folder = Path.Combine(context.DataDirectory, "downloads");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "2026-09-03 10:00:00 INFO started");
        return path;
    }
}

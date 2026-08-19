using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 插件激活:注册的连接类型描述本身是产品的一部分(它决定连接对话框长什么样),
/// 所以这里按 SDK 的测试替身把它取出来逐项验。
/// </summary>
[TestClass]
public sealed class RedisPluginActivationTests
{
    private static TestPluginContext NewContext(string locale = "zh-Hans")
    {
        var context = new TestPluginContext { PluginId = "velashell.redis" };
        context.HostInfo.Locale = locale;
        return context;
    }

    [TestMethod]
    public async Task Activate_RegistersOneWorkspaceUnderThePluginId()
    {
        using TestPluginContext context = NewContext();
        var plugin = new RedisPlugin();

        await plugin.ActivateAsync(context, CancellationToken.None);

        WorkspaceDescriptor descriptor = context.RecordingWorkspaces.Registered.Single();
        Assert.AreEqual("velashell.redis", descriptor.Id);
        Assert.AreEqual("Redis", descriptor.DisplayName);
        Assert.AreEqual(6379, descriptor.DefaultPort);
    }

    [TestMethod]
    public async Task Activate_DeclaresAnonymousAndCertificateTrust()
    {
        using TestPluginContext context = NewContext();
        var plugin = new RedisPlugin();

        await plugin.ActivateAsync(context, CancellationToken.None);

        WorkspaceDescriptor descriptor = context.RecordingWorkspaces.Registered.Single();
        // 匿名是一条正当路径(开发机上的 Redis 通常没有 requirepass),宿主据此不弹登录框。
        Assert.IsTrue(descriptor.Features.HasFlag(WorkspaceFeatures.AnonymousAccess));
        Assert.IsTrue(descriptor.Features.HasFlag(WorkspaceFeatures.CertificateTrust));
    }

    [TestMethod]
    public async Task Activate_PointsThumbprintKeyAtARealHiddenField()
    {
        // 指纹字段不存在时,宿主的"信任该证书"点了等于没点 —— 这类失败只在真机上暴露,
        // 代价是用户对着同一个弹窗点三次都连不上。SDK 侧的 Register 也会校验这一条。
        using TestPluginContext context = NewContext();
        var plugin = new RedisPlugin();

        await plugin.ActivateAsync(context, CancellationToken.None);

        WorkspaceDescriptor descriptor = context.RecordingWorkspaces.Registered.Single();
        ProtocolSettingField field = descriptor.Fields.Single(f => f.Key == descriptor.TrustedThumbprintSettingKey);
        Assert.IsTrue(field.IsHidden);
    }

    [TestMethod]
    public async Task Activate_LocalizesLabels()
    {
        using TestPluginContext chinese = NewContext("zh-Hans");
        using TestPluginContext english = NewContext("en");

        await new RedisPlugin().ActivateAsync(chinese, CancellationToken.None);
        await new RedisPlugin().ActivateAsync(english, CancellationToken.None);

        Assert.AreEqual("服务地址", chinese.RecordingWorkspaces.Registered.Single().HostLabel);
        Assert.AreEqual("Server address", english.RecordingWorkspaces.Registered.Single().HostLabel);
    }

    [TestMethod]
    public async Task LocaleChanged_ReRegistersWithoutSwappingTheProvider()
    {
        // provider 换了实例就等于"换了实现",注册表会通知宿主关掉该类型名下所有已打开的
        // 文档 —— 用户只是切了个语言,标签页不该全没了。
        using TestPluginContext context = NewContext("en");
        var plugin = new RedisPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        IWorkspaceProvider? before = context.RecordingWorkspaces.GetProvider("velashell.redis");

        context.HostEvents.RaiseLocaleChanged("zh-Hans");

        IWorkspaceProvider? after = context.RecordingWorkspaces.GetProvider("velashell.redis");
        Assert.AreSame(before, after);
        Assert.AreEqual("服务地址", context.RecordingWorkspaces.Registered.Single().HostLabel);
    }

    [TestMethod]
    public async Task Deactivate_UnregistersTheWorkspace()
    {
        // 不撤注销,ALC 就回收不掉,而用户还会在连接页看到一个再也连不上的页签。
        using TestPluginContext context = NewContext();
        var plugin = new RedisPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);

        await plugin.DeactivateAsync(CancellationToken.None);

        Assert.IsEmpty(context.RecordingWorkspaces.Registered);
    }
}

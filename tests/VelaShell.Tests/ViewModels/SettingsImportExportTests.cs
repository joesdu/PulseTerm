using System.Text.Json;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 设置的导入导出。导出文件的用途决定了它一定会离开本机(备份、换机、贴给别人排查),
/// 所以这里守两件事:秘密不能跟着走,以及每一次导入导出都要有可见的结果。
/// </summary>
[TestClass]
[TestCategory("Settings")]
public sealed class SettingsImportExportTests
{
    private const string SecretProxyPassword = "hunter2-should-never-leave-this-machine";

    /// <summary>导出里不能出现代理密码。</summary>
    /// <remarks>
    /// 旧实现直接序列化整份 AppSettings。文件看上去只是一堆设置,用户不会想到里面有凭据,
    /// 于是它被随手发到工单、同步到网盘、提交进仓库。
    /// </remarks>
    [TestMethod]
    public async Task Export_DoesNotCarryTheProxyPassword()
    {
        SettingsViewModel viewModel = await CreateAsync(ConfiguredProxy());

        string json = viewModel.BuildExportJson();

        Assert.DoesNotContain(SecretProxyPassword, json, "代理密码被写进导出文件了。");
    }

    /// <summary>脱敏只作用于导出的副本,不能顺手把用户当前配好的密码清掉。</summary>
    [TestMethod]
    public async Task Export_LeavesTheLiveSettingsAlone()
    {
        SettingsViewModel viewModel = await CreateAsync(ConfiguredProxy());

        viewModel.BuildExportJson();

        Assert.AreEqual(SecretProxyPassword, viewModel.Proxy.Password, "导出把内存里的密码也抹了。");
    }

    /// <summary>非秘密设置要能原样往返。</summary>
    [TestMethod]
    public async Task Export_ThenImport_RoundTripsNonSecretSettings()
    {
        SettingsViewModel source = await CreateAsync(ConfiguredProxy());
        source.General.ConnectTimeoutSeconds = 42;
        string json = source.BuildExportJson();

        SettingsViewModel target = await CreateAsync(new AppSettings());
        SettingsImportResult result = target.TryApplyImportedJson(json);

        Assert.AreEqual(SettingsImportResult.AppliedNeedsSecrets, result);
        Assert.AreEqual(42, target.General.ConnectTimeoutSeconds);
        Assert.AreEqual("proxy.example.com", target.Proxy.Host);
    }

    /// <summary>
    /// 导入一份"代理开着、用户名在、密码空"的配置时,必须告诉用户密码得重填。
    /// </summary>
    [TestMethod]
    public async Task Import_ReportsThatSecretsMustBeRefilled()
    {
        SettingsViewModel viewModel = await CreateAsync(new AppSettings());

        SettingsImportResult result = viewModel.TryApplyImportedJson(
            JsonSerializer.Serialize(new
            {
                proxy = new { type = "socks5", host = "h", port = 1080, username = "u", password = "" }
            }));

        Assert.AreEqual(SettingsImportResult.AppliedNeedsSecrets, result);
    }

    /// <summary>不需要认证的代理不该冒出"去补密码"的提示。</summary>
    [TestMethod]
    public async Task Import_WithoutProxyAuth_DoesNotAskForSecrets()
    {
        SettingsViewModel viewModel = await CreateAsync(new AppSettings());

        SettingsImportResult result = viewModel.TryApplyImportedJson(
            JsonSerializer.Serialize(new { proxy = new { type = "none" } }));

        Assert.AreEqual(SettingsImportResult.Applied, result);
    }

    /// <summary>
    /// 非法内容要报失败,而且不能动现有设置。
    /// </summary>
    /// <remarks>
    /// 调用方以前把返回值整个丢掉,选错文件时界面毫无反应 —— 用户只能靠猜。
    /// </remarks>
    [TestMethod]
    public async Task Import_OfSomethingThatIsNotSettings_FailsAndChangesNothing()
    {
        SettingsViewModel viewModel = await CreateAsync(ConfiguredProxy());

        Assert.AreEqual(SettingsImportResult.Invalid, viewModel.TryApplyImportedJson("{ not json"));
        Assert.AreEqual(SettingsImportResult.Invalid, viewModel.TryApplyImportedJson("null"));
        Assert.AreEqual(SecretProxyPassword, viewModel.Proxy.Password, "导入失败却改动了现有设置。");
    }

    private static AppSettings ConfiguredProxy()
    {
        var settings = new AppSettings();
        settings.Proxy.Type = "socks5";
        settings.Proxy.Host = "proxy.example.com";
        settings.Proxy.Port = 1080;
        settings.Proxy.Username = "vela";
        settings.Proxy.Password = SecretProxyPassword;
        return settings;
    }

    private static async Task<SettingsViewModel> CreateAsync(AppSettings initial)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => Task.FromResult(initial));
        var viewModel = new SettingsViewModel(settings, new ThemeService(initial.Theme));
        await viewModel.LoadCommand.Execute().FirstAsync();
        return viewModel;
    }
}

using System.Globalization;
using FluentAssertions;
using PulseTerm.Core.Localization;
using PulseTerm.Core.Resources;
using Xunit;

namespace PulseTerm.Core.Tests.Resources;

[Trait("Category", "i18n")]
public class LocalizationTests : IDisposable
{
    private readonly CultureInfo _originalCulture;
    
    public LocalizationTests()
    {
        _originalCulture = CultureInfo.CurrentUICulture;
    }
    
    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _originalCulture;
        CultureInfo.CurrentCulture = _originalCulture;
    }
    
    [Fact]
    public void DefaultCulture_ReturnsEnglishStrings()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        
        Strings.QuickConnect.Should().Be("Quick Connect");
        Strings.RecentConnections.Should().Be("Recent Connections");
        Strings.Settings.Should().Be("Settings");
        Strings.NewTab.Should().Be("New Tab");
        Strings.Connect.Should().Be("Connect");
    }
    
    [Fact]
    public void ChineseCulture_ReturnsChineseStrings()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
        
        Strings.QuickConnect.Should().Be("快速连接");
        Strings.RecentConnections.Should().Be("最近连接");
        Strings.Settings.Should().Be("设置");
        Strings.NewTab.Should().Be("新标签页");
        Strings.Connect.Should().Be("连接");
    }
    
    [Fact]
    public void LocalizationService_GetString_ReturnsEnglishByDefault()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        var service = new LocalizationService();
        
        service.GetString("QuickConnect").Should().Be("Quick Connect");
        service.GetString("ServerGroups").Should().Be("Server Groups");
        service.GetString("Disconnect").Should().Be("Disconnect");
    }
    
    [Fact]
    public void LocalizationService_GetString_ReturnsChineseForZhCN()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
        var service = new LocalizationService();
        
        service.GetString("QuickConnect").Should().Be("快速连接");
        service.GetString("ServerGroups").Should().Be("服务器分组");
        service.GetString("Disconnect").Should().Be("断开");
    }
    
    [Fact]
    public void LocalizationService_GetString_MissingKey_ReturnsKeyName()
    {
        var service = new LocalizationService();
        
        service.GetString("NonExistentKey").Should().Be("NonExistentKey");
    }
    
    [Fact]
    public void LocalizationService_CurrentLanguage_ReturnsCurrentUICulture()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        var service = new LocalizationService();
        
        service.CurrentLanguage.Should().Be("en");
        
        CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
        service.CurrentLanguage.Should().Be("zh-CN");
    }
    
    [Fact]
    public void LocalizationService_SetLanguage_ChangesCurrentCulture()
    {
        var service = new LocalizationService();
        
        service.SetLanguage("zh-CN");
        CultureInfo.CurrentUICulture.Name.Should().Be("zh-CN");
        service.CurrentLanguage.Should().Be("zh-CN");
        
        service.SetLanguage("en");
        CultureInfo.CurrentUICulture.Name.Should().Be("en");
        service.CurrentLanguage.Should().Be("en");
    }
    
    [Fact]
    public void AllRequiredStrings_ExistInEnglishResx()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        
        Strings.QuickConnect.Should().NotBeNullOrEmpty();
        Strings.RecentConnections.Should().NotBeNullOrEmpty();
        Strings.ServerGroups.Should().NotBeNullOrEmpty();
        Strings.Settings.Should().NotBeNullOrEmpty();
        Strings.Notifications.Should().NotBeNullOrEmpty();
        
        Strings.NewTab.Should().NotBeNullOrEmpty();
        Strings.CloseTab.Should().NotBeNullOrEmpty();
        Strings.Search.Should().NotBeNullOrEmpty();
        Strings.Copy.Should().NotBeNullOrEmpty();
        Strings.Split.Should().NotBeNullOrEmpty();
        Strings.Broadcast.Should().NotBeNullOrEmpty();
        Strings.SyncGroup.Should().NotBeNullOrEmpty();
        
        Strings.FileName.Should().NotBeNullOrEmpty();
        Strings.Size.Should().NotBeNullOrEmpty();
        Strings.Permissions.Should().NotBeNullOrEmpty();
        Strings.Modified.Should().NotBeNullOrEmpty();
        Strings.Upload.Should().NotBeNullOrEmpty();
        Strings.Download.Should().NotBeNullOrEmpty();
        Strings.Refresh.Should().NotBeNullOrEmpty();
        
        Strings.LocalForward.Should().NotBeNullOrEmpty();
        Strings.RemoteForward.Should().NotBeNullOrEmpty();
        Strings.LocalPort.Should().NotBeNullOrEmpty();
        Strings.RemoteAddress.Should().NotBeNullOrEmpty();
        Strings.NewTunnel.Should().NotBeNullOrEmpty();
        Strings.ActiveTunnels.Should().NotBeNullOrEmpty();
        
        Strings.SearchCommands.Should().NotBeNullOrEmpty();
        Strings.SystemMonitor.Should().NotBeNullOrEmpty();
        Strings.Network.Should().NotBeNullOrEmpty();
        Strings.Docker.Should().NotBeNullOrEmpty();
        Strings.Custom.Should().NotBeNullOrEmpty();
        
        Strings.Connected.Should().NotBeNullOrEmpty();
        Strings.Connecting.Should().NotBeNullOrEmpty();
        Strings.Disconnected.Should().NotBeNullOrEmpty();
        Strings.Latency.Should().NotBeNullOrEmpty();
        
        Strings.Connect.Should().NotBeNullOrEmpty();
        Strings.Disconnect.Should().NotBeNullOrEmpty();
        Strings.Save.Should().NotBeNullOrEmpty();
        Strings.Cancel.Should().NotBeNullOrEmpty();
        Strings.Delete.Should().NotBeNullOrEmpty();
        Strings.Edit.Should().NotBeNullOrEmpty();
        Strings.OK.Should().NotBeNullOrEmpty();
        Strings.Error.Should().NotBeNullOrEmpty();
        Strings.Warning.Should().NotBeNullOrEmpty();
        
        Strings.Language.Should().NotBeNullOrEmpty();
        Strings.Theme.Should().NotBeNullOrEmpty();
        Strings.Font.Should().NotBeNullOrEmpty();
        Strings.FontSize.Should().NotBeNullOrEmpty();
        Strings.ScrollbackLines.Should().NotBeNullOrEmpty();
        
        Strings.Password.Should().NotBeNullOrEmpty();
        Strings.PrivateKey.Should().NotBeNullOrEmpty();
        Strings.Username.Should().NotBeNullOrEmpty();
        Strings.Host.Should().NotBeNullOrEmpty();
        Strings.Port.Should().NotBeNullOrEmpty();
        Strings.HostKeyVerification.Should().NotBeNullOrEmpty();
        Strings.TrustThisHost.Should().NotBeNullOrEmpty();
    }
    
    [Fact]
    public void AllRequiredStrings_ExistInChineseResx()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
        
        Strings.QuickConnect.Should().NotBeNullOrEmpty();
        Strings.RecentConnections.Should().NotBeNullOrEmpty();
        Strings.ServerGroups.Should().NotBeNullOrEmpty();
        Strings.Settings.Should().NotBeNullOrEmpty();
        Strings.Notifications.Should().NotBeNullOrEmpty();
        
        Strings.NewTab.Should().NotBeNullOrEmpty();
        Strings.CloseTab.Should().NotBeNullOrEmpty();
        Strings.Search.Should().NotBeNullOrEmpty();
        Strings.Copy.Should().NotBeNullOrEmpty();
        Strings.Split.Should().NotBeNullOrEmpty();
        Strings.Broadcast.Should().NotBeNullOrEmpty();
        Strings.SyncGroup.Should().NotBeNullOrEmpty();
        
        Strings.FileName.Should().NotBeNullOrEmpty();
        Strings.Size.Should().NotBeNullOrEmpty();
        Strings.Permissions.Should().NotBeNullOrEmpty();
        Strings.Modified.Should().NotBeNullOrEmpty();
        Strings.Upload.Should().NotBeNullOrEmpty();
        Strings.Download.Should().NotBeNullOrEmpty();
        Strings.Refresh.Should().NotBeNullOrEmpty();
        
        Strings.LocalForward.Should().NotBeNullOrEmpty();
        Strings.RemoteForward.Should().NotBeNullOrEmpty();
        Strings.LocalPort.Should().NotBeNullOrEmpty();
        Strings.RemoteAddress.Should().NotBeNullOrEmpty();
        Strings.NewTunnel.Should().NotBeNullOrEmpty();
        Strings.ActiveTunnels.Should().NotBeNullOrEmpty();
        
        Strings.SearchCommands.Should().NotBeNullOrEmpty();
        Strings.SystemMonitor.Should().NotBeNullOrEmpty();
        Strings.Network.Should().NotBeNullOrEmpty();
        Strings.Docker.Should().NotBeNullOrEmpty();
        Strings.Custom.Should().NotBeNullOrEmpty();
        
        Strings.Connected.Should().NotBeNullOrEmpty();
        Strings.Connecting.Should().NotBeNullOrEmpty();
        Strings.Disconnected.Should().NotBeNullOrEmpty();
        Strings.Latency.Should().NotBeNullOrEmpty();
        
        Strings.Connect.Should().NotBeNullOrEmpty();
        Strings.Disconnect.Should().NotBeNullOrEmpty();
        Strings.Save.Should().NotBeNullOrEmpty();
        Strings.Cancel.Should().NotBeNullOrEmpty();
        Strings.Delete.Should().NotBeNullOrEmpty();
        Strings.Edit.Should().NotBeNullOrEmpty();
        Strings.OK.Should().NotBeNullOrEmpty();
        Strings.Error.Should().NotBeNullOrEmpty();
        Strings.Warning.Should().NotBeNullOrEmpty();
        
        Strings.Language.Should().NotBeNullOrEmpty();
        Strings.Theme.Should().NotBeNullOrEmpty();
        Strings.Font.Should().NotBeNullOrEmpty();
        Strings.FontSize.Should().NotBeNullOrEmpty();
        Strings.ScrollbackLines.Should().NotBeNullOrEmpty();
        
        Strings.Password.Should().NotBeNullOrEmpty();
        Strings.PrivateKey.Should().NotBeNullOrEmpty();
        Strings.Username.Should().NotBeNullOrEmpty();
        Strings.Host.Should().NotBeNullOrEmpty();
        Strings.Port.Should().NotBeNullOrEmpty();
        Strings.HostKeyVerification.Should().NotBeNullOrEmpty();
        Strings.TrustThisHost.Should().NotBeNullOrEmpty();
    }
}

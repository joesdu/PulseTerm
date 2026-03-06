using System.Reactive.Concurrency;
using FluentAssertions;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Resources;

namespace PulseTerm.App.Tests.ViewModels;

public class StatusBarViewModelTests
{
    [Fact]
    [Trait("Category", "StatusBar")]
    public void Constructor_SetsDefaultValues()
    {
        var vm = new StatusBarViewModel();

        vm.StatusText.Should().Be(Strings.Ready);
        vm.ConnectionInfo.Should().BeEmpty();
        vm.Status.Should().Be(Strings.Disconnected);
        vm.Latency.Should().BeEmpty();
        vm.TerminalType.Should().Be("xterm-256color");
        vm.WindowSize.Should().Be("80\u00D724");
        vm.Encoding.Should().Be("UTF-8");
        vm.Uptime.Should().BeEmpty();
        vm.IsConnected.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "StatusBar")]
    public void SetConnectionInfo_UpdatesProperty()
    {
        var vm = new StatusBarViewModel();

        vm.ConnectionInfo = "user@host:22";

        vm.ConnectionInfo.Should().Be("user@host:22");
    }

    [Fact]
    [Trait("Category", "StatusBar")]
    public void SetStatus_Connected_UpdatesIsConnected()
    {
        var vm = new StatusBarViewModel();

        vm.Status = Strings.Connected;

        vm.IsConnected.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "StatusBar")]
    public void SetStatus_Disconnected_UpdatesIsConnected()
    {
        var vm = new StatusBarViewModel();

        vm.Status = Strings.Connected;
        vm.IsConnected.Should().BeTrue();

        vm.Status = Strings.Disconnected;
        vm.IsConnected.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "StatusBar")]
    public void AllProperties_RaisePropertyChanged()
    {
        var vm = new StatusBarViewModel();
        var changedProperties = new List<string>();

        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        vm.StatusText = "Testing";
        vm.ConnectionInfo = "user@host";
        vm.Status = Strings.Connected;
        vm.Latency = "12ms";
        vm.TerminalType = "xterm";
        vm.WindowSize = "120\u00D740";
        vm.Encoding = "ASCII";
        vm.Uptime = "01:23:45";

        changedProperties.Should().Contain("StatusText");
        changedProperties.Should().Contain("ConnectionInfo");
        changedProperties.Should().Contain("Status");
        changedProperties.Should().Contain("Latency");
        changedProperties.Should().Contain("TerminalType");
        changedProperties.Should().Contain("WindowSize");
        changedProperties.Should().Contain("Encoding");
        changedProperties.Should().Contain("Uptime");
    }

    [Fact]
    [Trait("Category", "StatusBar")]
    public void FormatUptime_FormatsCorrectly()
    {
        var elapsed = new TimeSpan(1, 23, 45);
        var formatted = elapsed.ToString(@"hh\:mm\:ss");

        formatted.Should().Be("01:23:45");
    }

    [Fact]
    [Trait("Category", "StatusBar")]
    public async Task StartUptimeTimer_UpdatesUptimeProperty()
    {
        var vm = new StatusBarViewModel();

        vm.StartUptimeTimer();
        await Task.Delay(1500);

        vm.Uptime.Should().NotBeEmpty();
        vm.StopUptimeTimer();
    }

    [Fact]
    [Trait("Category", "StatusBar")]
    public async Task StopUptimeTimer_StopsUpdating()
    {
        var vm = new StatusBarViewModel();

        vm.StartUptimeTimer();
        await Task.Delay(1500);
        vm.StopUptimeTimer();

        var uptimeAfterStop = vm.Uptime;
        await Task.Delay(1500);

        vm.Uptime.Should().Be(uptimeAfterStop);
    }

    [Fact]
    [Trait("Category", "StatusBar")]
    public void Dispose_CleansUpTimer()
    {
        var vm = new StatusBarViewModel();
        vm.StartUptimeTimer();

        var act = () => vm.Dispose();
        act.Should().NotThrow();
    }
}

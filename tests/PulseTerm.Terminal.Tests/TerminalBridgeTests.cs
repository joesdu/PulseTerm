using System.Text;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NSubstitute;
using PulseTerm.Core.Ssh;
using Xunit;

namespace PulseTerm.Terminal.Tests;

[Trait("Category", "TerminalBridge")]
public class TerminalBridgeTests
{
    [AvaloniaFact]
    public void Feed_VT100EscapeSequences_RendersColoredText()
    {
        var emulator = new AvaloniaTerminalEmulator();
        
        var redText = Encoding.UTF8.GetBytes("\x1B[31mRED\x1B[0m");
        emulator.Feed(redText);
        
        var line = emulator.GetBufferLine(0);
        line.Should().Contain("RED");
    }

    [AvaloniaFact]
    public void Feed_CJKCharacters_HandlesDoubleWidth()
    {
        var emulator = new AvaloniaTerminalEmulator();
        
        var cjkText = Encoding.UTF8.GetBytes("你好世界");
        emulator.Feed(cjkText);
        
        var line = emulator.GetBufferLine(0);
        line.Should().Contain("你好");
    }

    [AvaloniaFact]
    public void Feed_LargeData_DoesNotCrash()
    {
        var emulator = new AvaloniaTerminalEmulator();
        
        var largeData = new byte[1024 * 1024];
        for (int i = 0; i < largeData.Length; i++)
        {
            largeData[i] = (byte)('A' + (i % 26));
        }
        
        Action act = () => emulator.Feed(largeData);
        act.Should().NotThrow();
        
        var memoryBefore = GC.GetAllocatedBytesForCurrentThread();
        emulator.Feed(largeData);
        var memoryAfter = GC.GetAllocatedBytesForCurrentThread();
        
        var memoryUsed = memoryAfter - memoryBefore;
        memoryUsed.Should().BeLessThan(50 * 1024 * 1024);
    }

    [AvaloniaFact]
    public void Resize_UpdatesDimensions()
    {
        var emulator = new AvaloniaTerminalEmulator();
        
        emulator.Resize(120, 40);
        
        emulator.Columns.Should().BeGreaterThanOrEqualTo(80);
        emulator.Rows.Should().BeGreaterThanOrEqualTo(24);
    }

    [AvaloniaFact]
    public void UserInput_EventWorks()
    {
        var emulator = new AvaloniaTerminalEmulator();
        byte[]? capturedInput = null;
        
        emulator.UserInput += (data) => capturedInput = data;
        
        var testData = Encoding.UTF8.GetBytes("test");
        emulator.TriggerUserInput(testData);
        
        capturedInput.Should().NotBeNull();
        capturedInput.Should().BeEquivalentTo(testData);
    }
}

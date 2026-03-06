using FluentAssertions;
using PulseTerm.Terminal;
using Xunit;

namespace PulseTerm.Terminal.Tests;

[Trait("Category", "Scrollback")]
public class ScrollbackBufferTests
{
    [Fact]
    public void AddLine_StoresLineCorrectly()
    {
        var buffer = new ScrollbackBuffer(100);
        var line = new TerminalLine { Content = "hello world" };

        buffer.AddLine(line);

        buffer.ScrollbackLineCount.Should().Be(1);
        buffer.GetLine(0).Content.Should().Be("hello world");
    }

    [Fact]
    public void AddLine_CircularWrap_OverwritesOldest()
    {
        var buffer = new ScrollbackBuffer(3);

        buffer.AddLine(new TerminalLine { Content = "line0" });
        buffer.AddLine(new TerminalLine { Content = "line1" });
        buffer.AddLine(new TerminalLine { Content = "line2" });
        buffer.AddLine(new TerminalLine { Content = "line3" });

        buffer.ScrollbackLineCount.Should().Be(3);
        buffer.GetLine(0).Content.Should().Be("line1");
        buffer.GetLine(1).Content.Should().Be("line2");
        buffer.GetLine(2).Content.Should().Be("line3");
    }

    [Fact]
    public void GetLine_ReturnsCorrectContent()
    {
        var buffer = new ScrollbackBuffer(100);

        for (int i = 0; i < 10; i++)
        {
            buffer.AddLine(new TerminalLine { Content = $"line {i}" });
        }

        buffer.GetLine(0).Content.Should().Be("line 0");
        buffer.GetLine(5).Content.Should().Be("line 5");
        buffer.GetLine(9).Content.Should().Be("line 9");
    }

    [Fact]
    public void ScrollTo_MovesViewport()
    {
        var buffer = new ScrollbackBuffer(100);
        buffer.VisibleRows = 24;

        for (int i = 0; i < 50; i++)
        {
            buffer.AddLine(new TerminalLine { Content = $"line {i}" });
        }

        buffer.ScrollTo(10);

        buffer.ViewportRow.Should().Be(10);
    }

    [Fact]
    public void ScrollUp_MovesViewportUp()
    {
        var buffer = new ScrollbackBuffer(100);
        buffer.VisibleRows = 24;

        for (int i = 0; i < 50; i++)
        {
            buffer.AddLine(new TerminalLine { Content = $"line {i}" });
        }

        buffer.ScrollTo(30);
        buffer.ScrollUp(5);

        buffer.ViewportRow.Should().Be(25);
    }

    [Fact]
    public void ScrollDown_MovesViewportDown()
    {
        var buffer = new ScrollbackBuffer(100);
        buffer.VisibleRows = 24;

        for (int i = 0; i < 50; i++)
        {
            buffer.AddLine(new TerminalLine { Content = $"line {i}" });
        }

        buffer.ScrollTo(10);
        buffer.ScrollDown(5);

        buffer.ViewportRow.Should().Be(15);
    }

    [Fact]
    public void Search_FindsTextAcrossScrollback()
    {
        var buffer = new ScrollbackBuffer(100);

        buffer.AddLine(new TerminalLine { Content = "foo bar baz" });
        buffer.AddLine(new TerminalLine { Content = "hello world" });
        buffer.AddLine(new TerminalLine { Content = "foo again" });

        var matches = buffer.Search("foo");

        matches.Should().HaveCount(2);
        matches[0].Row.Should().Be(0);
        matches[0].Column.Should().Be(0);
        matches[0].Length.Should().Be(3);
        matches[1].Row.Should().Be(2);
        matches[1].Column.Should().Be(0);
        matches[1].Length.Should().Be(3);
    }

    [Fact]
    public void TotalLines_CountsScrollbackAndVisible()
    {
        var buffer = new ScrollbackBuffer(100);
        buffer.VisibleRows = 24;

        for (int i = 0; i < 10; i++)
        {
            buffer.AddLine(new TerminalLine { Content = $"line {i}" });
        }

        buffer.TotalLines.Should().Be(10 + 24);
    }

    [Fact]
    public void ConfigurableMaxLines()
    {
        var buffer = new ScrollbackBuffer(500);
        buffer.MaxLines.Should().Be(500);

        var defaultBuffer = new ScrollbackBuffer();
        defaultBuffer.MaxLines.Should().Be(10000);
    }

    [Fact]
    public void Clear_RemovesAllLines()
    {
        var buffer = new ScrollbackBuffer(100);

        for (int i = 0; i < 20; i++)
        {
            buffer.AddLine(new TerminalLine { Content = $"line {i}" });
        }

        buffer.Clear();

        buffer.ScrollbackLineCount.Should().Be(0);
        buffer.ViewportRow.Should().Be(0);
    }

    [Fact]
    public void ScrollUp_ClampsToZero()
    {
        var buffer = new ScrollbackBuffer(100);
        buffer.VisibleRows = 24;

        for (int i = 0; i < 10; i++)
        {
            buffer.AddLine(new TerminalLine { Content = $"line {i}" });
        }

        buffer.ScrollTo(3);
        buffer.ScrollUp(100);

        buffer.ViewportRow.Should().Be(0);
    }

    [Fact]
    public void ScrollDown_ClampsToMaxViewport()
    {
        var buffer = new ScrollbackBuffer(100);
        buffer.VisibleRows = 24;

        for (int i = 0; i < 50; i++)
        {
            buffer.AddLine(new TerminalLine { Content = $"line {i}" });
        }

        buffer.ScrollDown(1000);

        buffer.ViewportRow.Should().Be(50);
    }

    [Fact]
    public void Search_FindsMultipleMatchesInSameLine()
    {
        var buffer = new ScrollbackBuffer(100);

        buffer.AddLine(new TerminalLine { Content = "ab ab ab" });

        var matches = buffer.Search("ab");

        matches.Should().HaveCount(3);
        matches[0].Column.Should().Be(0);
        matches[1].Column.Should().Be(3);
        matches[2].Column.Should().Be(6);
    }

    [Fact]
    public void GetLine_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var buffer = new ScrollbackBuffer(100);
        buffer.AddLine(new TerminalLine { Content = "only line" });

        var act = () => buffer.GetLine(5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CircularWrap_ManyOverwrites_MaintainsCorrectOrder()
    {
        var buffer = new ScrollbackBuffer(5);

        for (int i = 0; i < 20; i++)
        {
            buffer.AddLine(new TerminalLine { Content = $"line {i}" });
        }

        buffer.ScrollbackLineCount.Should().Be(5);
        buffer.GetLine(0).Content.Should().Be("line 15");
        buffer.GetLine(1).Content.Should().Be("line 16");
        buffer.GetLine(2).Content.Should().Be("line 17");
        buffer.GetLine(3).Content.Should().Be("line 18");
        buffer.GetLine(4).Content.Should().Be("line 19");
    }
}

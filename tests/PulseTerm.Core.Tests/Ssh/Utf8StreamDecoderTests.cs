using FluentAssertions;
using Xunit;
using PulseTerm.Core.Ssh;
using System.Text;

namespace PulseTerm.Core.Tests.Ssh;

[Trait("Category", "SshConnection")]
public class Utf8StreamDecoderTests
{
    [Fact]
    public void DecodeBytes_CompleteUtf8Sequences_ReturnsCorrectString()
    {
        var decoder = new Utf8StreamDecoder();
        var input = Encoding.UTF8.GetBytes("Hello World");

        var result = decoder.DecodeBytes(input);

        result.Should().Be("Hello World");
    }

    [Fact]
    public void DecodeBytes_EmptyInput_ReturnsEmptyString()
    {
        var decoder = new Utf8StreamDecoder();
        var input = Array.Empty<byte>();

        var result = decoder.DecodeBytes(input);

        result.Should().BeEmpty();
    }

    [Fact]
    public void DecodeBytes_Split2ByteChar_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        var fullChar = "é";
        var bytes = Encoding.UTF8.GetBytes(fullChar);
        bytes.Length.Should().Be(2);

        var result1 = decoder.DecodeBytes(new[] { bytes[0] });
        result1.Should().BeEmpty();

        var result2 = decoder.DecodeBytes(new[] { bytes[1] });
        result2.Should().Be(fullChar);
    }

    [Fact]
    public void DecodeBytes_Split3ByteCjkChar_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        var cjkChar = "你";
        var bytes = Encoding.UTF8.GetBytes(cjkChar);
        bytes.Length.Should().Be(3);

        var result1 = decoder.DecodeBytes(new[] { bytes[0] });
        result1.Should().BeEmpty();

        var result2 = decoder.DecodeBytes(new[] { bytes[1] });
        result2.Should().BeEmpty();

        var result3 = decoder.DecodeBytes(new[] { bytes[2] });
        result3.Should().Be(cjkChar);
    }

    [Fact]
    public void DecodeBytes_Split3ByteCjkChar_TwoBytesThenOne_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        var cjkChar = "好";
        var bytes = Encoding.UTF8.GetBytes(cjkChar);
        bytes.Length.Should().Be(3);

        var result1 = decoder.DecodeBytes(new[] { bytes[0], bytes[1] });
        result1.Should().BeEmpty();

        var result2 = decoder.DecodeBytes(new[] { bytes[2] });
        result2.Should().Be(cjkChar);
    }

    [Fact]
    public void DecodeBytes_Split4ByteEmoji_BuffersAndCompletes()
    {
        var decoder = new Utf8StreamDecoder();
        var emoji = "😀";
        var bytes = Encoding.UTF8.GetBytes(emoji);
        bytes.Length.Should().Be(4);

        var result1 = decoder.DecodeBytes(new[] { bytes[0] });
        result1.Should().BeEmpty();

        var result2 = decoder.DecodeBytes(new[] { bytes[1] });
        result2.Should().BeEmpty();

        var result3 = decoder.DecodeBytes(new[] { bytes[2] });
        result3.Should().BeEmpty();

        var result4 = decoder.DecodeBytes(new[] { bytes[3] });
        result4.Should().Be(emoji);
    }

    [Fact]
    public void DecodeBytes_MixedCompleteAndSplit_DecodesCorrectly()
    {
        var decoder = new Utf8StreamDecoder();
        var text = "Hello你";
        var bytes = Encoding.UTF8.GetBytes(text);

        var result1 = decoder.DecodeBytes(bytes.Take(7).ToArray());
        result1.Should().Be("Hello");

        var result2 = decoder.DecodeBytes(bytes.Skip(7).ToArray());
        result2.Should().Be("你");
    }

    [Fact]
    public void DecodeBytes_MultipleSplitSequences_BuffersCorrectly()
    {
        var decoder = new Utf8StreamDecoder();
        var text = "你好世界";
        var bytes = Encoding.UTF8.GetBytes(text);

        foreach (var b in bytes.Take(bytes.Length - 1))
        {
            var result = decoder.DecodeBytes(new[] { b });
            if (result.Length > 0)
            {
                result.Should().MatchRegex("^[你好世]$");
            }
        }

        var finalResult = decoder.DecodeBytes(new[] { bytes.Last() });
        finalResult.Should().Be("界");
    }

    [Fact]
    [Trait("Category", "EdgeCase")]
    public void DecodeBytes_InvalidUtf8_ProducesReplacementCharacter()
    {
        var decoder = new Utf8StreamDecoder();

        // 0xFF is never valid in UTF-8
        var invalidBytes = new byte[] { 0xFF, 0xFE };
        var result = decoder.DecodeBytes(invalidBytes);

        // Flush to force any buffered invalid bytes to emit
        result += decoder.Flush();

        result.Should().Contain("\uFFFD");
    }

    [Fact]
    [Trait("Category", "EdgeCase")]
    public void Flush_IncompleteSequence_ProducesReplacementCharacter()
    {
        var decoder = new Utf8StreamDecoder();

        // First byte of a 3-byte sequence (0xE4 starts 你)
        var incompleteBytes = new byte[] { 0xE4 };
        var result = decoder.DecodeBytes(incompleteBytes);
        result.Should().BeEmpty();

        var flushed = decoder.Flush();
        flushed.Should().Contain("\uFFFD");
    }
}

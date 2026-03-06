using System.Text;

namespace PulseTerm.Core.Ssh;

public class Utf8StreamDecoder
{
    private readonly List<byte> _buffer = new();
    private readonly Decoder _decoder;

    public Utf8StreamDecoder()
    {
        // Use replacement fallback to emit U+FFFD for invalid byte sequences
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        _decoder = encoding.GetDecoder();
        _decoder.Fallback = new DecoderReplacementFallback("\uFFFD");
    }

    public string DecodeBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        _buffer.AddRange(bytes);

        var charBuffer = new char[_buffer.Count * 2];
        var bytesUsed = 0;
        var charsUsed = 0;
        var completed = false;

        _decoder.Convert(
            _buffer.ToArray(),
            0,
            _buffer.Count,
            charBuffer,
            0,
            charBuffer.Length,
            flush: false,
            out bytesUsed,
            out charsUsed,
            out completed);

        _buffer.RemoveRange(0, bytesUsed);

        return new string(charBuffer, 0, charsUsed);
    }

    /// <summary>
    /// Flush any remaining incomplete bytes as replacement characters (U+FFFD)
    /// </summary>
    public string Flush()
    {
        var allBytes = _buffer.ToArray();
        var charBuffer = new char[Math.Max(allBytes.Length, 1) * 2];

        _decoder.Convert(
            allBytes,
            0,
            allBytes.Length,
            charBuffer,
            0,
            charBuffer.Length,
            flush: true,
            out var bytesUsed,
            out var charsUsed,
            out _);

        _buffer.Clear();

        return new string(charBuffer, 0, charsUsed);
    }

    public void Reset()
    {
        _buffer.Clear();
        _decoder.Reset();
    }
}

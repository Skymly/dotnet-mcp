using System.Text;

namespace DotNetMcp.Server;

internal static class FileTextCodec
{
    public static (string Text, Encoding Encoding) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Decode(bytes);
    }

    public static void Write(string path, string text, Encoding encoding) =>
        File.WriteAllText(path, text, encoding);

    /// <summary>
    /// Decode for a write-back. False when the bytes cannot be re-encoded without replacement,
    /// including a no-BOM file that is not valid UTF-8.
    /// </summary>
    internal static bool TryReadLossless(ReadOnlySpan<byte> bytes, out string text, out Encoding encoding)
    {
        encoding = Detect(bytes);
        var strict = (Encoding)encoding.Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        try
        {
            text = strict.GetString(bytes);
            if (strict.Preamble.Length > 0 && text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text[1..];
            }

            var encoded = strict.GetBytes(text);
            var preamble = strict.GetPreamble();
            if (preamble.Length == 0)
            {
                if (!encoded.AsSpan().SequenceEqual(bytes))
                {
                    text = "";
                    return false;
                }

                return true;
            }

            if (preamble.Length + encoded.Length != bytes.Length)
            {
                text = "";
                return false;
            }

            if (!preamble.AsSpan().SequenceEqual(bytes[..preamble.Length])
                || !encoded.AsSpan().SequenceEqual(bytes[preamble.Length..]))
            {
                text = "";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or EncoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    internal static Encoding Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        var encoding = Detect(bytes);
        var text = encoding.GetString(bytes);
        if (encoding.Preamble.Length > 0 && text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return (text, encoding);
    }
}

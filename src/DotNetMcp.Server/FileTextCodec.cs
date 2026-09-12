using System.Text;

namespace DotNetMcp.Server;

internal static class FileTextCodec
{
    public static (string Text, Encoding Encoding) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = Detect(bytes);
        var text = encoding.GetString(bytes);
        if (encoding.Preamble.Length > 0 && text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return (text, encoding);
    }

    public static void Write(string path, string text, Encoding encoding) =>
        File.WriteAllText(path, text, encoding);

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
}

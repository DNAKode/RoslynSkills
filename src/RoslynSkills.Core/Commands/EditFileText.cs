using System.Text;

namespace RoslynSkills.Core.Commands;

internal sealed record FileTextSnapshot(
    string FilePath,
    string Text,
    Encoding Encoding,
    string LineEnding,
    bool HasCrLf,
    bool HasBareLf);

/// <summary>
/// Shared file reading, encoding/BOM preservation, and line-ending normalization for edit commands.
/// </summary>
internal static class EditFileText
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static async Task<FileTextSnapshot> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        string normalizedPath = Path.GetFullPath(filePath);
        byte[] bytes = await File.ReadAllBytesAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
        (Encoding encoding, int preambleLength) = DetectEncoding(bytes);
        string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        (string lineEnding, bool hasCrLf, bool hasBareLf) = AnalyzeLineEndings(text);
        return new FileTextSnapshot(normalizedPath, text, encoding, lineEnding, hasCrLf, hasBareLf);
    }

    public static async Task WriteAsync(
        string filePath,
        string content,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        string normalizedPath = Path.GetFullPath(filePath);
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(content);
        if (preamble.Length == 0)
        {
            await File.WriteAllBytesAsync(normalizedPath, body, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] output = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, output, preamble.Length, body.Length);
        await File.WriteAllBytesAsync(normalizedPath, output, cancellationToken).ConfigureAwait(false);
    }

    public static string DetectLineEnding(string text, string? fallback = null)
    {
        (string lineEnding, bool hasCrLf, bool hasBareLf) = AnalyzeLineEndings(text);
        if (!hasCrLf && !hasBareLf)
        {
            return fallback ?? "\n";
        }

        return lineEnding;
    }

    public static string NormalizeLineEndings(string text, string targetLineEnding)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string lf = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        return string.Equals(targetLineEnding, "\n", StringComparison.Ordinal)
            ? lf
            : lf.Replace("\n", targetLineEnding, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures a rewritten document preserves the file's original line-ending style when the file
    /// originally used uniform LF or uniform CRLF.
    /// </summary>
    public static string PreserveLineEndings(FileTextSnapshot original, string updatedText)
    {
        if (original.HasBareLf && !original.HasCrLf)
        {
            return NormalizeLineEndings(updatedText, "\n");
        }

        if (original.HasCrLf && !original.HasBareLf)
        {
            return NormalizeLineEndings(updatedText, "\r\n");
        }

        return updatedText;
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 &&
            bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        }

        if (bytes.Length >= 4 &&
            bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Utf8WithBom, 3);
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode, 2);
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode, 2);
        }

        return (Utf8NoBom, 0);
    }

    private static (string LineEnding, bool HasCrLf, bool HasBareLf) AnalyzeLineEndings(string text)
    {
        int crlfCount = 0;
        int bareLfCount = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i > 0 && text[i - 1] == '\r')
                {
                    crlfCount++;
                }
                else
                {
                    bareLfCount++;
                }
            }
        }

        bool hasCrLf = crlfCount > 0;
        bool hasBareLf = bareLfCount > 0;
        string lineEnding = crlfCount > bareLfCount ? "\r\n" : "\n";
        return (lineEnding, hasCrLf, hasBareLf);
    }
}

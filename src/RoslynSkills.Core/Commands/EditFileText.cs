using System.Security.Cryptography;
using System.Text;

namespace RoslynSkills.Core.Commands;

internal sealed record FileTextSnapshot(
    string FilePath,
    string Text,
    Encoding Encoding,
    string LineEnding,
    bool HasCrLf,
    bool HasBareLf,
    string ContentSha256,
    byte[] OriginalBytes);

internal sealed record AtomicFileWrite(
    string FilePath,
    string UpdatedText,
    Encoding Encoding,
    byte[] OriginalBytes,
    string ExpectedOriginalSha256);

internal sealed record AtomicBatchWriteResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    string? FailedFilePath,
    string? ExpectedSha256,
    string? ActualSha256,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> RolledBackPaths,
    IReadOnlyList<string> RollbackErrors);

/// <summary>
/// Shared file reading, encoding/BOM preservation, SHA-256 content hashing, line-ending normalization,
/// and crash-safe atomic temp-file+rename writes for edit commands.
/// </summary>
internal static class EditFileText
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static async Task<FileTextSnapshot> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        string normalizedPath = Path.GetFullPath(filePath);
        byte[] bytes = await File.ReadAllBytesAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
        string sha256 = ComputeSha256(bytes);
        (Encoding encoding, int preambleLength) = DetectEncoding(bytes);
        string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        (string lineEnding, bool hasCrLf, bool hasBareLf) = AnalyzeLineEndings(text);
        return new FileTextSnapshot(normalizedPath, text, encoding, lineEnding, hasCrLf, hasBareLf, sha256, bytes);
    }

    public static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string ComputeEncodedSha256(string content, Encoding encoding)
        => ComputeSha256(EncodeContent(content, encoding));

    public static string? TryComputeFileSha256(string filePath)
    {
        try
        {
            string normalizedPath = Path.GetFullPath(filePath);
            if (!File.Exists(normalizedPath))
            {
                return null;
            }

            return ComputeSha256(File.ReadAllBytes(normalizedPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string NormalizeSha256(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["sha256:".Length..].Trim();
        }

        return trimmed.ToLowerInvariant();
    }

    public static bool IsValidSha256Hex(string normalized)
    {
        if (normalized.Length != 64)
        {
            return false;
        }

        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    public static byte[] EncodeContent(string content, Encoding encoding)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(content);
        if (preamble.Length == 0)
        {
            return body;
        }

        byte[] output = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, output, preamble.Length, body.Length);
        return output;
    }

    /// <summary>
    /// Writes <paramref name="content"/> crash-safely via a sibling temp file and atomic rename/replace.
    /// </summary>
    public static async Task WriteAsync(
        string filePath,
        string content,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        string normalizedPath = Path.GetFullPath(filePath);
        byte[] output = EncodeContent(content, encoding);
        await WriteBytesAtomicAsync(normalizedPath, output, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteBytesAtomicAsync(
        string normalizedPath,
        byte[] output,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = BuildSiblingTempPath(normalizedPath);
        try
        {
            await using (FileStream fs = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 8192, useAsync: true))
            {
                await fs.WriteAsync(output, cancellationToken).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, normalizedPath, overwrite: true);
        }
        finally
        {
            TryDeleteQuietly(tempPath);
        }
    }

    /// <summary>
    /// Two-phase atomic multi-file commit:
    /// 1) Stages every target's new bytes into sibling temp files;
    /// 2) Re-verifies every file's current disk SHA-256 against its read-time hash (eliminating TOCTOU races);
    /// 3) Moves staged temp files over targets, rolling back previously moved files if any move fails.
    /// </summary>
    public static async Task<AtomicBatchWriteResult> WriteAtomicBatchAsync(
        IReadOnlyList<AtomicFileWrite> writes,
        CancellationToken cancellationToken)
    {
        if (writes.Count == 0)
        {
            return new AtomicBatchWriteResult(
                Succeeded: true,
                ErrorCode: null,
                ErrorMessage: null,
                FailedFilePath: null,
                ExpectedSha256: null,
                ActualSha256: null,
                WrittenPaths: Array.Empty<string>(),
                RolledBackPaths: Array.Empty<string>(),
                RollbackErrors: Array.Empty<string>());
        }

        List<(AtomicFileWrite Write, string TempPath)> staged = new(writes.Count);
        List<AtomicFileWrite> committed = new(writes.Count);

        try
        {
            // Phase 1: Stage all temp files in target directories.
            foreach (AtomicFileWrite write in writes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedPath = Path.GetFullPath(write.FilePath);
                byte[] output = EncodeContent(write.UpdatedText, write.Encoding);
                string tempPath = BuildSiblingTempPath(normalizedPath);
                await using (FileStream fs = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 8192, useAsync: true))
                {
                    await fs.WriteAsync(output, cancellationToken).ConfigureAwait(false);
                    await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                staged.Add((write, tempPath));
            }

            // Phase 2: Re-verify disk SHA-256 right before replacing any file to detect concurrent external edits.
            foreach ((AtomicFileWrite write, _) in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedPath = Path.GetFullPath(write.FilePath);
                string? currentDiskSha = TryComputeFileSha256(normalizedPath);
                if (currentDiskSha is null ||
                    !string.Equals(currentDiskSha, write.ExpectedOriginalSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new AtomicBatchWriteResult(
                        Succeeded: false,
                        ErrorCode: "concurrent_modification",
                        ErrorMessage: $"File '{normalizedPath}' was modified on disk during transaction execution before commit (expected sha256 '{write.ExpectedOriginalSha256}', found '{currentDiskSha ?? "<missing>"}'). Aborted before writing any file.",
                        FailedFilePath: normalizedPath,
                        ExpectedSha256: write.ExpectedOriginalSha256,
                        ActualSha256: currentDiskSha,
                        WrittenPaths: Array.Empty<string>(),
                        RolledBackPaths: Array.Empty<string>(),
                        RollbackErrors: Array.Empty<string>());
                }
            }

            // Phase 3: Commit atomic moves, rolling back previously moved files if any move fails.
            foreach ((AtomicFileWrite write, string tempPath) in staged)
            {
                string normalizedPath = Path.GetFullPath(write.FilePath);
                try
                {
                    File.Move(tempPath, normalizedPath, overwrite: true);
                    committed.Add(write);
                }
                catch (Exception ex)
                {
                    List<string> rolledBack = new();
                    List<string> rollbackErrors = new();
                    for (int i = committed.Count - 1; i >= 0; i--)
                    {
                        AtomicFileWrite prior = committed[i];
                        try
                        {
                            await WriteBytesAtomicAsync(Path.GetFullPath(prior.FilePath), prior.OriginalBytes, CancellationToken.None).ConfigureAwait(false);
                            rolledBack.Add(Path.GetFullPath(prior.FilePath));
                        }
                        catch (Exception rbEx)
                        {
                            rollbackErrors.Add($"{prior.FilePath}: {rbEx.Message}");
                        }
                    }

                    return new AtomicBatchWriteResult(
                        Succeeded: false,
                        ErrorCode: "transaction_write_failed",
                        ErrorMessage: $"Failed to commit '{normalizedPath}': {ex.Message}",
                        FailedFilePath: normalizedPath,
                        ExpectedSha256: write.ExpectedOriginalSha256,
                        ActualSha256: null,
                        WrittenPaths: Array.Empty<string>(),
                        RolledBackPaths: rolledBack,
                        RollbackErrors: rollbackErrors);
                }
            }

            return new AtomicBatchWriteResult(
                Succeeded: true,
                ErrorCode: null,
                ErrorMessage: null,
                FailedFilePath: null,
                ExpectedSha256: null,
                ActualSha256: null,
                WrittenPaths: committed.Select(w => Path.GetFullPath(w.FilePath)).ToArray(),
                RolledBackPaths: Array.Empty<string>(),
                RollbackErrors: Array.Empty<string>());
        }
        finally
        {
            foreach ((_, string tempPath) in staged)
            {
                TryDeleteQuietly(tempPath);
            }
        }
    }

    private static string BuildSiblingTempPath(string normalizedPath)
    {
        string directory = Path.GetDirectoryName(normalizedPath) ?? Environment.CurrentDirectory;
        string fileName = Path.GetFileName(normalizedPath);
        return Path.Combine(directory, $".roslynskills-tx-{Environment.ProcessId}-{Guid.NewGuid():N}-{fileName}.tmp");
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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

namespace ClaudeCodeOverview.Core.Ingestion;

public sealed record TailResult(List<byte[]> Lines, long NewOffset, bool Truncated, int OversizedSkipped);

/// <summary>
/// Incremental, offset-based tailer for append-only JSONL files.
/// Only complete '\n'-terminated lines are consumed; a trailing partial line stays for the
/// next pass, so the returned offset is always safe to persist alongside the parsed rows.
/// Files may be held open by live Claude Code sessions (Windows) — hence the share flags.
/// </summary>
public static class FileTailer
{
    public const long MaxLineBytes = 64L * 1024 * 1024;

    public static TailResult ReadNewLines(string path, long offset)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920);

        if (fs.Length < offset)
            return new TailResult([], 0, Truncated: true, 0);

        fs.Seek(offset, SeekOrigin.Begin);

        var lines = new List<byte[]>();
        var current = new MemoryStream();
        var buffer = new byte[81920];
        long absChunkStart = offset;
        long newOffset = offset;
        var oversized = 0;
        var skippingOversized = false;
        int read;

        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;

                if (skippingOversized)
                {
                    skippingOversized = false;
                    oversized++;
                }
                else
                {
                    current.Write(buffer, start, i - start);
                    var line = TrimCr(current);
                    if (line.Length > 0) lines.Add(line);
                }
                current.SetLength(0);
                newOffset = absChunkStart + i + 1;
                start = i + 1;
            }

            if (!skippingOversized)
            {
                current.Write(buffer, start, read - start);
                if (current.Length > MaxLineBytes)
                {
                    current.SetLength(0);
                    skippingOversized = true;
                }
            }
            absChunkStart += read;
        }

        return new TailResult(lines, newOffset, Truncated: false, oversized);
    }

    private static byte[] TrimCr(MemoryStream ms)
    {
        var len = ms.Length;
        var buf = ms.GetBuffer();
        if (len > 0 && buf[len - 1] == (byte)'\r') len--;
        var result = new byte[len];
        Array.Copy(buf, result, len);
        return result;
    }
}

using System.Text;
using ClaudeCodeOverview.Core.Ingestion;

namespace ClaudeCodeOverview.Tests;

public class FileTailerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ccov-tail-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public void Consumes_only_complete_lines_and_leaves_partial_tail()
    {
        File.WriteAllText(_path, "{\"a\":1}\n{\"b\":2}\n{\"partial\":");

        var result = FileTailer.ReadNewLines(_path, 0);

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(result.Lines[0]));
        // Offset stops after the last complete line, before the partial one.
        Assert.Equal(Encoding.UTF8.GetByteCount("{\"a\":1}\n{\"b\":2}\n"), result.NewOffset);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Picks_up_the_completed_line_on_the_next_pass()
    {
        File.WriteAllText(_path, "{\"a\":1}\n{\"partial\":");
        var first = FileTailer.ReadNewLines(_path, 0);
        Assert.Single(first.Lines);

        File.AppendAllText(_path, "true}\n");
        var second = FileTailer.ReadNewLines(_path, first.NewOffset);

        Assert.Single(second.Lines);
        Assert.Equal("{\"partial\":true}", Encoding.UTF8.GetString(second.Lines[0]));
    }

    [Fact]
    public void Trims_carriage_returns()
    {
        File.WriteAllText(_path, "{\"a\":1}\r\n");
        var result = FileTailer.ReadNewLines(_path, 0);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(result.Lines[0]));
    }

    [Fact]
    public void Detects_truncation_when_file_shrinks_below_offset()
    {
        File.WriteAllText(_path, "{\"a\":1}\n");
        var result = FileTailer.ReadNewLines(_path, offset: 999);
        Assert.True(result.Truncated);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Skips_empty_lines_but_advances_offset()
    {
        File.WriteAllText(_path, "{\"a\":1}\n\n{\"b\":2}\n");
        var result = FileTailer.ReadNewLines(_path, 0);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(new FileInfo(_path).Length, result.NewOffset);
    }
}

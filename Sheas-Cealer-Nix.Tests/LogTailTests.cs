using System;
using System.IO;
using System.Text;
using Sheas_Cealer_Nix.Utils;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class LogTailTests : IDisposable
{
    private readonly string TempLog = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (File.Exists(TempLog))
            File.Delete(TempLog);
    }

    [Fact]
    public void ReadTail_FileMissing_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LogTail.ReadTail(TempLog));
    }

    [Fact]
    public void ReadTail_SmallFile_ReturnsWholeContent()
    {
        File.WriteAllText(TempLog, "line1\nline2\n");

        Assert.Equal("line1\nline2\n", LogTail.ReadTail(TempLog));
    }

    [Fact]
    public void ReadTail_LargeFile_TakesTrailingBytesAndDropsPartialLine()
    {
        string filler = new('b', (int)LogTail.MaxTailBytes);

        File.WriteAllText(TempLog, "head\n" + filler + "\nlast line\n");

        string tail = LogTail.ReadTail(TempLog);

        Assert.Equal("last line\n", tail);
    }

    [Fact]
    public void ReadIncrement_Append_ReturnsOnlyNewContent()
    {
        File.WriteAllText(TempLog, "line1\n");
        long lastLength = 0;
        LogTail.ReadIncrement(TempLog, ref lastLength);

        File.AppendAllText(TempLog, "line2\n");

        Assert.Equal("line2\n", LogTail.ReadIncrement(TempLog, ref lastLength));
        Assert.Equal(new FileInfo(TempLog).Length, lastLength);
    }

    [Fact]
    public void ReadIncrement_NoChange_ReturnsNull()
    {
        File.WriteAllText(TempLog, "line1\n");
        long lastLength = 0;
        LogTail.ReadIncrement(TempLog, ref lastLength);

        Assert.Null(LogTail.ReadIncrement(TempLog, ref lastLength));
    }

    [Fact]
    public void ReadIncrement_FileMissing_ReturnsNull()
    {
        long lastLength = 0;

        Assert.Null(LogTail.ReadIncrement(TempLog, ref lastLength));
    }

    [Fact]
    public void ReadIncrement_FileRewritten_ReturnsFreshTail()
    {
        File.WriteAllText(TempLog, "old content that is long enough to matter\n");
        long lastLength = 0;
        LogTail.ReadIncrement(TempLog, ref lastLength);

        File.WriteAllText(TempLog, "new start\n");

        Assert.Equal("new start\n", LogTail.ReadIncrement(TempLog, ref lastLength));
        Assert.Equal(new FileInfo(TempLog).Length, lastLength);
    }

    [Fact]
    public void ReadIncrement_OverrunClampsToTail()
    {
        File.WriteAllText(TempLog, new('a', 100));
        long lastLength = 0;
        LogTail.ReadIncrement(TempLog, ref lastLength);

        File.AppendAllText(TempLog, new('b', (int)LogTail.MaxTailBytes * 2));

        string? chunk = LogTail.ReadIncrement(TempLog, ref lastLength);

        Assert.NotNull(chunk);
        Assert.True(chunk.Length <= LogTail.MaxTailBytes + 2);
        Assert.Equal(new FileInfo(TempLog).Length, lastLength);
    }

    [Fact]
    public void ReadTail_ConcurrentWriter_DoesNotThrow()
    {
        using FileStream stream = new(TempLog, FileMode.Create, FileAccess.Write, FileShare.Read);
        byte[] payload = Encoding.UTF8.GetBytes("shared line\n");

        stream.Write(payload);
        stream.Flush();

        Assert.NotEmpty(LogTail.ReadTail(TempLog));

        stream.Write(payload);
        stream.Flush();

        long lastLength = payload.Length;

        Assert.Equal("shared line\n", LogTail.ReadIncrement(TempLog, ref lastLength));
    }
}

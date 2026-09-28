using Cealing_Agent;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class AgentLogTrimTests : IDisposable
{
    private readonly string TempLog = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (File.Exists(TempLog))
            File.Delete(TempLog);
    }

    [Fact]
    public void TrimIfNeeded_FileMissing_DoesNothing()
    {
        AgentLog.TrimIfNeeded(TempLog, 100);

        Assert.False(File.Exists(TempLog));
    }

    [Fact]
    public void TrimIfNeeded_UnderCap_KeepsFileIntact()
    {
        string content = "line1\nline2\nline3\n";

        File.WriteAllText(TempLog, content);
        AgentLog.TrimIfNeeded(TempLog, 1000);

        Assert.Equal(content, File.ReadAllText(TempLog));
    }

    [Fact]
    public void TrimIfNeeded_OverCap_DiscardsOlderHalfAndKeepsNewestLines()
    {
        StringBuilder content = new();

        for (int i = 0; i < 100; i++)
            content.AppendLine($"line {i:000} of the log");

        File.WriteAllText(TempLog, content.ToString());
        long originalLength = new FileInfo(TempLog).Length;

        AgentLog.TrimIfNeeded(TempLog, originalLength / 2);

        string trimmed = File.ReadAllText(TempLog);
        long trimmedLength = new FileInfo(TempLog).Length;

        Assert.True(trimmedLength <= originalLength / 2 + 200);
        Assert.Contains("older half discarded", trimmed);
        // 截断点之后的内容必须原样保留，最后一行不能丢
        Assert.EndsWith(Environment.NewLine, trimmed);
        Assert.DoesNotContain("line 000", trimmed);
        Assert.Contains("line 099", trimmed);
    }
}

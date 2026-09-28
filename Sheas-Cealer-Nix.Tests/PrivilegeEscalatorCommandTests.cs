using Sheas_Cealer_Nix.Utils;
using System.Linq;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class PrivilegeEscalatorCommandTests
{
    [Fact]
    public void MacLaunchUsesOsascriptWithAdminPrivileges()
    {
        string[] args = [.. PrivilegeEscalator.BuildLaunchArgs("/app/Cealing-Agent", "/tmp/a.sock", "/app/c.json", "/app", "/data", "501", "/app/a.log", "macos")];

        Assert.Equal("osascript", args[0]);
        Assert.Equal("-e", args[1]);
        Assert.Contains("with administrator privileges", args[2]);
        Assert.Contains("/app/Cealing-Agent", args[2]);
    }

    [Fact]
    public void MacLaunchRedirectsAllFileDescriptorsAndBackgrounds()
    {
        string script = PrivilegeEscalator.BuildLaunchArgs("/app/Cealing-Agent", "/tmp/a.sock", "/app/c.json", "/app", "/data", "501", "/app/a.log", "macos")[2];

        // 后台化靠 sh 的 & + fd 重定向；不能用 nohup —— osascript 无控制终端，
        // BSD nohup 会因 ioctl(TIOCNOTTY) 失败而放弃执行命令。
        Assert.Contains("&", script);
        Assert.Contains("2>&1", script);
        Assert.Contains("</dev/null", script);
        Assert.DoesNotContain("nohup", script);
    }

    [Fact]
    public void MacLaunchQuotesPathsContainingSpaces()
    {
        string script = PrivilegeEscalator.BuildLaunchArgs("/Applications/My App/Cealing-Agent", "/tmp/a.sock", "/app/c.json", "/app", "/data", "501", "/app/a.log", "macos")[2];

        // 路径带空格必须整个保留在一个 shell 单引号 token 里，否则会被 sh 拆成两个参数。
        Assert.Contains("'/Applications/My App/Cealing-Agent'", script);
        Assert.Contains("--socket", script);
    }

    [Fact]
    public void MacLaunchEscapesDoubleQuotesForAppleScript()
    {
        string script = PrivilegeEscalator.BuildLaunchArgs("/app/We\"ird", "/tmp/a.sock", "/app/c.json", "/app", "/data", "501", "/app/a.log", "macos")[2];

        Assert.DoesNotContain("do shell script \"do shell", script);
        Assert.Contains("\\\"", script);
    }

    [Fact]
    public void LinuxLaunchUsesPkexecWithBackgroundedShell()
    {
        string[] args = [.. PrivilegeEscalator.BuildLaunchArgs("/app/Cealing-Agent", "/tmp/a.sock", "/app/c.json", "/app", "/data", "1000", "/app/a.log", "linux")];

        Assert.Equal("pkexec", args[0]);
        Assert.Equal("sh", args[1]);
        Assert.Equal("-c", args[2]);
        string script = args[3];
        Assert.Contains("/app/Cealing-Agent", script);
        Assert.Contains("&", script);
        Assert.Contains("2>&1", script);
        Assert.Contains("</dev/null", script);
        Assert.DoesNotContain("nohup", script);
    }

    [Fact]
    public void DirectArgsSkipEscalation()
    {
        string[] args = [.. PrivilegeEscalator.BuildDirectArgs("/app/Cealing-Agent", "/tmp/a.sock", "/app/c.json", "/app", "/data", "0", "/app/a.log")];

        Assert.DoesNotContain("osascript", args);
        Assert.DoesNotContain("pkexec", args);
        string script = args[^1];
        Assert.Contains("/app/Cealing-Agent", script);
        Assert.Contains("&", script);
        Assert.DoesNotContain("nohup", script);
    }

    [Fact]
    public void WindowsArgsAreAgentOptionsOnlyNoShellAtAll()
    {
        // 回归：Windows 曾经走 linux 分支（pkexec sh -c），本机根本没有 pkexec，
        // Process.Start 直接抛「系统找不到指定的文件」，全局伪造点了毫无反应。
        string[] args = [.. PrivilegeEscalator.BuildWindowsArgs(
            @"C:\Users\a\AppData\Local\Temp\cealing-agent.sock",
            @"D:\App\Cealing-Host-U.json",
            @"D:\App",
            @"C:\Users\a\AppData\Roaming\Sheas-Cealer-Nix",
            "0")];

        Assert.Equal(
            [
                "--socket", @"C:\Users\a\AppData\Local\Temp\cealing-agent.sock",
                "--config", @"D:\App\Cealing-Host-U.json",
                "--app-dir", @"D:\App",
                "--data-dir", @"C:\Users\a\AppData\Roaming\Sheas-Cealer-Nix",
                "--owner-uid", "0"
            ],
            args);

        // 反斜杠路径不能进 shell：进了就被当成转义符拆掉
        Assert.DoesNotContain("sh", args);
        Assert.DoesNotContain("-c", args);
        Assert.DoesNotContain("pkexec", args);
    }

    [Fact]
    public void WindowsAgentLaunchKeepsDataDirForUserWritablePaths()
    {
        string args = string.Join(' ', PrivilegeEscalator.BuildWindowsArgs("/tmp/a.sock", "/data/c.json", "/app", "/data", "0"));

        Assert.Contains("--data-dir /data", args);
        Assert.Contains("--app-dir /app", args);
    }

    [Fact]
    public void LaunchForwardsDataDirSoRootAgentUsesUserWritablePath()
    {
        // agent 以 root 运行，不能用 root 的 $HOME 推数据目录；必须由 GUI 显式传 --data-dir，
        // 否则配置/证书会重新落回 app bundle，升级即丢。
        string mac = PrivilegeEscalator.BuildLaunchArgs("/app/Cealing-Agent", "/tmp/a.sock", "/data/c.json", "/app", "/data", "501", "/data/a.log", "macos")[2];
        string linux = PrivilegeEscalator.BuildLaunchArgs("/app/Cealing-Agent", "/tmp/a.sock", "/data/c.json", "/app", "/data", "1000", "/data/a.log", "linux")[3];

        foreach (string script in new[] { mac, linux })
        {
            Assert.Contains("--data-dir '/data'", script);
            // app 目录仍要传：二进制只存在于 bundle 里
            Assert.Contains("--app-dir '/app'", script);
        }
    }
}

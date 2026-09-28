using Cealing_Agent;
using System;
using System.Linq;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class TrustStoreCommandTests
{
    [Fact]
    public void MacInstallUsesSecurityAddTrustedCert()
    {
        string[] args = [.. TrustStore.BuildInstallArgs("/tmp/root.pem", "macos")];

        Assert.Equal("security", args[0]);
        Assert.Equal("add-trusted-cert", args[1]);
        Assert.Contains("-d", args);
        Assert.Contains("trustRoot", args);
        Assert.Contains("/Library/Keychains/System.keychain", args);
        Assert.Equal("/tmp/root.pem", args[^1]);
    }

    [Fact]
    public void MacUninstallUsesSha1Lookup()
    {
        string[] args = [.. TrustStore.BuildUninstallArgs("AABBCC", "macos")];

        Assert.Equal("delete-certificate", args[1]);
        Assert.Equal("AABBCC", args[args.ToList().IndexOf("-Z") + 1]);
    }

    [Fact]
    public void LinuxInstallCopiesAndRefreshes()
    {
        string script = TrustStore.BuildInstallArgs("/tmp/root.pem", "linux")[2];

        Assert.Contains("cp -- '/tmp/root.pem' '/usr/local/share/ca-certificates/cealing-root.crt'", script);
        Assert.Contains("update-ca-certificates", script);
    }

    [Fact]
    public void LinuxUninstallRemovesAndRefreshes()
    {
        string script = TrustStore.BuildUninstallArgs("AABBCC", "linux")[2];

        Assert.Contains("rm -f", script);
        Assert.Contains("--fresh", script);
    }

    [Fact]
    public void ShellQuoteEscapesEmbeddedSingleQuote()
    {
        Assert.Equal("'/tmp/it'\\''s/root.pem'", TrustStore.ShellQuote("/tmp/it's/root.pem"));
    }

    [Fact]
    public void LinuxInstallQuotesPathContainingSingleQuote()
    {
        string script = TrustStore.BuildInstallArgs("/tmp/it's/root.pem", "linux")[2];

        Assert.Contains("'/tmp/it'\\''s/root.pem'", script);
        Assert.DoesNotContain(" /tmp/it's ", script);
    }

    // 回归：certutil 不认 "LocalMachine\Root" 这种 PowerShell 存储路径写法，会把它当成文件路径
    // 存储去打开网络路径，报 0x80070035 ERROR_BAD_NETPATH（只读的 `certutil -store "LocalMachine\Root"`
    // 同样复现，而 `certutil -store Root` 正常列出机器级根证书）。certutil 默认就是机器级上下文。
    [Fact]
    public void WindowsInstallUsesBareRootStoreThatCertutilUnderstands()
    {
        string[] install = [.. TrustStore.BuildInstallArgs(@"C:\data\Cealing-Root.pem", "windows")];
        string[] uninstall = [.. TrustStore.BuildUninstallArgs("AABBCC", "windows")];

        Assert.Equal("certutil", install[0]);
        Assert.Equal("-addstore", install[1]);
        Assert.Equal("Root", install[2]);
        Assert.DoesNotContain('\\', install[2]);
        Assert.Equal(@"C:\data\Cealing-Root.pem", install[^1]);

        // 装和删必须落在同一个存储，否则根证书会永久残留在系统信任库里
        Assert.Equal(install[2], uninstall[2]);
    }

    [Fact]
    public void WindowsUninstallDeletesByThumbprintNotLinuxCaCertificates()
    {
        // 回归：卸载曾经把 Windows 当 linux，去跑 sh / update-ca-certificates，
        // 结果 LocalMachine\Root 里的根证书永远删不掉。
        string[] args = [.. TrustStore.BuildUninstallArgs("AABBCC", "windows")];

        Assert.Equal("certutil", args[0]);
        Assert.Equal("-delstore", args[1]);
        Assert.Equal("AABBCC", args[^1]);
    }

    [Fact]
    public void AgentPlatformNameIsWindowsNotMistakenForLinux()
    {
        string expected = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

        Assert.Equal(expected, EngineSupervisor.Platform);
    }

    [Fact]
    public void UnknownPlatformThrows()
    {
        Assert.Throws<PlatformNotSupportedException>(() => TrustStore.BuildInstallArgs("/tmp/root.pem", "freebsd"));
    }
}

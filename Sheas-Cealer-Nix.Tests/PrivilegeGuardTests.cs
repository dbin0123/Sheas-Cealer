using Cealing_Core;
using System;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

/// <summary>
/// Windows 上「全局伪造点了没反应」的一条真实链路：GUI 用 Verb=RunAs 拉 agent，
/// 用户没批 UAC（或被安全软件拦下）时 agent 仍然以普通令牌跑起来了，
/// 于是它写 hosts 得到 Access to the path ... is denied，
/// 而界面上只剩一句读不懂的英文堆栈。缺的是「agent 自己有没有提权」这个事实。
/// </summary>
public class PrivilegeGuardTests
{
    [Fact]
    public void ElevatedAgentNeedsNoWarning()
    {
        Assert.Null(Privilege.RequireElevated(true, "windows"));
        Assert.Null(Privilege.RequireElevated(true, "linux"));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    [InlineData("linux")]
    public void NonElevatedAgentExplainsWhatIsMissing(string platform)
    {
        string? denied = Privilege.RequireElevated(false, platform);

        Assert.NotNull(denied);
        // 必须点名「管理员权限」，否则用户只会看到 Access is denied
        Assert.Contains("管理员", denied);
        // 必须给出下一步动作（重新授权），而不是只报现状
        Assert.Contains("提权", denied);
        Assert.Contains(platform, denied);
    }

    [Fact]
    public void IsElevatedIsQueryableOnEveryPlatform()
    {
        // 只要求「不抛」：具体值取决于跑测试的进程令牌，测试里不能对它做断言
        bool elevated = Privilege.IsElevated;

        Assert.True(elevated || !elevated);
    }
}

using Cealing_Core;
using Cealing_Core.Protocol;
using Sheas_Cealer_Nix.Consts;
using Sheas_Cealer_Nix.Utils;
using System;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

/// <summary>
/// 「能不能复用这个 agent」的判定逻辑。这里是 v1.0.15 仍然复用了僵尸 agent 的地方：
/// 当时只看 AppDir，而僵尸 agent 是 v1.0.13、根本不上报任何身份字段，
/// 于是被当成「无法判断身份」而沿用旧行为直接复用，
/// 它随后用自己捕获的 --config（还指向 app bundle）去找文件，报
/// 「config not found: &lt;bundle 内路径&gt;」。
/// </summary>
public class ProxyControllerIdentityTests
{
    // 判定必须返回 null（可复用）
    private static AgentStatus Compatible(string? appDir = null, string? configPath = null) => new()
    {
        Running = false,
        Engine = "none",
        Pid = 12345,
        AppDir = appDir ?? MainConst.AppDir,
        ConfigPath = configPath ?? MainConst.ProxyConfigPath,
        ProtocolVersion = AgentProtocol.Version,
        // 复用前提之一是它真的有特权：普通令牌下的 agent 写不动 hosts，
        // 复用它只会让全局伪造静默失效。
        Elevated = true
    };

    [Fact]
    public void CurrentAgentIsReusable()
    {
        Assert.Null(ProxyController.DescribeMismatch(Compatible()));
    }

    // 判定必须非 null（退役）
    [Fact]
    public void LegacyAgentThatReportsNothingMustBeRetired()
    {
        // v1.0.13 的 agent：JSON 里只有 running/engine/coproxy/port/ruleCount/pid
        // —— 没有 appDir、configPath、protocolVersion。
        AgentStatus? legacy = AgentJson.Deserialize<AgentStatus>(
            """{"running":false,"engine":"none","coproxy":false,"httpPort":0,"httpsPort":0,"ruleCount":0,"pid":34650}""");

        string? reason = ProxyController.DescribeMismatch(legacy);

        Assert.NotNull(reason);
        // 老版本被当成协议过旧，而不是被放行
        Assert.Contains("协议版本过旧", reason);
    }

    [Fact]
    public void NullStatusMustBeRetired()
    {
        Assert.NotNull(ProxyController.DescribeMismatch(null));
    }

    // 原地升级的关键场景：app 目录完全没变，但 --config 从 bundle 挪到了数据目录。
    // 只看 AppDir 会误判成「同一个 agent」，于是继续用旧 --config。
    [Fact]
    public void SameAppDirWithOldConfigPathMustBeRetired()
    {
        AgentStatus samePathOldAgent = Compatible(
            appDir: MainConst.AppDir,
            configPath: System.IO.Path.Combine(MainConst.AppDir, "cealing-proxy.json"));

        string? reason = ProxyController.DescribeMismatch(samePathOldAgent);

        Assert.NotNull(reason);
        Assert.Contains("配置路径不一致", reason);
    }

    [Fact]
    public void NewerProtocolIsAcceptable()
    {
        AgentStatus future = Compatible();
        future.ProtocolVersion = AgentProtocol.Version + 1;

        Assert.Null(ProxyController.DescribeMismatch(future));
    }

    [Fact]
    public void TrailingSlashDifferenceIsNotATransition()
    {
        // agent 的 --app-dir 带尾部斜杠（PrivilegeEscalator 历史上就传过这种），不能当成不同 agent
        AgentStatus withSlash = Compatible(appDir: MainConst.AppDir + "/");

        Assert.Null(ProxyController.DescribeMismatch(withSlash));
    }

    [Fact]
    public void ConfigPathNormalizationHandlesTrailingSlashToo()
    {
        AgentStatus status = Compatible(configPath: MainConst.ProxyConfigPath + "/");

        Assert.Null(ProxyController.DescribeMismatch(status));
    }

    [Fact]
    public void AppDirChangeIsReported()
    {
        AgentStatus moved = Compatible(appDir: "/some/other/location");

        string? reason = ProxyController.DescribeMismatch(moved);

        Assert.NotNull(reason);
        Assert.Contains("应用目录不一致", reason);
    }

    [Fact]
    public void NonElevatedAgentMustBeRetired()
    {
        // UAC 没批、或 agent 是被别的方式拉起来的：身份字段全对，但它没有特权。
        // 复用它 = 全局伪造写 hosts 必挂，而且挂得毫无提示。
        AgentStatus plainToken = Compatible();
        plainToken.Elevated = false;

        string? reason = ProxyController.DescribeMismatch(plainToken);

        Assert.NotNull(reason);
        Assert.Contains("未提权", reason);
    }
}

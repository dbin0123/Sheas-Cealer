using Cealing_Agent;
using Cealing_Core.Protocol;
using Sheas_Cealer_Nix.Utils;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

// GUI 侧的租约实现。这里守住的是「认领之后连接必须一直开着」这一条：
// AgentClient.SendAsync 每次都 using 掉 socket，HoldAsync 若照抄那个写法，
// agent 会在认领成功的同一刻看到 EOF 并自杀 —— 症状是「一启用代理，过 60 秒全网断掉」。
public sealed class AgentClientLeaseTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _socket = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".sock");
    private readonly string _config = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_socket))
            File.Delete(_socket);

        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task HeldLeaseKeepsAgentAlivePastItsOwnDeadline()
    {
        // 故意把「无人认领就自杀」的超时设得比观察窗口短：只要租约连接被 hold 住了，
        // agent 就不能退出；反过来，连接要是被 HoldAsync 提前 dispose，这里必然看到退出。
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(
            _socket, _config, _dir, ownerLeaseDeadline: TimeSpan.FromSeconds(1));

        AgentClient client = new(_socket);

        AgentResponse response = await client.HoldAsync();

        Assert.True(response.Ok);
        Assert.Equal(AgentProtocol.Version, response.Status!.ProtocolVersion);

        await Task.Delay(2500);

        // 必须显式钉住 client：租约 socket 挂在它的 _lease 字段上，而这段等待里再没有引用它，
        // GC 一旦回收就把 socket 关掉，agent 读到 EOF 正常退出——全量跑（有内存压力）时
        // 这条断言就会红，单独跑永远绿。生产里 ProxyController 的 Client 是常驻的，不受影响。
        GC.KeepAlive(client);

        Assert.False(fixture.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ReclaimingFromTheSameGuiDoesNotRetireTheAgent()
    {
        // 界面上每次点「启用/重载」都会再走一遍 EnsureAgentAsync，所以重复认领必须是安全的：
        // 新连接先被 agent 确认接管，旧连接才关掉，中间不能出现「没有主人」的瞬间。
        // 这里要连发三次 hold，超时同样得留出调度余量，理由同上一个测试。
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(
            _socket, _config, _dir, ownerLeaseDeadline: TimeSpan.FromSeconds(1));

        AgentClient client = new(_socket);

        await client.HoldAsync();
        await client.HoldAsync();
        await client.HoldAsync();

        await Task.Delay(2500);

        GC.KeepAlive(client);

        Assert.False(fixture.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task HoldingADeadAgentFailsInsteadOfHanging()
    {
        // 没有 agent 时（socket 文件不存在）必须立刻失败，让上层走「重新拉起」分支；
        // 挂住的话 GUI 的启用按钮会一直转圈。
        AgentClient client = new(_socket);

        await Assert.ThrowsAnyAsync<Exception>(() => client.HoldAsync());
    }
}

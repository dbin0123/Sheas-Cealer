using Cealing_Core;
using Cealing_Core.Protocol;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

// 主人存活租约（hold）是「GUI 没了 agent 必须跟着没」的唯一保障：
// 崩溃、被任务管理器杀掉、注销、断电都不会走到 shutdown 命令，
// 但只要那条长连接断了，agent 就必须自己拆引擎、还原 hosts、拆根证书然后退出。
public sealed class AgentServerLeaseTests : IAsyncLifetime, IDisposable
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
        Directory.Delete(_dir, recursive: true);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (File.Exists(_socket))
            File.Delete(_socket);
    }

    [Fact]
    public async Task LeaseLossRetiresAgent()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        Socket lease = await HoldAsync();

        Assert.False(fixture.ShutdownToken.IsCancellationRequested);

        lease.Dispose();

        Assert.True(await WaitUntilAsync(() => fixture.ShutdownToken.IsCancellationRequested, TimeSpan.FromSeconds(3)),
            "GUI 的连接一断，agent 必须自己走退出流程");
    }

    [Fact]
    public async Task ReplacedLeaseDoesNotRetireAgent()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        Socket oldLease = await HoldAsync();
        Socket newLease = await HoldAsync();

        // 新 GUI 接管后，旧连接断开属于正常交接，不能把 agent 拆了
        oldLease.Dispose();

        Assert.False(await WaitUntilAsync(() => fixture.ShutdownToken.IsCancellationRequested, TimeSpan.FromMilliseconds(500)));

        newLease.Dispose();

        Assert.True(await WaitUntilAsync(() => fixture.ShutdownToken.IsCancellationRequested, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task NeverClaimedAgentRetiresItself()
    {
        // GUI 拉起 agent 后崩在发 hold 之前 —— 没有租约兜底的话，这就是一个永久驻留的 root 进程。
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(
            _socket, _config, _dir, ownerLeaseDeadline: TimeSpan.FromMilliseconds(400));

        Assert.True(await WaitUntilAsync(() => fixture.ShutdownToken.IsCancellationRequested, TimeSpan.FromSeconds(3)),
            "到点没人认领就必须自杀，不能挂着特权等");
    }

    [Fact]
    public async Task HoldReportsIdentitySoGuiCanVerifyItOwnsTheRightAgent()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        (Socket lease, AgentResponse response) = await HoldWithResponseAsync();

        Assert.True(response.Ok);
        Assert.Equal(AgentProtocol.Version, response.Status!.ProtocolVersion);

        lease.Dispose();
    }

    private static async Task<AgentResponse> ReadHoldResponseAsync(Socket lease)
    {
        using NetworkStream stream = new(lease, ownsSocket: false);
        using StreamReader reader = new(stream, Encoding.UTF8, false, 4096, leaveOpen: true);

        return AgentJson.Deserialize<AgentResponse>((await reader.ReadLineAsync())!)!;
    }

    // 必须读到回应才算接管成功：agent 是「先认领、再回应」，所以收到回应就意味着租约已经在它手里换人了。
    // 不等回应就返回的话，测试会在 agent 还没认领新连接时拆掉旧连接，看到的就是「主人没了」。
    private async Task<(Socket Lease, AgentResponse Response)> HoldWithResponseAsync()
    {
        Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        await client.ConnectAsync(new UnixDomainSocketEndPoint(_socket));

        NetworkStream stream = new(client, ownsSocket: false);
        StreamWriter writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(AgentJson.Serialize(new AgentRequest { Command = AgentCommand.Hold }));

        return (client, await ReadHoldResponseAsync(client));
    }

    private async Task<Socket> HoldAsync() => (await HoldWithResponseAsync()).Lease;

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        for (int i = 0; i < (int)(timeout.TotalMilliseconds / 50); i++)
        {
            if (condition())
                return true;

            await Task.Delay(50);
        }

        return condition();
    }
}

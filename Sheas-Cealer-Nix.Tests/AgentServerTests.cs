using Cealing_Agent;
using Cealing_Core;
using Cealing_Core.Protocol;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class AgentServerTests : IAsyncLifetime
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
        foreach (string path in new[] { _socket, _config })
            if (File.Exists(path))
                File.Delete(path);

        Directory.Delete(_dir, recursive: true);

        return Task.CompletedTask;
    }

    private async Task<AgentResponse> SendAsync(AgentRequest request)
    {
        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(_socket));

        await using NetworkStream stream = new(client);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };

        await writer.WriteLineAsync(AgentJson.Serialize(request));

        string? line = await reader.ReadLineAsync();

        return AgentJson.Deserialize<AgentResponse>(line!)!;
    }

    [Fact]
    public async Task PingReportsNotRunning()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        AgentResponse response = await SendAsync(new AgentRequest { Command = AgentCommand.Ping });

        Assert.True(response.Ok);
        AgentStatus? status = response.Status;

        Assert.NotNull(status);
        Assert.False(status.Running);
    }

    [Fact]
    public async Task StatusReportsAppDirSoGuiCanDetectStaleAgents()
    {
        // 僵尸 agent 复用是「config not found: <新路径>」这类自相矛盾报错的根源：
        // 旧 root agent 捕获的是已删除 bundle 里的路径。GUI 靠 AppDir 字段识别并换掉它。
        string dataDir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(dataDir);

        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir, dataDir);

        AgentResponse response = await SendAsync(new AgentRequest { Command = AgentCommand.Ping });

        Assert.True(response.Ok);
        Assert.NotNull(response.Status);

        // AppDir 是 bundle（放二进制），不是 dataDir（放配置/证书）
        Assert.Equal(AppPaths.Normalize(_dir), AppPaths.Normalize(response.Status.AppDir));
        Assert.NotEqual(AppPaths.Normalize(dataDir), AppPaths.Normalize(response.Status.AppDir));
    }

    [Fact]
    public async Task StatusReportsConfigPathAndProtocolVersionForHandshake()
    {
        // 原地升级时 AppDir 不会变，只有 --config 会变（v1.0.14 把配置挪出 bundle）。
        // GUI 复用 agent 前要比对这两个字段，否则会把旧版本 agent 当成「同一个」继续用，
        // 它随后用自己的旧 --config 去找文件，报
        // 「config not found: <bundle 内路径>」——而 GUI 明明已把配置写到新位置。
        string dataDir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(dataDir);

        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir, dataDir);

        AgentResponse response = await SendAsync(new AgentRequest { Command = AgentCommand.Ping });

        Assert.NotNull(response.Status);
        Assert.Equal(AgentProtocol.Version, response.Status.ProtocolVersion);
        Assert.Equal(AppPaths.Normalize(_config), AppPaths.Normalize(response.Status.ConfigPath));
    }

    [Fact]
    public async Task StatusReportsOwnElevationState()
    {
        // 「UAC 没批」和「agent 压根没起来」以前在界面上长得一模一样。
        // agent 必须自报特权状态，GUI 才能把它区分开。
        // 值取决于跑测试的进程令牌，所以这里断言「与自身一致」而不是某个固定值。
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        AgentResponse response = await SendAsync(new AgentRequest { Command = AgentCommand.Ping });

        Assert.NotNull(response.Status);
        Assert.Equal(Privilege.IsElevated, response.Status.Elevated);
    }

    // 老版本 agent 的 JSON 里没有 ConfigPath / ProtocolVersion 字段，
    // 反序列化后分别是 "" 和 0 —— GUI 必须把这当成「不可复用」。
    [Fact]
    public void LegacyAgentStatusDeserializesAsIncompatible()
    {
        AgentStatus? status = AgentJson.Deserialize<AgentStatus>(
            """{"running":false,"engine":"none","coproxy":false,"httpPort":0,"httpsPort":0,"ruleCount":0,"pid":34650}""");

        Assert.NotNull(status);
        Assert.Equal(0, status.ProtocolVersion);
        Assert.True(string.IsNullOrEmpty(status.ConfigPath));
    }

    [Fact]
    public void DataDirDiffersFromAppDirNormalizesConsistently()
    {
        // 路径末尾斜杠差异不能被当成「不同 agent」
        Assert.Equal(
            AppPaths.Normalize(Path.Combine(_dir, "data")),
            AppPaths.Normalize(Path.Combine(_dir, "data") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task UnknownCommandIsRejected()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        AgentResponse response = await SendAsync(new AgentRequest { Command = "nope" });

        Assert.False(response.Ok);
        Assert.Equal("unknown command: nope", response.Error);
    }

    [Fact]
    public async Task MalformedJsonIsRejected()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(_socket));

        await using NetworkStream stream = new(client);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };

        await writer.WriteLineAsync("{not json");

        AgentResponse? response = AgentJson.Deserialize<AgentResponse>((await reader.ReadLineAsync())!);

        Assert.NotNull(response);
        Assert.False(response.Ok);
        Assert.Equal("malformed request", response.Error);
    }

    [Fact]
    public async Task StartWithoutConfigFileIsRejected()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        AgentResponse response = await SendAsync(new AgentRequest { Command = AgentCommand.Start });

        Assert.False(response.Ok);
        Assert.Contains("config not found", response.Error);
    }

    [Fact]
    public async Task SocketIsOwnerOnly()
    {
        // Unix 权限位是 Unix 独有的概念：Windows 上 agent 明确跳过 SetUnixFileMode（见 AgentServer.cs），
        // 强行断言只会得到 PlatformNotSupportedException。
        if (!OperatingSystem.IsWindows())
        {
            await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

            UnixFileMode mode = File.GetUnixFileMode(_socket);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }
}

internal sealed class AgentServerFixture : IAsyncDisposable
{
    private readonly AgentServer _server;

    private AgentServerFixture(AgentServer server) => _server = server;

    internal CancellationToken ShutdownToken => _server.ShutdownToken;

    public static async Task<AgentServerFixture> StartAsync(string socketPath, string configPath, string appDir, string? dataDir = null, TimeSpan? ownerLeaseDeadline = null)
    {
        EngineSupervisor supervisor = new(appDir, dataDir ?? appDir, 0, Path.Combine(appDir, "hosts-test"));

        // ownerUid 在 v1 只写日志不参与鉴权，测试里传 0 即可（不要用 Environment.Getuid，net8.0 无此 API）
        AgentServer server = new(socketPath, 0, supervisor, configPath, ownerLeaseDeadline);

        server.Start();

        // 等 socket 真正可连
        for (int i = 0; i < 50; i++)
            try
            {
                using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await probe.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));

                return new AgentServerFixture(server);
            }
            catch (SocketException)
            {
                await Task.Delay(20);
            }

        throw new TimeoutException("agent server socket never became connectable");
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}

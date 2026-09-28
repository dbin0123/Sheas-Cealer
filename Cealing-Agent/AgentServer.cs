using Cealing_Core;
using Cealing_Core.Protocol;
using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent;

internal sealed class AgentServer(string socketPath, int ownerUid, EngineSupervisor supervisor, string configPath, TimeSpan? ownerLeaseDeadline = null) : IAsyncDisposable
{
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _shutdown = new();

    // 退出只能发生一次：租约丢失、60 秒无人认领、shutdown 命令、信号处理这几条路径可能同时到达，
    // 而 supervisor.StopAsync() 会拆引擎、删 hosts 块、卸载根证书 —— 跑两遍既慢又会在日志里
    // 留下一堆「删除失败」的假象。
    private readonly object _retireGate = new();
    private Task? _retire;

    // GUI 主进程持有的那条长连接。null = 还没有主人认领。
    private Socket? _ownerLease;
    private bool _leaseEverAcquired;
    private readonly object _leaseLock = new();

    // 默认 60 秒：agent 是被 GUI 拉起来的，GUI 起来后立刻发 hold；
    // 超过这个时间还没人认领，说明拉它的那个 GUI 已经没了（崩在两步之间 / 被人手动起 agent），
    // 不能让它一直挂着 root 权限 + hosts + 80·443。
    private readonly TimeSpan _ownerLeaseDeadline = ownerLeaseDeadline ?? TimeSpan.FromSeconds(60);

    // Program 靠这个 token 等待 shutdown 命令，避免自己造一个永不返回的 Delay
    internal CancellationToken ShutdownToken => _shutdown.Token;

    internal void Start()
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));

        // Bind 只是建了 socket 文件，必须 Listen 才算进入监听状态，否则客户端 Connect 直接 connection refused。
        _listener.Listen(512);

        // socket 由 root 进程创建。GUI 是普通用户，而 connect() 需要对 socket 文件有写权限，
        // 所以 root:root 0600 的 socket 普通用户连不上（EACCES）—— 表现就是 agent 日志里明明
        // 写着 listening，GUI 却一直超时。把属主改成发起授权的那个 uid，保持 0600：
        // 只有该用户能连，其他本地用户仍然连不上。
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            OwnedFile.HandOver(socketPath, ownerUid);
        }

        AgentLog.Info($"listening on {socketPath} (owner uid {ownerUid})");

        // uid 未知时 chown 被跳过，socket 留在 root 手里，普通用户的 GUI 就永远连不上。
        // 这里必须说清楚，否则现象只是「GUI 一直报超时」，谁都猜不到原因。
        if (ownerUid < 0 && !OperatingSystem.IsWindows())
            AgentLog.Error("owner uid unknown (-1): the socket stays root-owned and the GUI will not be able to connect");

        _ = Task.Run(AcceptLoopAsync);
        _ = Task.Run(EnforceOwnerLeaseDeadlineAsync);
    }

    // 没人认领就退场。只在「从未有过租约」时生效：一旦有主人，主人断开由租约那条路径处理。
    private async Task EnforceOwnerLeaseDeadlineAsync()
    {
        try
        {
            await Task.Delay(_ownerLeaseDeadline, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_leaseLock)
        {
            if (_leaseEverAcquired)
                return;
        }

        AgentLog.Warn($"no owner lease within {_ownerLeaseDeadline.TotalSeconds:0}s; assuming the launching GUI is gone and shutting down");

        await RetireOnceAsync("no owner lease").ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
            try
            {
                Socket connection = await _listener.AcceptAsync(_shutdown.Token).ConfigureAwait(false);

                _ = Task.Run(() => HandleAsync(connection));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"accept failed: {ex.Message}");
            }
    }

    private async Task HandleAsync(Socket connection)
    {
        try
        {
            using (connection)
            using (NetworkStream stream = new(connection, ownsSocket: false))
            using (StreamReader reader = new(stream, Encoding.UTF8, false, 4096, leaveOpen: true))
            using (StreamWriter writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);

                if (line is null)
                    return;

                AgentRequest? request = AgentJson.Deserialize<AgentRequest>(line);

                if (request is null)
                {
                    await WriteAsync(writer, new AgentResponse { Ok = false, Error = "malformed request" }).ConfigureAwait(false);
                    return;
                }

                if (request.Command == AgentCommand.Hold)
                {
                    // 先认领、再回应：反过来的话「新 GUI 接管」和「旧连接断开」的先后顺序就不确定了，
                    // 旧主人先断开会让 agent 误判成「主人没了」而自杀。
                    Socket? superseded = ClaimOwnerLease(connection);

                    if (superseded is not null)
                    {
                        AgentLog.Warn("owner lease replaced by a newer GUI");

                        try { superseded.Dispose(); }
                        catch { }
                    }

                    await WriteAsync(writer, Ok()).ConfigureAwait(false);

                    await AwaitOwnerLeaseEndAsync(connection, stream).ConfigureAwait(false);

                    return;
                }

                AgentResponse response = await DispatchAsync(request).ConfigureAwait(false);

                await WriteAsync(writer, response).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"connection failed: {ex.Message}");
        }
    }

    // 登记租约，返回被顶替掉的旧连接（没有则 null）。
    private Socket? ClaimOwnerLease(Socket connection)
    {
        lock (_leaseLock)
        {
            Socket? superseded = _ownerLease;

            _ownerLease = connection;
            _leaseEverAcquired = true;

            return superseded is not null && !ReferenceEquals(superseded, connection) ? superseded : null;
        }
    }

    // 读这条长连接直到 EOF。对端进程退出（正常退出、崩溃、被 kill、注销）时内核关闭 fd，
    // 这里立刻读到 0 字节 —— 不需要心跳，也不受睡眠/卡顿影响。
    private async Task AwaitOwnerLeaseEndAsync(Socket connection, NetworkStream stream)
    {
        AgentLog.Info("owner lease held");

        byte[] single = new byte[1];

        try
        {
            while (await stream.ReadAsync(single, _shutdown.Token).ConfigureAwait(false) > 0 && !_shutdown.IsCancellationRequested)
            {
                // 主人不会在这条连接上发别的命令；持续读只为了等 EOF。
            }
        }
        catch (Exception ex)
        {
            // 被新 GUI 顶替时旧 socket 是被 Dispose 的，这里的异常不代表主人没了
            AgentLog.Info($"owner lease read ended: {ex.GetType().Name}");
        }

        bool isCurrentOwner;

        lock (_leaseLock)
        {
            isCurrentOwner = ReferenceEquals(_ownerLease, connection);

            if (isCurrentOwner)
                _ownerLease = null;
        }

        if (!isCurrentOwner || _shutdown.IsCancellationRequested)
            return;

        AgentLog.Warn("owner lease dropped (GUI gone); shutting down");

        await RetireOnceAsync("owner gone").ConfigureAwait(false);
    }

    // 退出前必须把世界还原成启动前的样子：停引擎（含 mihomo/nginx 子进程）、还原 hosts、拆根证书。
    // 多条路径（租约丢失 / 无人认领超时 / shutdown 命令 / 进程信号）都想退出。
    //
    // 真正跑清理的只有一次，但**其他调用方必须等它跑完**，不能直接返回：
    // 信号处理器一旦返回就会 Environment.Exit，把还在删证书、还原 hosts 的那半截清理掐死，
    // 于是「杀 agent」这条最普通的操作反而留下脏系统。
    internal async Task RetireOnceAsync(string reason)
    {
        bool first;
        Task cleanup;

        lock (_retireGate)
        {
            first = _retire is null;

            _retire ??= Task.Run(() => RetireCoreAsync(reason));

            cleanup = _retire;
        }

        if (!first)
            AgentLog.Info($"exit request ({reason}) joined the cleanup already in progress");

        await cleanup.ConfigureAwait(false);
    }

    private async Task RetireCoreAsync(string reason)
    {
        try
        {
            await supervisor.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Error($"cleanup before exit ({reason}) failed: {ex}");
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
    }

    // 每次回应都带上身份字段。GUI 靠这些判断「对面是不是我这一版拉起来的 agent」：
    // 只看 AppDir 不够 —— 应用原地升级时 AppDir 完全不变，只有 --config / 协议版本会变。
    private AgentResponse Ok()
    {
        AgentStatus status = supervisor.Status;

        status.ConfigPath = configPath;
        status.ProtocolVersion = AgentProtocol.Version;

        return new AgentResponse { Ok = true, Status = status };
    }

    private async Task<AgentResponse> DispatchAsync(AgentRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case AgentCommand.Ping:
                    return Ok();

                case AgentCommand.Status:
                    return Ok();

                case AgentCommand.Start:
                case AgentCommand.Reload:
                {
                    if (!File.Exists(configPath))
                        return new AgentResponse { Ok = false, Error = $"config not found: {configPath}" };

                    ProxyConfig? config = AgentJson.Deserialize<ProxyConfig>(await File.ReadAllTextAsync(configPath).ConfigureAwait(false));

                    if (config is null)
                        return new AgentResponse { Ok = false, Error = "malformed config" };

                    await supervisor.StartAsync(config, _shutdown.Token).ConfigureAwait(false);

                    return Ok();
                }

                case AgentCommand.Stop:
                case AgentCommand.Cleanup:
                    await supervisor.StopAsync().ConfigureAwait(false);

                    return Ok();

                case AgentCommand.Shutdown:
                    await RetireOnceAsync("shutdown command").ConfigureAwait(false);

                    return Ok();

                default:
                    return new AgentResponse { Ok = false, Error = $"unknown command: {request.Command}" };
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error($"{request.Command} failed: {ex}");

            return new AgentResponse { Ok = false, Error = ex.Message };
        }
    }

    private static Task WriteAsync(StreamWriter writer, AgentResponse response) => writer.WriteLineAsync(AgentJson.Serialize(response));

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();

        try
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
        catch { }
    }
}

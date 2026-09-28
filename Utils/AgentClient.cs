using Cealing_Core;
using Cealing_Core.Protocol;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Utils;

internal sealed class AgentClient(string socketPath)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // 主人存活租约（hold）那条长连接。见 HoldAsync()。
    private OwnerLease? _lease;
    private readonly object _leaseLock = new();

    internal string SocketPath { get; } = socketPath;

    internal bool IsAlive => File.Exists(SocketPath);

    /// <summary>
    /// 认领 agent 的主人租约：连接建立后**故意不关**，一直挂到本进程退出。
    /// agent 靠这条连接的 EOF 判断 GUI 是否还活着 —— 崩溃、被任务管理器杀掉、注销都走不到
    /// shutdown 命令，但进程一死内核必然关 fd，agent 于是自己停引擎、还原 hosts、拆根证书再退出。
    /// 所以这里绝不能像 SendAsync 那样用 using：那条连接一被 dispose，agent 立刻自杀。
    ///
    /// 重复调用是安全的：agent「先认领、再回应」，新连接被确认之后才关掉旧连接，
    /// 所以 agent 眼里永远是「先有主人、后旧主人消失」，不会误判成没人了。
    /// 这也顺带覆盖了「agent 被重启」的情形 —— 旧租约此时已经死了，重新认领即可。
    /// </summary>
    internal async Task<AgentResponse> HoldAsync()
    {
        Socket connection = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            using CancellationTokenSource cts = new(DefaultTimeout);

            await connection.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cts.Token).ConfigureAwait(false);

            StreamWriter writer = new(new NetworkStream(connection, ownsSocket: false), new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(AgentJson.Serialize(new AgentRequest { Command = AgentCommand.Hold })).ConfigureAwait(false);

            StreamReader reader = new(new NetworkStream(connection, ownsSocket: false), Encoding.UTF8, false, 4096, leaveOpen: true);

            string? line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);

            if (line is null)
                throw new IOException("agent closed the hold connection");

            AgentResponse response = AgentJson.Deserialize<AgentResponse>(line)
                ?? throw new IOException("malformed hold response");

            if (!response.Ok)
                throw new IOException($"agent rejected the hold: {response.Error}");

            OwnerLease? previous;

            lock (_leaseLock)
            {
                previous = _lease;

                _lease = new OwnerLease(connection, writer, reader);
            }

            // 确认接管成功后才拆旧连接
            previous?.Dispose();

            return response;
        }
        catch
        {
            connection.Dispose();

            throw;
        }
    }

    private sealed class OwnerLease(Socket socket, StreamWriter writer, StreamReader reader) : IDisposable
    {
        // Socket 有终结器会关 fd，这三个对象必须互相引用、并且被 AgentClient 的字段钉住；
        // AgentClient 本身挂在 ProxyController 上，后者是主窗口的字段，活到进程结束。
        private readonly StreamWriter _writer = writer;
        private readonly StreamReader _reader = reader;

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            socket.Dispose();
        }
    }

    internal async Task<AgentResponse> SendAsync(AgentRequest request, TimeSpan? timeout = null)
    {
        using CancellationTokenSource cts = new(timeout ?? DefaultTimeout);
        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        await client.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cts.Token).ConfigureAwait(false);

        await using NetworkStream stream = new(client);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };

        await writer.WriteLineAsync(AgentJson.Serialize(request)).ConfigureAwait(false);

        using CancellationTokenSource readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        string? line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);

        return line is null
            ? new AgentResponse { Ok = false, Error = "agent closed the connection" }
            : AgentJson.Deserialize<AgentResponse>(line) ?? new AgentResponse { Ok = false, Error = "malformed agent response" };
    }

    internal Task<AgentResponse> StatusAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Status });
    internal Task<AgentResponse> PingAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Ping });
    internal Task<AgentResponse> StartAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Start }, TimeSpan.FromSeconds(30));
    internal Task<AgentResponse> ReloadAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Reload }, TimeSpan.FromSeconds(30));
    internal Task<AgentResponse> StopAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Stop }, TimeSpan.FromSeconds(30));
    internal Task<AgentResponse> CleanupAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Cleanup }, TimeSpan.FromSeconds(30));
    internal Task<AgentResponse> ShutdownAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Shutdown }, TimeSpan.FromSeconds(30));
}

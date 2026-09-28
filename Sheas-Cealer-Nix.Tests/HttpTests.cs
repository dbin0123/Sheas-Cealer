using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sheas_Cealer_Nix.Utils;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class HttpTests
{
    // 回归测试：调用方传进来的 HttpClient 是长生命周期的（MainWin.MainClient / AboutWin.AboutClient
    // 都是窗口字段）。旧实现 `using var httpClient = client ?? new HttpClient()` 会把它 dispose 掉，
    // 于是「启动时的后台预检」用掉一次之后，点「更新上游规则」必抛 ObjectDisposedException，
    // 而异常从 async void 逃逸 -> 整个界面卡死。
    [Fact]
    public async Task SharedClientIsNotDisposedByGetAsync()
    {
        using TestServer server = TestServer.Respond("hello");

        using HttpClient client = new();

        Assert.Equal("hello", await Http.GetAsync(server.Url, client));
        Assert.Equal("hello", await Http.GetAsync(server.Url, client));
    }

    [Fact]
    public async Task SharedClientSurvivesWhenDifferentUrlsAreFetched()
    {
        using TestServer first = TestServer.Respond("one");
        using TestServer second = TestServer.Respond("two");

        using HttpClient client = new();

        // 顺序反了也必须都能用：旧实现在第一次调用后 client 就已经是 disposed 的了
        Assert.Equal("two", await Http.GetAsync(second.Url, client));
        Assert.Equal("one", await Http.GetAsync(first.Url, client));
    }

    // 超时必须翻译成可读提示，不能把 "The operation was canceled." 甩给用户
    [Fact]
    public async Task HungServerSurfacesAsReadableTimeout()
    {
        using TestServer server = TestServer.Hang();

        using HttpClient client = new();

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
            () => Http.GetAsync(server.Url, client, CancellationToken.None, TimeSpan.FromMilliseconds(300)));

        Assert.Contains("超时", ex.Message);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsTimeout()
    {
        using TestServer server = TestServer.Hang();

        using HttpClient client = new();
        using CancellationTokenSource cts = new();

        await cts.CancelAsync();

        // 调用方自己取消的应该原样抛 OperationCanceledException，而不是伪装成超时
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Http.GetAsync(server.Url, client, cts.Token, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task GetByteArrayDoesNotDisposeSharedClient()
    {
        using TestServer server = TestServer.Respond("payload");

        using HttpClient client = new();

        Assert.Equal(7, (await Http.GetByteArrayAsync(server.Url, client)).Length);
        Assert.Equal(7, (await Http.GetByteArrayAsync(server.Url, client)).Length);
    }
}

/// <summary>一次性本地 HTTP 服务，避免测试依赖外网。</summary>
internal sealed class TestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly bool _hang;
    private readonly string _body;
    private readonly CancellationTokenSource _cts = new();

    private TestServer(bool hang, string body)
    {
        _hang = hang;
        _body = body;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        _ = Task.Run(AcceptLoopAsync);
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

    public static TestServer Respond(string body) => new(hang: false, body);

    public static TestServer Hang() => new(hang: true, string.Empty);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 读掉请求头，客户端才会认为请求已发出
                        using NetworkStream stream = client.GetStream();
                        byte[] buffer = new byte[1024];
                        await stream.ReadAsync(buffer, _cts.Token);

                        if (_hang)
                        {
                            // 接受连接但永不响应：模拟上游挂住
                            await Task.Delay(Timeout.Infinite, _cts.Token);
                            return;
                        }

                        byte[] payload = Encoding.UTF8.GetBytes(_body);
                        byte[] head = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                            $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");

                        await stream.WriteAsync(head, _cts.Token);
                        await stream.WriteAsync(payload, _cts.Token);
                        await stream.FlushAsync(_cts.Token);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        client.Dispose();
                    }
                });
            }
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}

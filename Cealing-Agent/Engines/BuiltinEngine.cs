using Cealing_Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Cealing_Agent.Engines;

internal sealed class BuiltinEngine()
{
    // nginx 的 proxy_* 语义对照：return https://$host$request_uri → 302 跳转；
    // server_name ~正则 → 首个大不敏感匹配，未匹配时回退到第一个 server（nginx default_server 语义）。
    private static readonly string[] HopByHopHeaders =
        ["Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade"];

    // 响应头按总量边累加边丢弃，HPACK 编码器写不下时不会只丢一个头，而是整条流断掉
    // （浏览器侧 ERR_HTTP2_PROTOCOL_ERROR / ERR_CONNECTION_RESET，响应体全丢）。
    // 上限取 64KB：实测 Kestrel 自己不是瓶颈（单条 40KB、总量 96KB 都能完整发出去），
    // 真正会翻车的是客户端——curl 在总量 ~100KB 处截断，Firefox 的 network.http.max-header-length
    // 默认就是 65536。gemini.google.com 的 content-security-policy ~19KB + reporting-endpoints ~4KB
    // 在这个预算下能原样转发；再大的量才丢。
    private const int MaxRelayHeadersTotalLength = 64 * 1024;

    private readonly List<BuiltinEngineRule> _rules = [];
    private WebApplication? _app;
    private Task? _shutdownTask;

    internal bool IsRunning => _app is not null;

    internal async Task StartAsync(ProxyConfig config, X509Certificate2 certificate)
    {
        await StopAsync().ConfigureAwait(false);

        _rules.Clear();

        foreach (ProxyRule rule in config.Rules)
        {
            try
            {
                _rules.Add(BuiltinEngineRule.FromProxyRule(rule));
            }
            catch (ArgumentException ex)
            {
                AgentLog.Warn($"skip malformed rule '{rule.ServerName}': {ex.Message}");
            }
        }

        _rules.ForEach(rule => rule.Arm());

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = null });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider());
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.ListenLocalhost(config.HttpPort);
            kestrel.ListenLocalhost(config.HttpsPort, listen => listen.UseHttps(certificate));
        });

        WebApplication app = builder.Build();

        // .NET 8 的 WebApplication 只暴露 Run(string url) 和 Use(Func<RequestDelegate, RequestDelegate>)：
        // Run 内部是 StartAsync + WaitForShutdownAsync，同步阻塞到进程退出，StartAsync 永远不会返回
        // （原 Utils/BuiltinNginx.cs 的 app.Run(...) 就是这个坑，整个 StartAsync 卡死不返回）。
        // 所以挂中间件走 Use(middlewareFactory)，再显式 await StartAsync。
        app.Use(next => HandleAsync);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            _rules.ForEach(rule => rule.Disarm());
            _rules.Clear();

            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _app = app;
        _shutdownTask = app.WaitForShutdownAsync();

        AgentLog.Info($"builtin engine listening on {config.HttpPort}/{config.HttpsPort} with {_rules.Count} rules");
    }

    internal async Task StopAsync()
    {
        WebApplication? app = _app;

        _app = null;

        if (app is not null)
        {
            try { await app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { }

            try { await app.DisposeAsync().ConfigureAwait(false); }
            catch { }

            if (_shutdownTask is not null)
            {
                try { await _shutdownTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch { }
            }
        }

        _shutdownTask = null;
        _rules.ForEach(rule => rule.Disarm());
        _rules.Clear();
    }

    private async Task HandleAsync(HttpContext context)
    {
        HttpRequest request = context.Request;

        if (!request.IsHttps)
        {
            context.Response.Headers.Location = $"https://{request.Host.Host}{request.PathBase}{request.Path}{request.QueryString}";
            context.Response.StatusCode = StatusCodes.Status302Found;

            return;
        }

        BuiltinEngineRule? rule = _rules.Find(rule => rule.ServerName.IsMatch(request.Host.Host)) ?? (_rules.Count != 0 ? _rules[0] : null);

        if (rule is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using HttpRequestMessage upstreamRequest = new(new HttpMethod(request.Method), $"https://{rule.Ip}{request.PathBase}{request.Path}{request.QueryString}")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        upstreamRequest.Headers.Host = request.Host.Value;

        // TryAddWithoutValidation 在名字是 content header 时静默返回 false ——
        // Content-Type / Content-Encoding 这类必须进 Content.Headers，加不进
        // request headers。收集被拒的头，等挂了 Content 之后再搬过去。
        var contentHeaders = new List<(string Name, string[] Values)>();

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase) ||
                header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] values = header.Value.ToArray();
            if (!upstreamRequest.Headers.TryAddWithoutValidation(header.Key, values))
                contentHeaders.Add((header.Key, values));
        }

        // 请求体转发：HTTP/2 用 DATA 帧传 body，**没有** Transfer-Encoding 头。
        // 旧实现只在「有 Content-Length」或「有 Transfer-Encoding」时才附上 content，
        // 于是 HTTP/2 里不带 content-length 的 POST（ContentLength 为 null 且无 TE）
        // 会两个分支都不命中 → content 保持为 null → 请求体被静默丢弃，
        // 上游收到空 body 返回 400（gemini 的 /_/BardChatUi/data/batchexecute 就是这样）。
        //
        // 正确做法：GET/HEAD 之外一律挂 StreamContent，长度已知才显式设 ContentLength，
        // 未知时交给 HttpClient 用 chunked（HTTP/1.1 上游可接受）。
        bool attachContent = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);

        if (attachContent)
        {
            upstreamRequest.Content = new StreamContent(request.Body);

            if (request.ContentLength is long contentLength)
                upstreamRequest.Content.Headers.ContentLength = contentLength;

            // 只搬第一遍循环里被 .NET 拒收的头（= content header）。
            // 不能把所有头再往 Content.Headers 加一遍 —— TryAddWithoutValidation
            // 不校验名字，会把 Origin / sec-fetch-* 等也塞进去，抓包同一头出现
            // 两次，Google 边缘回 Error 400 (Bad Request)!!1。
            foreach (var (name, values) in contentHeaders)
            {
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;

                upstreamRequest.Content.Headers.TryAddWithoutValidation(name, values);
            }
        }

        HttpResponseMessage upstreamResponse;

        try
        {
            upstreamResponse = await rule.Client!.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            // 回源失败过去是**静默 502**。这里是整条链路上唯一能区分「连接被 TUN 吸走所以超时」
            // 「TLS 被 reset」还是「上游 IP 已经失效」的地方，不打出来只能靠外部猜。
            string matchedPattern = rule.ServerName.ToString();
            string pattern = matchedPattern.Length <= 72 ? matchedPattern : matchedPattern[..72] + '…';
            string inner = ex.InnerException is null ? string.Empty : $" <- {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";

            AgentLog.Warn($"upstream connect failed for {request.Host.Host}{request.Path} → {rule.Ip}:{rule.Port} sni={(rule.SniEnabled ? rule.Sni : "-")} rule=[{pattern}] {ex.GetType().Name}: {ex.Message}{inner}");

            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (upstreamResponse)
        {
            // 上游非 2xx 时把响应体缓冲下来。光看状态码和 Location 分不出 Google 是
            // 「参数错」「风控拦截」还是「TLS 指纹被识别」—— batchexecute 的 400 响应体
            // 里带结构化错误码，必须看到内容才能定位。错误响应体都很小，缓冲无副作用。
            int upstreamStatus = (int)upstreamResponse.StatusCode;

            byte[]? errorBody = null;

            if (upstreamStatus >= 400)
            {
                using var capture = new MemoryStream();
                await upstreamResponse.Content.CopyToAsync(capture, context.RequestAborted).ConfigureAwait(false);
                errorBody = capture.ToArray();

                string preview = errorBody.Length <= 240
                    ? System.Text.Encoding.UTF8.GetString(errorBody)
                    : System.Text.Encoding.UTF8.GetString(errorBody, 0, 240) + "…";

                // 响应头比状态码重要得多：server / x-cache / via / content-encoding 能直接
                // 判断是 Google 边缘层拒绝、还是应用层返回的。空 body 的 400 尤其要看头。
                var respHeaders = new List<string>();

                foreach (var h in upstreamResponse.Headers)
                    respHeaders.Add($"{h.Key}={h.Value}");

                foreach (var h in upstreamResponse.Content.Headers)
                    respHeaders.Add($"{h.Key}={h.Value}");

                AgentLog.Warn($"upstream {upstreamStatus} for {request.Host.Host}{request.Path} body={errorBody.Length}字节 respHeaders=[{string.Join(" | ", respHeaders)}] body={preview.Replace("\n", " ").Replace("\r", "")}");
            }

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;

            // 按累计预算转发响应头：HPACK 编码器按总量判定超限，所以必须边累加边丢弃，
            // 只看单个值不够（多个中等 header 加起来一样会爆）。
            int relayedHeadersLength = 0;

            void RelayHeaders(System.Net.Http.Headers.HttpHeaders source)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in source)
                {
                    if (HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                        continue;

                    string[] values = header.Value.ToArray();
                    int length = header.Key.Length + values.Sum(value => value.Length);

                    // 单个就超预算的直接丢；累计超预算后也不再收，保持已转发的部分可用。
                    if (length > MaxRelayHeadersTotalLength || relayedHeadersLength + length > MaxRelayHeadersTotalLength)
                    {
                        AgentLog.Warn($"drop oversized response header '{header.Key}' ({length} bytes, budget {MaxRelayHeadersTotalLength})");
                        continue;
                    }

                    relayedHeadersLength += length;
                    context.Response.Headers[header.Key] = new Microsoft.Extensions.Primitives.StringValues(values);
                }
            }

            RelayHeaders(upstreamResponse.Headers);
            RelayHeaders(upstreamResponse.Content.Headers);

            if (!HttpMethods.IsHead(request.Method) && upstreamResponse.StatusCode != HttpStatusCode.NoContent && upstreamResponse.StatusCode != HttpStatusCode.NotModified)
            {
                // 4xx 已经在上面读完并缓冲了，这里不能再从 Content 流读（已 EOF）。
                if (errorBody is not null)
                    await context.Response.Body.WriteAsync(errorBody, context.RequestAborted).ConfigureAwait(false);
                else
                    await upstreamResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    private sealed class FileLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

        public void Dispose() { }
    }

    private sealed class FileLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            try
            {
                AgentLog.Raw($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {formatter(state, exception)}{(exception is null ? string.Empty : Environment.NewLine + exception)}{Environment.NewLine}");
            }
            catch { }
        }
    }
}

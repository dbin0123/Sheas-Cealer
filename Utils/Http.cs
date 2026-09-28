using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Utils;

internal static class Http
{
    // 上游 GitLab 从国内访问经常要十几秒，不设上限的话「更新上游规则」会一直转圈；
    // 但也不能太长，否则用户以为界面死了。
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    internal static Task<string> GetAsync(string url, HttpClient? client = null) =>
        GetAsync(url, client, CancellationToken.None, null);

    internal static async Task<string> GetAsync(string url, HttpClient? client, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        // 关键：传入的 client 由调用方持有（MainClient / AboutClient 是窗口字段，生命周期跟窗口一样长），
        // 在这里 dispose 掉会污染调用方。旧实现写成 `using var httpClient = client ?? new HttpClient()`，
        // 于是「启动时那次后台预检」把 client 释放了，之后每次点按钮都抛
        // ObjectDisposedException —— 而异常从 async void 逃逸，表现就是点一下整个界面卡死。
        bool owned = client is null;
        HttpClient httpClient = client ?? new HttpClient();

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? DefaultTimeout);

            return await httpClient.GetStringAsync(url, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时不是取消：翻译成能看懂的提示，别把 "The operation was canceled." 甩给用户
            throw new TimeoutException($"请求超时（{(timeout ?? DefaultTimeout).TotalSeconds:0} 秒），请检查网络后重试");
        }
        finally
        {
            if (owned)
                httpClient.Dispose();
        }
    }

    internal static Task<string> GetAsync(string url, string userAgent, HttpClient? client = null) =>
        GetAsync(url, userAgent, client, CancellationToken.None);

    internal static async Task<string> GetAsync(string url, string userAgent, HttpClient? client, CancellationToken cancellationToken)
    {
        bool owned = client is null;
        HttpClient httpClient = client ?? new HttpClient();

        try
        {
            // 共享 client 上反复 Add 会把 User-Agent 累加成一串，先查后加
            if (!httpClient.DefaultRequestHeaders.Contains("User-Agent"))
                httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(DefaultTimeout);

            return await httpClient.GetStringAsync(url, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"请求超时（{DefaultTimeout.TotalSeconds:0} 秒），请检查网络后重试");
        }
        finally
        {
            if (owned)
                httpClient.Dispose();
        }
    }

    internal static async Task<byte[]> GetByteArrayAsync(string url, HttpClient? client = null)
    {
        bool owned = client is null;
        HttpClient httpClient = client ?? new HttpClient();

        try
        {
            using CancellationTokenSource cts = new(DefaultTimeout);

            return await httpClient.GetByteArrayAsync(url, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"请求超时（{DefaultTimeout.TotalSeconds:0} 秒），请检查网络后重试");
        }
        finally
        {
            if (owned)
                httpClient.Dispose();
        }
    }
}

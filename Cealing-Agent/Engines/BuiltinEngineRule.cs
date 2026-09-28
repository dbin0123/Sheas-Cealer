using Cealing_Core;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent.Engines;

internal sealed class BuiltinEngineRule
{
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    internal Regex ServerName { get; init; } = null!;
    internal string Ip { get; init; } = string.Empty;
    internal string? Sni { get; init; }
    internal bool SniEnabled { get; init; } = true;
    internal int Port { get; init; } = 443;

    internal HttpClient? Client;

    internal static BuiltinEngineRule FromProxyRule(ProxyRule rule) => new()
    {
        ServerName = new Regex(rule.ServerName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout),
        Ip = rule.Ip,
        Sni = rule.Sni,
        SniEnabled = rule.SniEnabled,
        Port = rule.Port
    };

    internal void Arm() => Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = ConnectAsync,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    internal void Disarm() => Client?.Dispose();

    // 上游证书一律不校验：SNI 已经被换成伪造值，返回的证书与伪造域名必然对不上。
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(Ip, Port, cancellationToken).ConfigureAwait(false);

            SslStream sslStream = new(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false, static (_, _, _, _) => true);

            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = SniEnabled ? Sni : null,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken).ConfigureAwait(false);

            return sslStream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

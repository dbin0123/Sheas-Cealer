using Cealing_Agent;
using Cealing_Core;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public sealed class ProxyCertificateHandshakeTests
{
    // 回归：叶证书是 child.CopyWithPrivateKey(key) 造出来的「临时密钥（ephemeral key）」，
    // Windows 的 Schannel 服务端不接受它，Kestrel 会在收到 ClientHello 后直接断开 socket。
    // 浏览器看到的是 ERR_CONNECTION_CLOSED（连 ServerHello 都没收到），而不是证书告警，
    // 所以从 GUI 日志「builtin engine listening on 80/443」完全看不出引擎已经不能服务 HTTPS。
    [Fact]
    public async Task ChildCertificateCanServeTlsHandshake()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "cealing-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            ProxyConfig config = new()
            {
                CertSans = [new ProxyCertSan { Domain = "example.com", Wildcard = false }]
            };

            ProxyCertificate certificate = ProxyCertificateFactory.Create(config, dataDir, ownerUid: -1);

            try
            {
                Assert.True(certificate.Child.HasPrivateKey, "leaf certificate must carry a private key");

                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();

                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<Exception?> serverTask = ServeAsync(listener, certificate.Child);

                using (TcpClient client = new())
                {
                    await client.ConnectAsync(IPAddress.Loopback, port);

                    using SslStream clientStream = new(client.GetStream(), false, (_, _, _, _) => true);
                    await clientStream.AuthenticateAsClientAsync("example.com");

                    Exception? serverError = await serverTask;

                    Assert.Null(serverError);

                    X509Certificate2 presented = new(clientStream.RemoteCertificate!);

                    Assert.Equal(certificate.Child.Thumbprint, presented.Thumbprint);

                    X509SubjectAlternativeNameExtension san = presented.Extensions
                        .OfType<X509SubjectAlternativeNameExtension>()
                        .Single();

                    Assert.Contains("example.com", san.EnumerateDnsNames());
                }
            }
            finally
            {
                certificate.Dispose();
            }
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    // 根证书必须跨启动复用：每次重启都换一张根，浏览器/系统信任的是旧根，
    // 代理出示的叶子就链对不上（这条不变量之前只靠注释守着，Windows 上一个同类
    // API 陷阱能让它静默失效 —— 见 JavaTrust.RootHashes 的 v1.0.10 回归）。
    [Fact]
    public void RootCertificateIsReusedAcrossStarts()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "cealing-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        ProxyConfig config = new()
        {
            CertSans = [new ProxyCertSan { Domain = "example.com", Wildcard = false }]
        };

        try
        {
            ProxyCertificate first = ProxyCertificateFactory.Create(config, dataDir, ownerUid: -1);
            byte[] firstRoot = first.Root.RawData;

            first.Dispose();

            ProxyCertificate second = ProxyCertificateFactory.Create(config, dataDir, ownerUid: -1);

            try
            {
                Assert.Equal(Convert.ToHexString(firstRoot), Convert.ToHexString(second.Root.RawData));

                // 复用路径要能把私钥配回来（Cealing-Key.pem），否则叶证书没法重签。
                Assert.True(second.Root.HasPrivateKey, "reused root must carry the private key from Cealing-Key.pem");
                using RSA? reusedKey = second.Root.GetRSAPrivateKey();

                Assert.NotNull(reusedKey);
            }
            finally
            {
                second.Dispose();
            }
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    private static async Task<Exception?> ServeAsync(TcpListener listener, X509Certificate2 certificate)
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using var serverStream = new SslStream(client.GetStream(), false);

            await serverStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate })
                .ConfigureAwait(false);

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            listener.Stop();
        }
    }
}

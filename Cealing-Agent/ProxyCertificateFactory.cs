using Cealing_Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Cealing_Agent;

internal sealed record ProxyCertificate(RSA Key, X509Certificate2 Root, X509Certificate2 Child, string RootPemPath, string ChildPemPath, string KeyPemPath)
{
    // 只在 EngineSupervisor.StopAsync 里手动释放（见 Task 7 Step 3），
    // 持有方只有 supervisor，不要在调用方再包一层 using
    internal void Dispose()
    {
        Root.Dispose();
        Child.Dispose();
        Key.Dispose();
    }
}

internal static class ProxyCertificateFactory
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static ProxyCertificate Create(ProxyConfig config, string dataDir, int ownerUid)
    {
        string rootPemPath = Path.Combine(dataDir, "Cealing-Root.pem");
        string childPemPath = Path.Combine(dataDir, "Cealing-Cert.pem");
        string keyPemPath = Path.Combine(dataDir, "Cealing-Key.pem");

        // 根证书必须**复用**而不是每次重新生成。
        // 旧实现无条件 CreateSelfSigned，于是每次发布/重启都产生一个新根；而 TrustStore.Install
        // 只在 IsTrustedInSystem() 为 false 时才安装，钥匙串里已经有受信任的旧根，
        // 新根就永远装不进去 —— 代理出示的叶证书由新根签发，浏览器信任的是旧根，
        // 链对不上，TLS/ALPN 协商直接被拒（ERR_HTTP2_PROTOCOL_ERROR / ERR_CONNECTION_RESET）。
        if (TryLoadExistingRoot(rootPemPath, keyPemPath, out RSA? existingKey, out X509Certificate2? existingRoot))
        {
            RSA reusedKey = existingKey!;
            X509Certificate2 reusedRoot = existingRoot!;

            X509Certificate2 reusedChild = IssueChild(config, reusedKey, reusedRoot);
            WritePem(childPemPath, reusedChild.ExportCertificatePem(), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, ownerUid);
            WritePem(keyPemPath, reusedKey.ExportPkcs8PrivateKeyPem(), UnixFileMode.UserRead | UnixFileMode.UserWrite, ownerUid);

            return new ProxyCertificate(reusedKey, reusedRoot, reusedChild, rootPemPath, childPemPath, keyPemPath);
        }

        RSA key = RSA.Create(2048);

        CertificateRequest rootRequest = new(TrustStore.RootCertSubjectName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, false));

        // 不能用 using：root/child 的生命周期要转移给返回的 ProxyCertificate record，
        // 由 EngineSupervisor.StopAsync 里的 record.Dispose() 统一释放。这里用 using 会在方法返回时提前释放。
        X509Certificate2 root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(100));

        X509Certificate2 childWithKey = IssueChild(config, key, root);

        // root.pem / child.pem 必须让普通用户也能读：GUI 侧要算 thumbprint、做用户域信任。
        // 但私钥（Cealing-Key.pem）绝不能 world-readable —— 它配合被信任的根证书就是 MITM 能力，
        // 只给属主（agent 是 root）读。
        WritePem(rootPemPath, root.ExportCertificatePem(), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, ownerUid);
        WritePem(childPemPath, childWithKey.ExportCertificatePem(), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, ownerUid);
        WritePem(keyPemPath, key.ExportPkcs8PrivateKeyPem(), UnixFileMode.UserRead | UnixFileMode.UserWrite, ownerUid);

        return new ProxyCertificate(key, root, childWithKey, rootPemPath, childPemPath, keyPemPath);
    }

    // SAN 每次启动都可能变（上游规则更新），所以复用根证书时叶证书必须重签。
    private static X509Certificate2 IssueChild(ProxyConfig config, RSA key, X509Certificate2 root)
    {
        CertificateRequest childRequest = new(TrustStore.ChildCertSubjectName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder sanBuilder = new();

        foreach (ProxyCertSan san in config.CertSans)
        {
            if (string.IsNullOrWhiteSpace(san.Domain) || san.Domain.Contains('*'))
                continue;

            sanBuilder.AddDnsName(san.Wildcard ? $"*.{san.Domain}" : san.Domain);
        }

        childRequest.CertificateExtensions.Add(sanBuilder.Build());

        // 明确标成 TLS 服务器证书（serverAuth）。少了 EKU 大多能用，但某些验证器/macOS 信任评估会挑刺。
        childRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        X509Certificate2 child = childRequest.Create(root, root.NotBefore, root.NotAfter, Guid.NewGuid().ToByteArray());

        // Kestrel 的 UseHttps 要求证书**带私钥**，而 childRequest.Create(root, ...) 出来的证书没有私钥
        // （原 MainWin 也是先 CopyWithPrivateKey 才能给内置引擎用）。少了这一步，内置引擎会报
        // "The server mode SSL must use a certificate with the associated private key."。
        X509Certificate2 childWithKey = child.CopyWithPrivateKey(key);
        child.Dispose();

        return OperatingSystem.IsWindows() ? ToSchannelUsableCertificate(childWithKey) : childWithKey;
    }

    // CopyWithPrivateKey 挂上去的是**临时密钥（ephemeral key）**，Windows 的 Schannel 服务端明确不接受：
    // SslStream.AuthenticateAsServer 抛 AuthenticationException「Authentication failed because the
    // platform does not support ephemeral keys」（内部 Win32「安全包中没有可用的凭证」SEC_E_NO_CREDENTIALS），
    // Kestrel 于是在收到 ClientHello 后直接断开 —— 浏览器表现为 ERR_CONNECTION_CLOSED，
    // 而不是证书告警，所以「引擎已监听 80/443」的日志完全掩盖了这个故障。
    // Linux/macOS 的 SslStream 走 OpenSSL，临时密钥可用，不用绕。
    //
    // 绕法：内存里导出 PFX 再导回来。回来的那份密钥是 Schannel 可用的密钥句柄，
    // 不落盘、不写密钥容器（不带 PersistKeySet），进程退出即消失。
    private static X509Certificate2 ToSchannelUsableCertificate(X509Certificate2 certificate)
    {
        string password = Guid.NewGuid().ToString("N");

        byte[] pkcs12 = certificate.Export(X509ContentType.Pkcs12, password);

        X509Certificate2 ephemeral = certificate;

        try
        {
            return new X509Certificate2(pkcs12, password, X509KeyStorageFlags.Exportable);
        }
        finally
        {
            ephemeral.Dispose();
        }
    }

    // 只有根证书仍在有效期内、且能配上一把可用的私钥时才复用；否则重新生成。
    private static bool TryLoadExistingRoot(string rootPemPath, string keyPemPath, out RSA? key, out X509Certificate2? root)
    {
        key = null;
        root = null;

        try
        {
            if (!File.Exists(rootPemPath) || !File.Exists(keyPemPath))
                return false;

            // root.pem 里**只有证书**（写的是 ExportCertificatePem），私钥单独放在 key.pem。
            // 所以不能对 root.pem 单独 CreateFromPemFile 后判 HasPrivateKey —— 那永远为空，
            // 会每次都误判为「不可复用」而重新生成根证书，反复打断浏览器信任。
            // CreateFromPemFile(cert, key) 才能把私钥装进证书对象。
            X509Certificate2 loadedRoot = X509Certificate2.CreateFromPemFile(rootPemPath, keyPemPath);
            RSA? loadedKey = loadedRoot.GetRSAPrivateKey();

            DateTime now = DateTime.Now;

            if (loadedKey is null || now < loadedRoot.NotBefore || now > loadedRoot.NotAfter)
            {
                loadedKey?.Dispose();
                loadedRoot.Dispose();
                return false;
            }

            root = loadedRoot;
            key = loadedKey;

            return true;
        }
        catch
        {
            key?.Dispose();
            root?.Dispose();

            key = null;
            root = null;

            return false;
        }
    }

    private static void WritePem(string path, string pem, UnixFileMode mode, int ownerUid)
    {
        File.WriteAllText(path, pem, Utf8NoBom);

        OwnedFile.HandOver(path, ownerUid);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

/// <summary>
/// 只检查看起来没问题：引擎里那条「流量被覆盖了没有」的答案，
/// 全在 bundle 的那份 <c>Cealing-Host-B.json</c> 里。
/// 它刷进数据目录、追加域名都属于改数据，所以这里用测试把内容钉住。
/// </summary>
public class BundledRulesContentTests
{
    // Antigravity（Gemini IDE）登录 + 资格判定实际要打到的全部 host。
    // 少任何一个都会退化成「浏览器一切正常、IDE 卡在加载」，
    // 而客户端不会报域名错误，只会在 language_server.log 里 EOF / TLS handshake timeout。
    private static readonly string[] AntigravityHosts =
    [
        "antigravity.google",
        "www.antigravity.google",
        "codeassist.google",
        "accounts.google.com",
        "oauth2.googleapis.com",
        "www.googleapis.com",
        "cloudcode-pa.googleapis.com",
        "daily-cloudcode-pa.googleapis.com",
        "generativelanguage.googleapis.com",
        "play.googleapis.com",
        "storage.googleapis.com",
        "fonts.googleapis.com",
        "developers.google.com",
        "lh3.googleusercontent.com"
    ];

    // 应用自带的 Google 国内前置：按 Host 选 vhost，SNI 恒为 g.cn，
    // 所以一条规则可以带一串域名。
    private const string BundledFrontIp = "183.56.143.147";
    private const string BundledFrontSni = "g.cn";

    private sealed record BundledRule(List<string> Includes, string? Sni, string Ip);

    private static List<BundledRule> ReadBundledRules()
    {
        string file = Path.Combine(FindRepoRoot(), "Cealing-Host-B.json");
        JsonDocumentOptions options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

        return JsonDocument.Parse(File.ReadAllText(file), options).RootElement
            .EnumerateArray()
            .Select(rule => new BundledRule(
                rule[0].EnumerateArray().Select(d => d.ToString()).ToList(),
                rule[1].ValueKind == JsonValueKind.Null ? null : rule[1].ToString(),
                rule[2].ToString()))
            .ToList();
    }

    // 测试读的是仓库根那份真文件（构建时 CopyToOutputDirectory 才进产物），
    // 从 bin 往上找 .sln 定位，避免写死 ../.. 的层数。
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sheas-Cealer-Nix.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new FileNotFoundException("cannot locate repo root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void BundledRulesCoverEveryAntigravityHost()
    {
        List<string> covered = [.. ReadBundledRules().SelectMany(r => r.Includes)];

        foreach (string host in AntigravityHosts)
            Assert.Contains(host, covered);
    }

    // .google 顶级域只有按 Host 路由的前置能服务：sniproxy 式中转对 Schannel 客户端会直接断连
    // （浏览器能开、IDE 不能开就是这么来的）。发版规则里不许出现别人的中转 IP。
    [Fact]
    public void BundledRulesUseOnlyTheAppOwnedFront()
    {
        foreach (BundledRule rule in ReadBundledRules())
        {
            Assert.Equal(BundledFrontIp, rule.Ip);
            Assert.Equal(BundledFrontSni, rule.Sni);
        }
    }
}

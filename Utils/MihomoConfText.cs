using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Sheas_Cealer_Nix.Utils;

/// <summary>
/// 读取并解析用户的 mihomo <c>config.yaml</c>。
///
/// <para>
/// 这个文件是用户自己维护的（代理节点、分流规则、端口全在里面），而每次启动代理都要往它里面
/// 注入 <c>tun</c> / <c>hosts</c> / <c>dns</c>，所以读一次要用两遍、事后还要把原文还给用户。
/// </para>
/// <para>
/// 原来的写法是在同一个 <see cref="FileStream"/> 上 <c>new StreamReader(...).ReadToEnd()</c> 两遍。
/// 第一遍把位置推到流末尾，第二遍拿到空串，于是「用户的原文」这个还原值被空串覆盖 ——
/// 点一次「启动 mihomo」，<c>config.yaml</c> 就被清成 0 字节，节点全丢，
/// 表现就是 github.com 这类域名在浏览器里也再也走不通。所以这里坚持「一次读完，按同一份文本解析两遍」。
/// </para>
/// </summary>
internal static class MihomoConfText
{
    internal static string Read(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    /// <summary>
    /// 每次调用都产出一棵全新的对象树。两个变体配置（带 dns 注入的和不带的）必须互不影响，
    /// 共用一份字典会让后一次赋值悄悄改掉前一次。
    /// </summary>
    internal static Dictionary<string, object> Parse(string text) =>
        new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<Dictionary<string, object>>(text) ?? [];
}

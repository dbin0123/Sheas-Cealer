using Cealing_Agent.Engines;
using System.Linq;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class BuiltinEngineHeaderRelayTests
{
    // 回归：gemini.google.com/app 的 content-security-policy 有 19764 字节。
    // Kestrel 的 HTTP/2 帧写出器把整块响应头编进 new byte[_maxFrameSize]（Chrome 只宣告 16384），
    // net8 的 HPackHeaderWriter 在「缓冲区还空着但这一条塞不下」时抛 HPackEncodingException，
    // 结果不是丢一条头，而是整块响应头作废 → 浏览器 RST_STREAM / 页面白屏。
    // 实测阈值：单值 15KB 通过、16KB 起抛。
    [Fact]
    public void OversizedSingleValueIsDroppedSoTheRestStillGoesOut()
    {
        string huge = new string('a', 19764);

        string[] relayed = BuiltinEngine.FilterRelayableValues("content-security-policy", [huge, "default-src 'self'", "img-src *"]);

        Assert.Equal(["default-src 'self'", "img-src *"], relayed);
    }

    [Fact]
    public void ValuesUpToTheCapAreRelayedUnchanged()
    {
        string atCap = new string('a', 15 * 1024);
        string overCap = new string('a', 15 * 1024 + 1);

        Assert.Equal([atCap], BuiltinEngine.FilterRelayableValues("x-at-cap", [atCap]));
        Assert.Empty(BuiltinEngine.FilterRelayableValues("x-over-cap", [overCap]));
    }

    // 值里只要有一个非 ASCII 字符，context.Response.Headers[name] = value 就抛
    // InvalidOperationException，整个响应变成 500；上游返回 UTF-8 文件名是常见情况。
    [Theory]
    [InlineData("attachment; filename= café.png")]
    [InlineData("bad\x01control")]
    [InlineData("bad\ncrlf")]
    public void NonAsciiValueIsDroppedInsteadOfFiftyingTheWholeResponse(string value)
    {
        Assert.Empty(BuiltinEngine.FilterRelayableValues("content-disposition", [value]));
    }

    [Fact]
    public void GoodValuesSurviveWhenASiblingIsUnrelayable()
    {
        string[] relayed = BuiltinEngine.FilterRelayableValues("set-cookie", ["NID=abc; Path=/", "SID=café", "TOK=xyz"]);

        Assert.Equal(["NID=abc; Path=/", "TOK=xyz"], relayed);
    }

    // HTAB 是 RFC 7230 允许的观察空白，Kestrel 也接受，不该被当成控制字符丢掉。
    [Fact]
    public void TabInsideValueIsKept()
    {
        Assert.Equal(["a\tb"], BuiltinEngine.FilterRelayableValues("x-tab", ["a\tb"]));
    }

    [Fact]
    public void PrintableAsciiUpToTildeIsKept()
    {
        string printable = string.Concat(Enumerable.Range(' ', 0x7E - ' ' + 1).Select(i => (char)i));

        Assert.Equal([printable], BuiltinEngine.FilterRelayableValues("x-printable", [printable]));
    }

    // 全部值都被丢掉时返回空数组，调用方据此整条头不发（空的 StringValues 会把头删掉）。
    [Fact]
    public void AllValuesDroppedYieldsEmptyArray()
    {
        Assert.Empty(BuiltinEngine.FilterRelayableValues("x-all-bad", [new string('a', 20000), "café"]));
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace EmqxMonitor.Tests;

/// <summary>带容错 converter 的临时 DTO（与 EmqxClientInfo.ClientAttrs 同构）</summary>
public class AttrHolder
{
    [JsonPropertyName("client_attrs")]
    [JsonConverter(typeof(TolerantStringDictConverter))]
    public Dictionary<string, string>? ClientAttrs { get; set; }
}

public class TolerantStringDictConverterTests
{
    private static Dictionary<string, string>? Parse(string json)
        => JsonSerializer.Deserialize<AttrHolder>(json)?.ClientAttrs;

    [Fact]
    public void 数字uid_归一为字符串()
    {
        var d = Parse("{\"client_attrs\":{\"callsign\":\"BG5AAA\",\"uid\":12345}}");
        Assert.NotNull(d);
        Assert.Equal("BG5AAA", d!["callsign"]);
        Assert.Equal("12345", d["uid"]);
    }

    [Fact]
    public void 字符串uid_原样保留()
    {
        var d = Parse("{\"client_attrs\":{\"uid\":\"12345\"}}");
        Assert.Equal("12345", d!["uid"]);
    }

    [Fact]
    public void 无attrs字段_返回null()
    {
        Assert.Null(Parse("{}"));
    }

    [Fact]
    public void attrs为null_返回null()
    {
        Assert.Null(Parse("{\"client_attrs\":null}"));
    }

    [Fact]
    public void 空对象_返回空字典()
    {
        var d = Parse("{\"client_attrs\":{}}");
        Assert.NotNull(d);
        Assert.Empty(d!);
    }

    [Fact]
    public void 布尔值_归一为字符串()
    {
        var d = Parse("{\"client_attrs\":{\"flag\":true}}");
        Assert.Equal("true", d!["flag"]);
    }
}

public class WebHelpersTests
{
    [Fact]
    public void ParseRange_正常范围()
    {
        var (f, t, err) = WebHelpers.ParseRange("2026-08-01T00:00", "2026-08-01T01:00");
        Assert.Null(err);
        Assert.Equal("2026-08-01 00:00:00", f);
        Assert.Equal("2026-08-01 01:00:00", t);
    }

    [Fact]
    public void ParseRange_格式错误()
    {
        var (_, _, err) = WebHelpers.ParseRange("bad", "2026-08-01T01:00");
        Assert.NotNull(err);
    }

    [Fact]
    public void ParseRange_结束早于开始()
    {
        var (_, _, err) = WebHelpers.ParseRange("2026-08-02T00:00", "2026-08-01T00:00");
        Assert.NotNull(err);
    }

    [Fact]
    public void ParseRange_跨度超31天_拒绝()
    {
        var (_, _, err) = WebHelpers.ParseRange("2026-01-01T00:00", "2026-03-01T00:00");
        Assert.NotNull(err);
    }

    [Fact]
    public void Csv_公式注入_加单引号前缀()
    {
        Assert.Equal("'=SUM(A1)", WebHelpers.Csv("=SUM(A1)"));
        Assert.Equal("'@cmd", WebHelpers.Csv("@cmd"));
        Assert.Equal("'-x", WebHelpers.Csv("-x"));
    }

    [Fact]
    public void Csv_含逗号引号_转义()
    {
        Assert.Equal("\"a,b\"", WebHelpers.Csv("a,b"));
        Assert.Equal("\"a\"\"b\"", WebHelpers.Csv("a\"b"));
    }

    [Fact]
    public void Csv_普通值_原样()
    {
        Assert.Equal("BG5ESN", WebHelpers.Csv("BG5ESN"));
    }
}

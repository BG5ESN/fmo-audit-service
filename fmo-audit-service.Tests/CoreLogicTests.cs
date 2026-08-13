using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace EmqxMonitor.Tests;

public class Crc32Tests
{
    [Fact]
    public void StandardVector_123456789()
    {
        // zlib 标准向量：CRC32("123456789") = 0xCBF43926
        var crc = Crc32.Compute(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void Empty_IsZero()
    {
        Assert.Equal(0u, Crc32.Compute([]));
    }
}

public class FmoRawParserTests
{
    /// <summary>构造合法 FMO/RAW 包：64B 包头（小端）+ 帧区，checkSum = CRC32(帧区)</summary>
    private static byte[] BuildPacket(byte[]? frame = null, uint? uid = null, uint? lenOverride = null, string callsign = "BG5ESN")
    {
        frame ??= [1, 2, 3, 4, 5, 6, 7, 8];
        var raw = new byte[64 + frame.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0, 2), 2);                    // version
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(2, 4), 0xDEADBEEF);           // flags
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(6, 4), uid ?? 12345u);         // UID
        Encoding.ASCII.GetBytes(callsign).CopyTo(raw.AsSpan(10));                          // callsign[12]
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(22, 4), 1700000000u);          // streamBeginUTC
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(26, 4), 1700000100u);          // timestamp
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(30, 4), lenOverride ?? (uint)raw.Length); // len
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(34, 2), 7);                    // frameNum
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(36, 4), Crc32.Compute(frame)); // checkSum
        raw[40] = 9;                                                                       // smeter
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(41, 4), 999u);                 // srvUid
        frame.CopyTo(raw, 64);
        return raw;
    }

    [Fact]
    public void Parse_合法包_字段正确()
    {
        var r = FmoRawParser.Parse(BuildPacket());
        Assert.True(r.Ok, r.Error);
        Assert.Equal(12345u, r.Uid);
        Assert.Equal("BG5ESN", r.Callsign);
        Assert.Equal(72u, r.Len);
        Assert.Equal(7, r.FrameNum);
        Assert.Equal(999u, r.SrvUid);
        Assert.Equal(9, r.Smeter);
        Assert.Equal(2, r.Version);
        Assert.Equal(0xDEADBEEFu, r.Flags);
        Assert.Equal(1700000000u, r.StreamBeginUtc);
        Assert.Equal(1700000100u, r.Timestamp);
        Assert.True(r.CrcOk);
    }

    [Fact]
    public void Parse_包长不足72_失败()
    {
        var r = FmoRawParser.Parse(new byte[71]);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Parse_超MTU_失败()
    {
        var r = FmoRawParser.Parse(new byte[1401]);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Parse_len字段与包长不符_失败()
    {
        var r = FmoRawParser.Parse(BuildPacket(lenOverride: 100));
        Assert.False(r.Ok);
        Assert.Contains("len", r.Error);
    }

    [Fact]
    public void Parse_callsign按零截断_短呼号()
    {
        // "BG5AAA" 5 字节写入 12 字节区域，后面是 0 填充
        var r = FmoRawParser.Parse(BuildPacket(callsign: "BG5AAA"));
        Assert.True(r.Ok);
        Assert.Equal("BG5AAA", r.Callsign);
    }

    [Fact]
    public void Parse_crc错误_仅标记CrcOkFalse_不判失败()
    {
        // 设计语义：CRC 只覆盖帧区且设备端已核验，此处不据此判 FAIL
        var raw = BuildPacket();
        raw[36] ^= 0xFF;   // 破坏 checkSum
        var r = FmoRawParser.Parse(raw);
        Assert.True(r.Ok);
        Assert.False(r.CrcOk);
    }
}

public class UpdateServiceVersionTests
{
    [Theory]
    [InlineData("2.0.14", "2.0.13", 1)]    // 大于
    [InlineData("2.0.13", "2.0.14", -1)]   // 小于
    [InlineData("2.0.13", "2.0.13", 0)]    // 相等
    [InlineData("v2.1.0", "2.0.99", 1)]    // v 前缀
    [InlineData("3.0.0", "2.99.99", 1)]    // 大版本
    public void CompareVersions(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(UpdateService.CompareVersions(a, b)));
    }
}

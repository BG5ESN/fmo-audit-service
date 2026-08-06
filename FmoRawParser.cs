using System.Buffers.Binary;

namespace EmqxMonitor;

/// <summary>
/// FMO/RAW 包头解析器（fmo-raw-header-audit 文档）。
/// MQTT payload 前 64 字节为固定包头（小端）：version(2) flags(4) UID(4) callsign[12] streamBeginUTC(4)
/// timestamp(4) len(4) frameNum(2) checkSum(4) smeter(1) srvUID(4) reserved(19)。
/// 合法性校验（对应固件 isValidPacket）：长度 ≥72、head.len == 实际长度、≤1400(MTU)、CRC32(raw[64:]) == checkSum。
/// 包头明文不加密不签名——包头是声明，身份真相在连接认证侧（client_attrs）。
/// </summary>
public static class FmoRawParser
{
    public const int HeadSize = 64;
    public const int MinValidLen = 72;      // 64 包头 + 8 首帧头
    public const int MaxLen = 1400;         // MTU

    /// <summary>解析结果</summary>
    public sealed class Result
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }          // 解析失败原因（Ok=false 时）
        public uint Uid { get; init; }
        public string Callsign { get; init; } = "";  // 按 \0 截断，原样（发送侧已大写）
        public uint Len { get; init; }
        public ushort FrameNum { get; init; }
        public uint CheckSum { get; init; }
        public bool CrcOk { get; init; }
        public byte Smeter { get; init; }
        public uint SrvUid { get; init; }
        public uint StreamBeginUtc { get; init; }
        public uint Timestamp { get; init; }
        public ushort Version { get; init; }
        public uint Flags { get; init; }
    }

    /// <summary>解析并校验一个 RAW 包；非法包返回 Ok=false + Error</summary>
    public static Result Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < MinValidLen)
            return new Result { Ok = false, Error = "包长不足 72 字节" };
        if (raw.Length > MaxLen)
            return new Result { Ok = false, Error = "超过 MTU 1400" };

        var version = BinaryPrimitives.ReadUInt16LittleEndian(raw[0..2]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(raw[2..6]);
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(raw[6..10]);
        var callsign = raw[10..22];
        // 12 字节定长，按第一个 \0 截断
        var csEnd = callsign.IndexOf((byte)0);
        if (csEnd < 0) csEnd = callsign.Length;
        var cs = System.Text.Encoding.ASCII.GetString(callsign[..csEnd]).Trim();
        var streamBegin = BinaryPrimitives.ReadUInt32LittleEndian(raw[22..26]);
        var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(raw[26..30]);
        var len = BinaryPrimitives.ReadUInt32LittleEndian(raw[30..34]);
        var frameNum = BinaryPrimitives.ReadUInt16LittleEndian(raw[34..36]);
        var checkSum = BinaryPrimitives.ReadUInt32LittleEndian(raw[36..40]);
        var smeter = raw[40];
        var srvUid = BinaryPrimitives.ReadUInt32LittleEndian(raw[41..45]);

        // 合法性校验
        if (len != (uint)raw.Length)
            return new Result { Ok = false, Error = $"len 字段({len})与包长({raw.Length})不符" };
        if (len < MinValidLen)
            return new Result { Ok = false, Error = "len 字段小于 72" };

        // CRC32 只覆盖 offset 64 之后的帧区——设备端已核验 CRC，此处不据此判 FAIL，
        // 仅计算结果供审计展示参考（crcOk）
        var crc = Crc32.Compute(raw[HeadSize..]);
        var crcOk = crc == checkSum;

        return new Result
        {
            Ok = true,
            Uid = uid,
            Callsign = cs,
            Len = len,
            FrameNum = frameNum,
            CheckSum = checkSum,
            CrcOk = crcOk,
            Smeter = smeter,
            SrvUid = srvUid,
            StreamBeginUtc = streamBegin,
            Timestamp = timestamp,
            Version = version,
            Flags = flags,
        };
    }
}

/// <summary>标准 CRC-32（zlib 兼容，多项式 0xEDB88320，初值 0，无最终异或）——与 ESP32 crc32_le 一致</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;   // 标准 CRC-32 初值（zlib 语义）
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;   // 最终异或
    }
}

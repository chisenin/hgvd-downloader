using System;
using System.Collections.Generic;

namespace Hgvd.Core.Sign;

/// <summary>
/// x-medusa 头的组装。
///
/// ★ 对应 Python 版 <c>hgvd/sign/core.py</c> 的
///   <c>_assemble_medusa</c> / <c>_xmxor</c> / <c>_key_hash</c> / <c>_device_proto</c>。
/// </summary>
public static class Medusa
{
    private static readonly byte[] MedusaSignKey = Util.FromHex(
        "8ebdfa3806ecc5cee79423e6029ed82540bc2218bb7eaef71cb691f7aa8aa2f5");
    private static readonly byte[] MedusaAesKey = Util.FromHex(
        "f1593376766ea98d34f31b057a9d5be4");
    private static readonly byte[] MedusaAesIv = Util.FromHex(
        "1fe109a4125283f418de9e051a969e12");

    /// <summary>签名熵 —— 决定 medusa 头里几个随机字段。</summary>
    public sealed class MedusaEntropy
    {
        public long EnvLaunch = 100;
        public long EnvPid = 10001;
        public long NowSecs;
        public int MessageRandom;
        public int Random;
        public int PackedRandom;
        public int GorgonRandom;
        public int HeliosRandom;
    }

    internal static byte[] Xmxor(byte[] data, byte[] key)
    {
        int n = data.Length;
        var encoded = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int at = (i * 4) & 28;
            byte d0 = key[at];
            byte d1 = key[at + 1];
            byte d2 = (byte)((((data[i] << 4) | (data[i] >> 4)) + d0) & 0xFF);
            d2 = (byte)((~d2 & 0xFF) ^ d1);
            d2 = (byte)(((d2 << 3) | (d2 >> 5)) & 0xFF);
            d2 = (byte)((d2 + d1) & 0xFF);
            d2 ^= d0;
            encoded[n - i - 1] = (byte)(~d2 & 0xFF);
        }

        byte last = (byte)(encoded[n - 1] ^ encoded[n - 2]);
        byte first = encoded[0];
        encoded[0] = (byte)((~last & 0xFF) + first);
        encoded[1] = (byte)((encoded[0] ^ encoded[n - 1] ^ 254) + encoded[1]);
        byte shifted = (byte)((encoded[1] << 3) | (encoded[1] >> 5));
        byte fix2 = (byte)((last - first) ^ (shifted ^ 2));
        encoded[2] = (byte)(encoded[2] + fix2);

        for (int i = 0; i < n - 4; i++)
        {
            byte sh = (byte)((encoded[i + 2] << 3) | (encoded[i + 2] >> 5));
            byte temp = (byte)(sh ^ encoded[i + 1] ^ (i + 3));
            encoded[i + 3] = (byte)((~temp & 0xFF) + encoded[i + 3]);
        }

        encoded[n - 1] ^= encoded[n - 2];
        uint total = 0;
        for (int i = 1; i < n; i++) total += encoded[i];
        encoded[0] = (byte)((encoded[0] ^ encoded[1]) + (total & 0xFF));
        return encoded;
    }

    internal static (byte[] Hash, byte[] Seed) KeyHash(byte[] signKey, int randomV)
    {
        // ★ randomV 是**无符号**语义 —— 必须先转成 uint 再移位，
        //   否则 int 的算术右移会把高位补成 1，seed 直接算错。
        uint rv = unchecked((uint)randomV);
        byte[] buf = Util.Concat(signKey, Util.Le32U(rv), signKey);
        byte[] hashV = Sm3.Sum(buf);
        uint d1 = (rv >> 16) & 255;
        uint d2 = ((d1 << 11) | (rv >> 24)) & 0xFFFFFFFF;
        d2 ^= d1 >> 5;
        d2 ^= d1;
        d2 = ~d2 & 0xFFFFFFFF;
        return (hashV, Util.Le32U(d2));
    }

    private static byte[] DeviceProto(string deviceId)
    {
        var outBuf = new List<byte>();
        outBuf.AddRange(Proto.SInt(1, 1));
        outBuf.AddRange(Proto.SInt(2, 2));
        outBuf.AddRange(Proto.String(3, Gorgon.AppId));
        outBuf.AddRange(Proto.String(4, deviceId));
        outBuf.AddRange(Proto.String(5, "Ai6svO3PyrwDOUSmO6ZcResxu"));
        outBuf.AddRange(Proto.String(6, "!noperm!"));
        outBuf.AddRange(Proto.SInt(7, -888888));
        outBuf.AddRange(Proto.SInt(8, -888888));
        outBuf.AddRange(Proto.SInt(9, 3));
        outBuf.AddRange(Proto.SInt(10, -888888));
        outBuf.AddRange(Proto.String(11, "!notset!"));
        outBuf.AddRange(Proto.String(12, "Asia/Shanghai,8"));
        outBuf.AddRange(Proto.String(13, "zh_CN"));
        outBuf.AddRange(Proto.SInt(14, 4));
        outBuf.AddRange(Proto.Float(16, 255.24993896484375f));
        outBuf.AddRange(Proto.Float(17, 35.58599090576172f));
        outBuf.AddRange(Proto.Float(18, 3.467449188232422f));
        outBuf.AddRange(Proto.Float(19, 3.467449188232422f));
        outBuf.AddRange(Proto.Float(20, 255.1754913330078f));
        outBuf.AddRange(Proto.Float(21, 42.17544174194336f));
        outBuf.AddRange(Proto.String(22, "16"));
        outBuf.AddRange(Proto.SInt(23, 41));
        outBuf.AddRange(Proto.SInt(24, 36));
        outBuf.AddRange(Proto.SInt(25, 1728388016635));
        outBuf.AddRange(Proto.SInt(26, 1728388016635));
        outBuf.AddRange(Proto.SInt(27, 1728388016635));
        outBuf.AddRange(Proto.SInt(28, 1728388016637));
        outBuf.AddRange(Proto.SInt(29, -1));
        outBuf.AddRange(Proto.String(30, "25053RT47C"));
        outBuf.AddRange(Proto.String(31, "Redmi"));
        outBuf.AddRange(Proto.String(32, "25053RT47C"));
        outBuf.AddRange(Proto.String(33, "25053RT47C"));
        outBuf.AddRange(Proto.String(34, "Xiaomi"));
        outBuf.AddRange(Proto.String(35, "Redmi"));
        outBuf.AddRange(Proto.String(36, "Redmi"));
        outBuf.AddRange(Proto.SInt(38, 31));
        return outBuf.ToArray();
    }

    public static string Assemble(string rawUrl, byte[] body, int khronos,
                                  string deviceId, string versionName, MedusaEntropy ent)
    {
        byte[] bodyMd5 = body != null && body.Length > 0 ? Util.Md5(body) : new byte[16];
        int qIdx = rawUrl.IndexOf('?');
        string queryRaw = qIdx >= 0 ? rawUrl.Substring(qIdx + 1) : "";
        byte[] querySm3 = Sm3.Sum(Util.Bytes(queryRaw));
        byte[] ts = Util.Le32(khronos);
        byte[] queryBodyTs = F13.Hash(querySm3, bodyMd5, ts, khronos);

        uint check = (uint)(((querySm3[0] & 63) << 14) | 0x18000001 |
                            ((queryBodyTs[0] & 63) << 8));

        var nested = new List<byte>();
        nested.AddRange(Proto.SInt(1, 111));
        nested.AddRange(Proto.SInt(2, 10));
        nested.AddRange(Proto.SInt(3, 694367));
        nested.AddRange(Proto.SInt(5, 586952199));

        var nested13 = new List<byte>();
        nested13.AddRange(Proto.SInt(1, ent.NowSecs));
        nested13.AddRange(Proto.SInt(2, -2));
        nested13.AddRange(Proto.SInt(4, 200));

        var env = new List<byte>();
        env.AddRange(Proto.SInt(1, ent.EnvLaunch));
        env.AddRange(Proto.SInt(2, 146331399));
        env.AddRange(Proto.SInt(3, 146331396));
        env.AddRange(Proto.SInt(5, 7));
        env.AddRange(Proto.String(6, "v04.06.04.03-bugfix"));
        env.AddRange(Proto.SInt(7, ent.EnvPid));
        env.AddRange(Proto.Bytes(12, DeviceProto(deviceId)));
        env.AddRange(Proto.Bytes(13, nested13.ToArray()));
        env.AddRange(Proto.String(14, versionName));

        byte[] qh = Util.Concat(Util.Bytes(queryRaw), bodyMd5, Util.Bytes("none"));
        byte[] queryHash = Sm3.Sum(qh);

        byte[] fixed1 = Util.FromHex("f7e85ffad7d7dc3bd62ac87057cf6118");
        byte[] fixed2 = Util.FromHex("4001000000000000");

        var message = new List<byte>();
        message.AddRange(Proto.Bytes(1, fixed1));
        message.AddRange(Proto.SInt(2, 3));
        message.AddRange(Proto.SInt(3, ent.MessageRandom));
        message.AddRange(Proto.String(4, Gorgon.AppId));
        message.AddRange(Proto.String(5, deviceId));
        message.AddRange(Proto.String(6, Gorgon.ChannelId));
        message.AddRange(Proto.String(7, versionName));
        message.AddRange(Proto.String(8, "v04.06.04-ml-android"));
        message.AddRange(Proto.SInt(9, Gorgon.GorgonSdkVersion));
        message.AddRange(Proto.Bytes(10, fixed2));
        message.AddRange(Proto.SInt(12, khronos));
        message.AddRange(Proto.Bytes(13, queryBodyTs));
        message.AddRange(Proto.Bytes(14, querySm3[..6]));
        message.AddRange(Proto.Bytes(15, nested.ToArray()));
        message.AddRange(Proto.String(16, "AXYQOS6n2m60x1fVZHIrH3iol"));
        message.AddRange(Proto.SInt(17, khronos));
        message.AddRange(Proto.Bytes(19, queryHash));
        message.AddRange(Proto.String(20, "none"));
        message.AddRange(Proto.SInt(21, 312));
        message.AddRange(Proto.Bytes(23, env.ToArray()));
        message.AddRange(Proto.String(24,
            "{\"cmr\":16777216,\"cmr2\":16777216,\"un_h\":1879194040,\"vpn\":0,\"kd\":0," +
            "\"fkd\":3672518972,\"pd\":-1872573247,\"dyn\":\"\",\"do\":0,\"tk\":true}"));

        var (hashV, seed) = KeyHash(MedusaSignKey, ent.Random);
        byte[] transformed = Xmxor(message.ToArray(), hashV);
        byte[] prefixBytes = Util.Concat(fixed2, transformed);
        Array.Reverse(prefixBytes);
        for (int i = 0; i < prefixBytes.Length; i++)
            prefixBytes[i] ^= seed[(~i) & 3];

        var packed = new List<byte>();
        packed.Add(0x35);
        packed.AddRange(Util.Le32(ent.PackedRandom));
        packed.AddRange(Util.Le32((int)check));
        packed.AddRange(prefixBytes);
        packed.Add((byte)((ent.Random >> 16) & 255));
        packed.Add((byte)((ent.Random >> 24) & 255));

        byte[] encrypted = new AesV3(MedusaAesKey, khronos)
            .Encrypt(packed.ToArray(), MedusaAesIv);

        byte[] version = Util.FromHex("03000000f7e85ffad7d7dc3bd62ac87057cf6118");
        var prefix = new List<byte>();
        for (int i = 0; i < 5; i++)
        {
            // ★ Python 用 struct.unpack_from("<I") —— **小端**
            uint v = (uint)(version[i * 4] | (version[i * 4 + 1] << 8) |
                            (version[i * 4 + 2] << 16) | (version[i * 4 + 3] << 24));
            prefix.AddRange(Util.Le32((int)(v ^ (uint)khronos)));
        }

        var outBuf = new List<byte>();
        outBuf.AddRange(prefix);
        outBuf.Add((byte)(ent.Random & 255));
        outBuf.Add((byte)((ent.Random >> 8) & 255));
        outBuf.Add(0);
        outBuf.Add(1);
        outBuf.AddRange(encrypted);
        return Convert.ToBase64String(outBuf.ToArray());
    }
}

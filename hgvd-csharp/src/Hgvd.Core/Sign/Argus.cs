using System;
using System.Collections.Generic;

namespace Hgvd.Core.Sign;

/// <summary>
/// x-argus 与 x-ladon 的计算（Simon 分组密码 + AES-CBC 信封）。
///
/// ★ 对应 Python 版 <c>hgvd/sign/core.py</c> 的
///   <c>_argus_header</c> / <c>_ladon_header</c> / <c>_simon_encrypt</c> /
///   <c>_simon_key_schedule</c>。
/// </summary>
public static class Argus
{
    private static readonly byte[] ArgusSignKey = Util.FromHex(
        "fc78e0a9657a0c748ce51559903ccf03510e51d3cff232d71343e88a321c5304");
    private static readonly byte[] ArgusAesKey = Util.FromHex(
        "d31e3718288a1027baab59f146a09a9c");
    private static readonly byte[] ArgusAesIv = Util.FromHex(
        "ea180a0336ed352fcd24e4d50018ae54");
    private static readonly byte[] ArgusMask = { 0xF2, 0xF7, 0xFC, 0xFF, 0xF2, 0xF7, 0xFC, 0xFF };
    private static readonly byte[] ArgusTail = Util.FromHex("eafb2cfe8568519154");

    public const string PlaybackLicenseId = "1611921764";

    private static byte[] Pkcs7(byte[] data)
    {
        // ★★ 填充值必须是「填充长度」本身（标准 PKCS#7），
        //   不是零字节！Python 版 `hgvd/sign/core.py::_pkcs7` 用的就是
        //   `bytes([pad]) * pad`，Go 参考实现同理。
        //   ★ 2026-11-14 实测：用零填充会让 x-argus / x-ladon 与 Python
        //     逐字节不一致 —— 服务端收到后**不回任何错误、直接返回空 body**，
        //     表现出来就是「播放接口挂了」，极难定位。
        int pad = 16 - data.Length % 16;
        var outBuf = new byte[data.Length + pad];
        Buffer.BlockCopy(data, 0, outBuf, 0, data.Length);
        for (int i = data.Length; i < outBuf.Length; i++)
            outBuf[i] = (byte)pad;
        return outBuf;
    }

    private static ulong[] SimonKeySchedule()
    {
        var k = new ulong[72];
        for (int i = 0; i < 4; i++)
            k[i] = BitConverter.ToUInt64(ArgusSignKey, i * 8);
        ulong z = 0x3DC94C3A046D678B;
        for (int i = 4; i < 72; i++)
        {
            ulong x = Util.Ror64(k[i - 1], 3) ^ k[i - 3];
            x ^= Util.Ror64(x, 1);
            k[i] = (~k[i - 4] & 0xFFFFFFFFFFFFFFFFUL) ^ x ^
                   ((z >> ((i - 4) % 62)) & 1) ^ 3;
        }
        return k;
    }

    private static void SimonEncrypt(byte[] plain)
    {
        ulong[] keys = SimonKeySchedule();
        for (int at = 0; at < plain.Length; at += 16)
        {
            ulong a = BitConverter.ToUInt64(plain, at);
            ulong b = BitConverter.ToUInt64(plain, at + 8);
            foreach (ulong rk in keys)
            {
                ulong na = b;
                ulong nb = (a ^ (Util.Rol64(b, 1) & Util.Rol64(b, 8)) ^ Util.Rol64(b, 2) ^ rk)
                           & 0xFFFFFFFFFFFFFFFFUL;
                a = na;
                b = nb;
            }
            Buffer.BlockCopy(BitConverter.GetBytes(a), 0, plain, at, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(b), 0, plain, at + 8, 8);
        }
    }

    private static string QueryValue(string query, string name)
    {
        foreach (string part in query.Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string k = part.Substring(0, eq);
            if (k != name) continue;
            return Uri.UnescapeDataString(part.Substring(eq + 1));
        }
        return "";
    }

    /// <summary>
    /// 计算 x-argus 头值（Base64）。
    /// </summary>
    /// <param name="nonce">
    /// 32 位随机数。★ 必须是 **uint**（完整 32 位）——
    /// Python 版用 <c>random.randint(0, 0xFFFFFFFF)</c>，
    /// 早期 C# 版用 <c>int</c> + <c>rng.Next(0, int.MaxValue)</c>，
    /// 只能取到 31 位，与 Python 的取值域不一致。
    /// </param>
    public static string Header(string query, byte[] bodyMd5, int ts, uint nonce,
                               string aid, string deviceId, string installId)
    {
        byte[] bodyHash = Sm3.Sum(bodyMd5);
        byte[] queryBytes = string.IsNullOrEmpty(query) ? new byte[16] : Util.Bytes(query);
        byte[] queryHash = Sm3.Sum(queryBytes);

        var counters = new List<byte>();
        counters.AddRange(Proto.VarintField(1, 1));
        counters.AddRange(Proto.VarintField(2, 1));
        counters.AddRange(Proto.VarintField(3, 1));
        counters.AddRange(Proto.VarintField(7, 0xC792ECCC));

        var device = new List<byte>();
        device.AddRange(Proto.Bytes(1, Util.Bytes("NX551J")));
        device.AddRange(Proto.VarintField(2, 8196));
        device.AddRange(Proto.VarintField(4, 0x80E0D800));

        var bean = new List<byte>();
        bean.AddRange(Proto.VarintField(1, 1077940818));
        bean.AddRange(Proto.VarintField(2, 2));
        bean.AddRange(Proto.VarintField(3, nonce));
        bean.AddRange(Proto.Bytes(4, Util.Bytes(aid)));
        bean.AddRange(Proto.Bytes(5, Util.Bytes(deviceId)));
        bean.AddRange(Proto.Bytes(6, Util.Bytes(PlaybackLicenseId)));
        bean.AddRange(Proto.Bytes(7, Util.Bytes(QueryValue(query, "version_name"))));
        bean.AddRange(Proto.Bytes(8, Util.Bytes("v04.04.05-ov-android")));
        bean.AddRange(Proto.VarintField(9, 134744640));
        bean.AddRange(Proto.Bytes(10, new byte[8]));
        bean.AddRange(Proto.VarintField(11, 0));
        bean.AddRange(Proto.VarintField(12, (ulong)((long)ts << 1)));
        bean.AddRange(Proto.Bytes(13, bodyHash[..6]));
        bean.AddRange(Proto.Bytes(14, queryHash[..6]));
        bean.AddRange(Proto.Bytes(15, counters.ToArray()));
        bean.AddRange(Proto.Bytes(16, Util.Bytes(installId)));
        bean.AddRange(Proto.Bytes(20, Util.Bytes("none")));
        bean.AddRange(Proto.VarintField(21, 738));
        bean.AddRange(Proto.Bytes(23, device.ToArray()));
        bean.AddRange(Proto.VarintField(25, 2));

        byte[] plain = Pkcs7(bean.ToArray());
        SimonEncrypt(plain);

        var frame = new List<byte>();
        frame.AddRange(ArgusMask);
        frame.AddRange(plain);
        frame.AddRange(ArgusTail);
        byte[] frameBytes = frame.ToArray();
        for (int i = ArgusMask.Length; i < frameBytes.Length; i++)
            frameBytes[i] ^= ArgusMask[i % ArgusMask.Length];
        Array.Reverse(frameBytes);

        byte[] padded = Pkcs7(Util.Concat(frameBytes, Util.Bytes("ao")));
        byte[] enc = AesCbc.Encrypt(padded, ArgusAesKey, ArgusAesIv);
        return Convert.ToBase64String(Util.Concat(new byte[] { 0xF2, 0x81 }, enc));
    }

    /// <summary>
    /// 诊断用：导出 argus 计算的各个中间阶段（hex）。
    /// ★ 用来与 Python 版逐阶段比对 —— 实测 C# 曾经因为
    ///   PKCS#7 填充值用错（零填充 vs 填充长度）导致整条签名对不上。
    /// </summary>
    public static string[] Stages(string query, byte[] bodyMd5, int ts, uint nonce,
                                   string aid, string deviceId, string installId)
    {
        byte[] bodyHash = Sm3.Sum(bodyMd5);
        byte[] queryBytes = string.IsNullOrEmpty(query) ? new byte[16] : Util.Bytes(query);
        byte[] queryHash = Sm3.Sum(queryBytes);

        var counters = new List<byte>();
        counters.AddRange(Proto.VarintField(1, 1));
        counters.AddRange(Proto.VarintField(2, 1));
        counters.AddRange(Proto.VarintField(3, 1));
        counters.AddRange(Proto.VarintField(7, 0xC792ECCC));

        var device = new List<byte>();
        device.AddRange(Proto.Bytes(1, Util.Bytes("NX551J")));
        device.AddRange(Proto.VarintField(2, 8196));
        device.AddRange(Proto.VarintField(4, 0x80E0D800));

        var bean = new List<byte>();
        bean.AddRange(Proto.VarintField(1, 1077940818));
        bean.AddRange(Proto.VarintField(2, 2));
        bean.AddRange(Proto.VarintField(3, nonce));
        bean.AddRange(Proto.Bytes(4, Util.Bytes(aid)));
        bean.AddRange(Proto.Bytes(5, Util.Bytes(deviceId)));
        bean.AddRange(Proto.Bytes(6, Util.Bytes(PlaybackLicenseId)));
        bean.AddRange(Proto.Bytes(7, Util.Bytes(QueryValue(query, "version_name"))));
        bean.AddRange(Proto.Bytes(8, Util.Bytes("v04.04.05-ov-android")));
        bean.AddRange(Proto.VarintField(9, 134744640));
        bean.AddRange(Proto.Bytes(10, new byte[8]));
        bean.AddRange(Proto.VarintField(11, 0));
        bean.AddRange(Proto.VarintField(12, (ulong)((long)ts << 1)));
        bean.AddRange(Proto.Bytes(13, bodyHash[..6]));
        bean.AddRange(Proto.Bytes(14, queryHash[..6]));
        bean.AddRange(Proto.Bytes(15, counters.ToArray()));
        bean.AddRange(Proto.Bytes(16, Util.Bytes(installId)));
        bean.AddRange(Proto.Bytes(20, Util.Bytes("none")));
        bean.AddRange(Proto.VarintField(21, 738));
        bean.AddRange(Proto.Bytes(23, device.ToArray()));
        bean.AddRange(Proto.VarintField(25, 2));

        byte[] beanBytes = bean.ToArray();
        byte[] plain = Pkcs7(beanBytes);
        byte[] plainCopy = (byte[])plain.Clone();
        SimonEncrypt(plain);

        var frame = new List<byte>();
        frame.AddRange(ArgusMask);
        frame.AddRange(plain);
        frame.AddRange(ArgusTail);
        byte[] frameBytes = frame.ToArray();
        for (int i = ArgusMask.Length; i < frameBytes.Length; i++)
            frameBytes[i] ^= ArgusMask[i % ArgusMask.Length];
        Array.Reverse(frameBytes);

        byte[] padded = Pkcs7(Util.Concat(frameBytes, Util.Bytes("ao")));
        byte[] enc = AesCbc.Encrypt(padded, ArgusAesKey, ArgusAesIv);

        return new[]
        {
            "bodyHash=" + Util.ToHex(bodyHash),
            "queryHash=" + Util.ToHex(queryHash),
            "counters=" + Util.ToHex(counters.ToArray()),
            "device=" + Util.ToHex(device.ToArray()),
            "bean=" + Util.ToHex(beanBytes),
            "pkcs7=" + Util.ToHex(plainCopy),
            "simon=" + Util.ToHex(plain),
            "frame=" + Util.ToHex(frameBytes),
            "padded=" + Util.ToHex(padded),
            "enc=" + Util.ToHex(enc),
        };
    }

    /// <summary>计算 x-ladon 头值（Base64）。</summary>
    public static string LadonHeader(int ts, uint nonce, string aid)
    {
        byte[] seed = Util.Le32U(nonce);
        byte[] digest = Util.Md5(Util.Concat(seed, Util.Bytes(aid)));
        string hexKey = Util.ToHex(digest);

        ulong k = BitConverter.ToUInt64(Util.Bytes(hexKey.Substring(0, 8)), 0);
        var pending = new List<ulong>
        {
            BitConverter.ToUInt64(Util.Bytes(hexKey.Substring(8, 8)), 0),
            BitConverter.ToUInt64(Util.Bytes(hexKey.Substring(16, 8)), 0),
            BitConverter.ToUInt64(Util.Bytes(hexKey.Substring(24, 8)), 0),
        };

        var keys = new List<ulong>();
        for (int i = 0; i < 34; i++)
        {
            keys.Add(k);
            ulong x = (Util.Ror64(pending[0], 8) + k) & 0xFFFFFFFFFFFFFFFFUL;
            x ^= (ulong)i;
            pending = new List<ulong> { pending[1], pending[2], x };
            k = x ^ Util.Rol64(k, 3);
        }

        byte[] plain = Pkcs7(Util.Bytes($"{ts}-{PlaybackLicenseId}-{aid}"));
        for (int at = 0; at < plain.Length; at += 16)
        {
            ulong a = BitConverter.ToUInt64(plain, at);
            ulong b = BitConverter.ToUInt64(plain, at + 8);
            foreach (ulong rk in keys)
            {
                b = rk ^ ((a + Util.Ror64(b, 8)) & 0xFFFFFFFFFFFFFFFFUL);
                a = b ^ Util.Rol64(a, 3);
            }
            Buffer.BlockCopy(BitConverter.GetBytes(a), 0, plain, at, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(b), 0, plain, at + 8, 8);
        }

        return Convert.ToBase64String(Util.Concat(seed, plain));
    }
}

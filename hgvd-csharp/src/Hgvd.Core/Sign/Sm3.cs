using System;

namespace Hgvd.Core.Sign;

/// <summary>
/// SM3 哈希（GM/T 0004-2012）。
///
/// ★ 抖音系签名链（x-gorgon / x-helios / x-medusa / x-argus）都依赖 SM3，
/// .NET 内置没有，必须自己实现。
/// ★ 对应 Python 版 <c>hgvd/sign/sm3.py</c>（逐行对照移植）。
/// </summary>
public static class Sm3
{
    private const uint Mask = 0xFFFFFFFF;

    private static uint Rol(uint x, int n)
    {
        n &= 31;
        if (n == 0) return x;
        return ((x << n) | (x >> (32 - n))) & Mask;
    }

    private static uint P0(uint x) => x ^ Rol(x, 9) ^ Rol(x, 17);
    private static uint P1(uint x) => x ^ Rol(x, 15) ^ Rol(x, 23);

    private static uint FF(int j, uint x, uint y, uint z) =>
        j < 16 ? (x ^ y ^ z) : ((x & y) | (x & z) | (y & z));

    private static uint GG(int j, uint x, uint y, uint z) =>
        j < 16 ? (x ^ y ^ z) : ((x & y) | ((~x & Mask) & z));

    /// <summary>SM3 摘要，32 字节。</summary>
    /// <remarks>
    /// ★ <c>"abc"</c> 的结果应为
    /// <c>66c7f0f462eeedd9d1f2d46bdc10e4e24167c4875cf2f7a2297da02b8f4ba8e0</c>。
    /// </remarks>
    public static byte[] Sum(byte[] msg)
    {
        uint[] h =
        {
            0x7380166F, 0x4914B2B9, 0x172442D7, 0xDA8A0600,
            0xA96F30BC, 0x163138AA, 0xE38DEE4D, 0xB0FB0E4E,
        };

        byte[] data = Pad(msg);
        uint[] w = new uint[68];
        uint[] wp = new uint[64];

        for (int off = 0; off < data.Length; off += 64)
        {
            for (int i = 0; i < 16; i++)
                w[i] = ReadU32Be(data, off + i * 4);

            for (int j = 16; j < 68; j++)
            {
                uint x = w[j - 16] ^ w[j - 9] ^ Rol(w[j - 3], 15);
                w[j] = P1(x) ^ Rol(w[j - 13], 7) ^ w[j - 6];
            }
            for (int j = 0; j < 64; j++)
                wp[j] = w[j] ^ w[j + 4];

            uint a = h[0], b = h[1], c = h[2], d = h[3];
            uint e = h[4], f = h[5], g = h[6], hh = h[7];

            for (int j = 0; j < 64; j++)
            {
                uint tj = j < 16 ? 0x79CC4519u : 0x7A879D8Au;
                uint ss1 = Rol((Rol(a, 12) + e + Rol(tj, j)) & Mask, 7);
                uint ss2 = ss1 ^ Rol(a, 12);
                uint tt1 = (FF(j, a, b, c) + d + ss2 + wp[j]) & Mask;
                uint tt2 = (GG(j, e, f, g) + hh + ss1 + w[j]) & Mask;

                d = c;
                c = Rol(b, 9);
                b = a;
                a = tt1;
                hh = g;
                g = Rol(f, 19);
                f = e;
                e = P0(tt2);
            }

            h[0] ^= a; h[1] ^= b; h[2] ^= c; h[3] ^= d;
            h[4] ^= e; h[5] ^= f; h[6] ^= g; h[7] ^= hh;
        }

        var result = new byte[32];
        for (int i = 0; i < 8; i++)
            WriteU32Be(result, i * 4, h[i] & Mask);
        return result;
    }

    public static string Hex(byte[] msg) => Util.ToHex(Sum(msg));

    private static byte[] Pad(byte[] msg)
    {
        ulong bitLen = (ulong)msg.Length * 8;
        var outBuf = new byte[msg.Length + 1];
        Buffer.BlockCopy(msg, 0, outBuf, 0, msg.Length);
        outBuf[msg.Length] = 0x80;

        int rem = (msg.Length + 1) % 64;
        int zeros = rem <= 56 ? 56 - rem : 120 - rem;
        var data = new byte[msg.Length + 1 + zeros + 8];
        Buffer.BlockCopy(outBuf, 0, data, 0, outBuf.Length);
        WriteU64Be(data, data.Length - 8, bitLen);
        return data;
    }

    internal static uint ReadU32Be(byte[] b, int off) =>
        ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) |
        ((uint)b[off + 2] << 8) | b[off + 3];

    internal static void WriteU32Be(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24);
        b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8);
        b[off + 3] = (byte)v;
    }

    internal static void WriteU64Be(byte[] b, int off, ulong v)
    {
        for (int i = 0; i < 8; i++)
            b[off + i] = (byte)(v >> (56 - 8 * i));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hgvd.Core.Sign;

/// <summary>签名层的通用小工具。</summary>
public static class Util
{
    private const string HexDigits = "0123456789abcdef";

    public static string ToHex(byte[] data)
    {
        if (data == null) return "";
        var sb = new StringBuilder(data.Length * 2);
        foreach (byte b in data)
        {
            sb.Append(HexDigits[b >> 4]);
            sb.Append(HexDigits[b & 0x0F]);
        }
        return sb.ToString();
    }

    public static byte[] FromHex(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        hex = hex.Trim();
        if (hex.Length % 2 != 0) throw new ArgumentException("hex 长度必须是偶数");
        var outBuf = new byte[hex.Length / 2];
        for (int i = 0; i < outBuf.Length; i++)
            outBuf[i] = (byte)((HexVal(hex[i * 2]) << 4) | HexVal(hex[i * 2 + 1]));
        return outBuf;
    }

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        throw new ArgumentException($"非法 hex 字符 '{c}'");
    }

    public static string Base64(byte[] data) => Convert.ToBase64String(data);

    public static byte[] UnBase64(string s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
        // 容忍 URL-safe 与换行
        s = s.Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "")
             .Replace(" ", "");
        int pad = s.Length % 4;
        if (pad == 2) s += "==";
        else if (pad == 3) s += "=";
        return Convert.FromBase64String(s);
    }

    /// <summary>字节序反转（reverse bits within each byte）。</summary>
    public static byte[] ReverseBits(byte[] data)
    {
        var outBuf = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            byte v = data[i];
            byte r = 0;
            for (int b = 0; b < 8; b++)
                if ((v & (1 << b)) != 0) r |= (byte)(1 << (7 - b));
            outBuf[i] = r;
        }
        return outBuf;
    }

    /// <summary>UTF-8 字节数组 → 字符串。</summary>
    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);

    /// <summary>字符串 → UTF-8 字节数组。</summary>
    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    public static string Md5Hex(byte[] data)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return ToHex(md5.ComputeHash(data));
    }

    public static byte[] Md5(byte[] data)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return md5.ComputeHash(data);
    }

    /// <summary>把 long 按小端写成 8 字节。</summary>
    public static byte[] Le64(long v)
    {
        var b = new byte[8];
        for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (8 * i));
        return b;
    }

    /// <summary>把 int 按小端写成 4 字节。</summary>
    /// <remarks>
    /// ★ 必须用**逻辑右移**（>>>）—— 传进来的值常是无符号语义
    ///   （如 0xfffa1fdc），(int) 转换后是负数，算术右移会把高位补成 1，
    ///   结果就变成 dc000000 而不是 dc1ffaff。
    /// </remarks>
    public static byte[] Le32(int v) => Le32U((uint)v);

    /// <summary>把 uint 按小端写成 4 字节（逻辑右移）。</summary>
    public static byte[] Le32U(uint v)
    {
        var b = new byte[4];
        for (int i = 0; i < 4; i++) b[i] = (byte)(v >> (8 * i));
        return b;
    }

    /// <summary>把 int 按大端写成 4 字节。</summary>
    public static byte[] Be32(int v)
    {
        var b = new byte[4];
        b[0] = (byte)(v >> 24); b[1] = (byte)(v >> 16);
        b[2] = (byte)(v >> 8);  b[3] = (byte)v;
        return b;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p?.Length ?? 0;
        var outBuf = new byte[total];
        int off = 0;
        foreach (var p in parts)
        {
            if (p == null || p.Length == 0) continue;
            Buffer.BlockCopy(p, 0, outBuf, off, p.Length);
            off += p.Length;
        }
        return outBuf;
    }

    public static byte[] Xor(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        var outBuf = new byte[n];
        for (int i = 0; i < n; i++) outBuf[i] = (byte)(a[i] ^ b[i]);
        return outBuf;
    }

    /// <summary>无符号右旋 32 位。</summary>
    public static uint Ror32(uint v, int n)
    {
        n &= 31;
        return n == 0 ? v : ((v >> n) | (v << (32 - n)));
    }

    /// <summary>无符号左旋 32 位。</summary>
    public static uint Rol32(uint v, int n)
    {
        n &= 31;
        return n == 0 ? v : ((v << n) | (v >> (32 - n)));
    }

    /// <summary>无符号右旋 64 位。</summary>
    public static ulong Ror64(ulong v, int n)
    {
        n &= 63;
        return n == 0 ? v : ((v >> n) | (v << (64 - n)));
    }

    /// <summary>无符号左旋 64 位。</summary>
    public static ulong Rol64(ulong v, int n)
    {
        n &= 63;
        return n == 0 ? v : ((v << n) | (v >> (64 - n)));
    }
}

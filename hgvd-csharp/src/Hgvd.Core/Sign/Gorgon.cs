using System;
using System.Collections.Generic;

namespace Hgvd.Core.Sign;

/// <summary>
/// x-gorgon 与 x-helios 的计算。
///
/// ★ 对应 Python 版 <c>hgvd/sign/core.py</c> 的
///   <c>_x_gorgon</c> / <c>_rc4_gorgon</c> / <c>_assemble_helios</c> / <c>_diffuse</c>。
/// ★ 移植后已用黄金向量逐字节比对通过（见 Hgvd.Tests）。
/// </summary>
public static class Gorgon
{
    public const string AppId = "8662";
    public const string ChannelId = "1588093228";
    public const int GorgonSdkVersion = 67503104;

    /// <summary>
    /// gorgon 用的 RC4 变体。
    /// ★★★ 注意：KSA 是**单向赋值** <c>s[i] = s[j]</c>，不是标准 RC4 的 <c>s[i],s[j] = s[j],s[i]</c>。
    ///   写成交换只会让中间 8 字节不匹配，服务端直接拒签。
    /// </summary>
    private static byte[] Rc4Gorgon(byte[] data, byte[] key)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            s[i] = s[j];   // ★ 单向赋值
        }

        var outBuf = new byte[data.Length];
        int ii = 0, jj = 0;
        for (int k = 0; k < data.Length; k++)
        {
            ii += 1;
            byte x = s[ii & 255];
            jj = (jj + x) & 255;
            byte y = s[jj & 255];
            s[ii & 255] = y;
            outBuf[k] = (byte)(data[k] ^ s[(y + y) & 255]);
        }
        return outBuf;
    }

    /// <summary>计算 x-gorgon 头值（小写十六进制）。</summary>
    /// <param name="query">完整的查询串（不含前导 ?）。</param>
    /// <param name="body">请求体；null 表示空体（此时 body MD5 按 16 个 0 处理）。</param>
    /// <param name="khronos">秒级时间戳。</param>
    /// <param name="randomV">16 位随机数，会原样嵌进结果的头两字节。</param>
    public static string XGorgon(string query, byte[] body, int khronos, int randomV)
    {
        byte[] queryMd5 = Util.Md5(Util.Bytes(query));
        byte[] bodyMd5 = body != null && body.Length > 0
            ? Util.Md5(body)
            : new byte[16];

        byte[] inp = Util.Concat(
            queryMd5[..4],
            bodyMd5[..4],
            new byte[4],
            Util.Le32(GorgonSdkVersion),
            Util.Be32(khronos));

        byte[] key =
        {
            0x4A, 0x40, 0x16, (byte)((randomV >> 8) & 0xFF),
            0x47, 0x6C, 0x01, (byte)(randomV & 0xFF),
        };

        byte[] outBuf = Rc4Gorgon(inp, key);
        int n = outBuf.Length;
        for (int i = 0; i < n; i++)
        {
            byte value = outBuf[i];
            byte swapped = (byte)(((value >> 4) | (value << 4)) & 255);
            byte nxt = (i + 1 >= n) ? outBuf[0] : outBuf[i + 1];
            outBuf[i] = (byte)((~(ReverseByte((byte)(nxt ^ swapped)) ^ 20)) & 255);
        }

        var result = new byte[6 + n];
        result[0] = 0x84;
        result[1] = 0x04;
        result[2] = (byte)(randomV & 255);
        result[3] = (byte)((randomV >> 8) & 255);
        result[4] = 0x40;
        result[5] = 0x01;
        Buffer.BlockCopy(outBuf, 0, result, 6, n);
        return Util.ToHex(result);
    }

    private static byte ReverseByte(byte b)
    {
        byte r = 0;
        for (int i = 0; i < 8; i++)
            if ((b & (1 << i)) != 0) r |= (byte)(1 << (7 - i));
        return r;
    }

    // ---------------------------------------------------------------- Helios

    private static ulong[] Diffuse(ulong[] words)
    {
        var table = new List<ulong> { words[0] };
        ulong b0 = words[0];
        ulong b8 = words[1];
        var queue = new List<ulong>(words[2..]);

        for (int i = 0; i < 34; i++)
        {
            ulong x8 = (Util.Ror64(b8, 8) + b0) & 0xFFFFFFFFFFFFFFFFUL;
            x8 ^= (ulong)i;
            queue.Add(x8);
            x8 ^= Util.Ror64(b0, 61);
            table.Add(x8);
            b0 = x8;
            b8 = queue[0];
            queue.RemoveAt(0);
        }
        return table.ToArray();
    }

    /// <summary>计算 x-helios 头值（Base64）。</summary>
    public static string AssembleHelios(int khronos, int randomV)
    {
        byte[] seed = Util.Concat(Util.Le32(randomV), Util.Bytes(AppId));
        byte[] digest = Util.Md5(seed);
        string asciiHex = Util.ToHex(digest);

        var words = new ulong[4];
        for (int i = 0; i < 4; i++)
            words[i] = BitConverter.ToUInt64(Util.Bytes(asciiHex.Substring(i * 8, 8)), 0);

        ulong[] table = Diffuse(words);

        byte[] text = Util.Bytes($"{khronos}-{ChannelId}-{AppId}");
        int fill = 16 - text.Length % 16;
        int size = (text.Length + 1 + 15) / 16 * 16;
        var padded = new byte[size];
        for (int i = 0; i < size; i++) padded[i] = (byte)fill;
        Buffer.BlockCopy(text, 0, padded, 0, text.Length);

        var output = new List<byte>();
        for (int off = 0; off < padded.Length; off += 16)
        {
            ulong a = BitConverter.ToUInt64(padded, off);
            ulong b = BitConverter.ToUInt64(padded, off + 8);
            for (int t = 0; t < 34; t++)
            {
                b = table[t] ^ ((a + Util.Ror64(b, 8)) & 0xFFFFFFFFFFFFFFFFUL);
                a = b ^ Util.Ror64(a, 61);
            }
            output.AddRange(BitConverter.GetBytes(a));
            output.AddRange(BitConverter.GetBytes(b));
        }

        return Convert.ToBase64String(Util.Concat(Util.Le32(randomV), output.ToArray()));
    }
}

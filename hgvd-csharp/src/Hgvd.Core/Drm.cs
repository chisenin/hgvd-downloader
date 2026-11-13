using System;
using System.Text;

namespace Hgvd.Core;

/// <summary>
/// CENC 内容密钥解包。
///
/// ★ 服务端在 <c>encrypt_info.spade_a</c> 里下发一份被包裹的密钥材料，
///   播放器按固定规则解开，得到 AES 内容密钥（hex）。
/// ★ 密钥是**本地计算**的，不需要额外请求。
/// ★ 加密方式是 CENC AES-CTR，同一集的所有清晰度共用一个内容密钥，
///   下载后用 <c>ffmpeg -decryption_key &lt;hex&gt;</c> 解密。
/// ★ 对应 Python 版 <c>hgvd/drm.py</c>。
/// </summary>
public static class Drm
{
    private static int Popcount(uint v)
    {
        int n = 0;
        while (v != 0) { v &= v - 1; n++; }
        return n;
    }

    private static int Popcount32(uint v) =>
        Popcount(v & 0xFF) + Popcount((v >> 8) & 0xFF) +
        Popcount((v >> 16) & 0xFF) + Popcount((v >> 24) & 0xFF);

    private static int HexDigit(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    /// <summary>播放器的字节置换。</summary>
    /// <remarks>
    /// ★ v2（<c>app_v2</c> / <c>web_v2</c> 家族）先两两交换相邻字节，
    ///   再做「减去前一个滚动常量」的变体。
    /// </remarks>
    private static void Scramble(byte[] buf, bool v2)
    {
        int len = buf.Length;
        if (v2)
        {
            int last = len - 1;
            for (int i = 0; i + 1 < len; i += 2)
            {
                (buf[i], buf[i + 1]) = (buf[i + 1], buf[i]);
                if (i == last - 2) break;
            }
        }

        byte prev = 0x55, cur = 0xFA;
        for (int i = 0; i < len; i++)
        {
            byte b = buf[i];
            byte term = (byte)(-(0x15 + Popcount32((uint)i)) & 0xFF);
            if ((i & 1) == 0)
            {
                buf[i] = v2
                    ? (byte)(((cur ^ b) - prev + term) & 0xFF)
                    : (byte)((term + (cur ^ b)) & 0xFF);
                cur = b;
            }
            else
            {
                buf[i] = v2
                    ? (byte)(((prev ^ b) - prev + term) & 0xFF)
                    : (byte)((term + (prev ^ b)) & 0xFF);
                prev = b;
            }
        }
    }

    /// <summary>从 <c>spade_a</c> 解出内容密钥（hex）。失败返回空串。</summary>
    public static string DeriveContentKey(string spadeA)
    {
        if (string.IsNullOrWhiteSpace(spadeA)) return "";
        byte[] raw;
        try { raw = Convert.FromBase64String(spadeA.Trim()); }
        catch { return ""; }
        if (raw.Length < 4) return "";

        int marker = raw[0] ^ raw[1] ^ raw[2];
        int prefixLen = marker - 0x30;
        if (prefixLen < 1 || prefixLen > raw.Length) return "";
        int outLen = raw.Length - marker + 0x2F;
        if (outLen < 2 || 1 + outLen > raw.Length) return "";

        var buf = new byte[outLen];
        Array.Copy(raw, 1, buf, 0, outLen);

        int tail = raw.Length - prefixLen;
        if (tail - 2 < 0 || tail > raw.Length) return "";
        byte b16 = raw[tail - 2], b14 = raw[tail - 1];
        var tag = new byte[prefixLen];
        for (int i = 0; i < prefixLen; i++)
        {
            if (i + tail >= raw.Length) return "";
            tag[i] = (byte)(b14 ^ b16 ^ raw[i + tail]);
        }
        string tagText = Encoding.Latin1.GetString(tag);
        bool v2 = tagText.StartsWith("app_v2") || tagText.StartsWith("web_v2");
        Scramble(buf, v2);

        int versionLen = HexDigit((char)buf[0]);
        if (versionLen < 1 || versionLen >= outLen) return "";
        int keyLen = outLen - versionLen;
        string hexKey = Encoding.Latin1.GetString(buf, 1, keyLen);
        if (string.IsNullOrEmpty(hexKey) || hexKey.Length % 2 != 0) return "";
        foreach (char ch in hexKey)
            if (HexDigit(ch) < 0) return "";
        return hexKey.ToLowerInvariant();
    }
}

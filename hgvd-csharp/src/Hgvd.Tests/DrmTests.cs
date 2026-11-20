using System;
using Hgvd.Core;

namespace Hgvd.Tests;

/// <summary>
/// CENC 内容密钥解包回归（纯离线，不联网）。
///
/// ★★ 为什么要有这个测试：
///   2026-11-21 用户反馈「教学类能下、短剧下不了」。
///   根因在 <see cref="Drm.DeriveContentKey"/> 的切片长度：
///     <code>
///     Python: hex_key = bytes(buf[1:key_len])          ← 长度 key_len-1
///     C#    : Encoding.Latin1.GetString(buf, 1, keyLen) ← count 多了 1
///     </code>
///   16 字节的 AES key 于是被取成 33 个字符（奇数），
///   紧接着 <c>hexKey.Length % 2 != 0</c> 直接判失败 ⇒ <c>ContentKey</c> 恒为空
///   ⇒ 所有 CENC 加密内容（短剧）下载后都不解密，成品 ffprobe 读不出时长，
///     最后报「成品无法解析（可能损坏）」。
///   教学类内容 <c>encrypt=false</c>、<c>spade_a</c> 为空，根本不走这条路径，
///   所以这个 bug 藏了很久。
///
/// ★ 黄金向量取自真实接口。同一集的 <c>spade_a</c> 与内容密钥是稳定的一一对应
///   （按内容加密，不随请求变化），所以适合当离线断言。
/// </summary>
public static class DrmTests
{
    private static int _fail;

    private static void Ok(string name, bool cond, string detail = "")
    {
        if (cond) { Console.WriteLine($"  ✓ {name}"); }
        else
        {
            _fail++;
            Console.WriteLine($"  ✗ {name}");
            if (detail.Length > 0) Console.WriteLine($"      {detail}");
        }
    }

    /// <summary>真实短剧分集（vid=7694909188752821273）的密钥材料。</summary>
    private const string Spade = "o7wt8GK4G/Jlixj3Vb0o9Hq6BvN6uyrBeIo0xkqON/NntC+qqg==";

    /// <summary>上面对应的 16 字节 AES 内容密钥（hex）。</summary>
    private const string ExpectKey = "b682b3fcfe53e198e2d18b9444e0dd61";

    public static int Run()
    {
        _fail = 0;
        Console.WriteLine("\n[DRM] CENC 内容密钥解包");

        string got = Drm.DeriveContentKey(Spade);

        // ★★ 这条直接守住「多切一字节」那个 bug：
        //    切片错位时长度会变成 33（奇数），实现里 `% 2 != 0` 会直接返回空串。
        Ok("真实 spade_a → 32 个 hex 字符", got.Length == 32, $"实际长度 {got.Length}：{got}");
        Ok("解出的密钥与黄金向量一致", got == ExpectKey, $"期望 {ExpectKey}，实际 {got}");
        Ok("密钥是纯小写 hex", got.Length == 32 && IsLowerHex(got));

        Ok("空输入返回空", Drm.DeriveContentKey("") == "");
        Ok("纯空白返回空", Drm.DeriveContentKey("   ") == "");
        Ok("非法 base64 返回空", Drm.DeriveContentKey("!!!not-base64!!!") == "");

        Console.WriteLine($"  —— 通过 {6 - _fail}，失败 {_fail}");
        return _fail;
    }

    private static bool IsLowerHex(string s)
    {
        foreach (char c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }
}

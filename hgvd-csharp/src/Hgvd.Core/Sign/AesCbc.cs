using System;
using System.Security.Cryptography;

namespace Hgvd.Core.Sign;

/// <summary>
/// 标准 AES-128-CBC 加密（Argus 信封用）。
///
/// ★ 与 <see cref="AesV3"/> 不同 —— 这里是**标准** AES-128。
/// ★ .NET 内置 <see cref="Aes"/>，不需要像 Python 版那样写纯 C# 兜底。
/// </summary>
public static class AesCbc
{
    /// <summary>AES-128-CBC 加密。数据长度不足 16 的倍数时按 PKCS#7 补齐。</summary>
    public static byte[] Encrypt(byte[] data, byte[] key, byte[] iv)
    {
        if (key == null || key.Length != 16)
            throw new ArgumentException("AES-128 的 key 必须是 16 字节");
        if (iv == null || iv.Length != 16)
            throw new ArgumentException("iv 必须是 16 字节");

        byte[] input = data;
        if (input.Length % 16 != 0)
            input = PadPkcs7(input);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(input, 0, input.Length);
    }

    /// <summary>AES-128-CBC 解密（对称，用于回环自测）。</summary>
    public static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
    {
        if (data == null || data.Length % 16 != 0)
            throw new ArgumentException("密文长度必须是 16 的倍数");
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] PadPkcs7(byte[] data)
    {
        int pad = 16 - data.Length % 16;
        var outBuf = new byte[data.Length + pad];
        Buffer.BlockCopy(data, 0, outBuf, 0, data.Length);
        for (int i = data.Length; i < outBuf.Length; i++)
            outBuf[i] = (byte)pad;
        return outBuf;
    }
}

using System;
using System.Collections.Generic;

namespace Hgvd.Core.Sign;

/// <summary>
/// AES-V3 变体。
///
/// ★ 这**不是**标准 AES —— S 盒、轮常量、行移位顺序都按字节重排，
///   且只有 2 轮混合（标准 AES 是 9/13/14 轮）。
/// ★ medusa 头必需。
/// ★ 对应 Python 版 <c>hgvd/sign/aesv3.py</c>（逐行对照移植）。
/// </summary>
public sealed class AesV3
{
    private const string SboxB64 =
        "+n0Ia5xZs0sEXznQOEqRmQBnpiCf9U2Ccybu3xhmgzOAAxn72f6uqqmwUsYL83klTni0NqxdG" +
        "ieeiNu9PGPsSRXBMB/cuFbUbM3KCUPINaPvHvSW0vwOcnuUhNHqRVpiAj/TEoE0K91+5ijypUYT" +
        "ATsh9mE3KSoN7Yyvv51cuyR2D3XkU4nhmI2xmmVwT1RMWKtub4sjxAcRDLrPoKSO2AU9FLLadMPX5" +
        "77Wf95IFj6FkKFVt3dCIsmGUC4X+WQxLJvxbRxEaOPpqJOXyzJX6+Vxaq3AzMfF/WAdoi1Hp+JRa" +
        "V56zgpBtpWP97mH4DoGEIq1+FvV8LyS/3wvwugbQOwb2r26mJEMsiuDQTRn+wrYdrVGBVlhI3WQhyr" +
        "jUBVMrLF5667llUcEaPCGPVGLD8qO5LlO8hKCvA7V9+8oJc9bXelqVQLhM76T5/WtnT45JKji+hdX0" +
        "HoNCDDWuKON/QeaxB5uImSX0h2wv0VmP2zd2yeApxHcpsVS+MC2yFwAc2B7oBkTqsk1SEvTpM2fmfM" +
        "QREBUfin0Bh+iq6EvPPavhWI2IX9e3yAas7Tm/3KEj2UmlFp36kN4x0rMLBRrxuh0U/zUHM4xcAMYjJ" +
        "Y4MonxOl/X+alpt2M3WMI7w3HLnpIBigtNiJu7T21v4P6lSd5WFgntnMEt7oF92XzRLkJbTcGmXepE" +
        "/UVOG6E/0YnhfS+q26utWcuxzpooyeD2cDlK1/8w9d28VzsRjbLuALbmGlp8+d7EzS6Au7lMpZ+ECM" +
        "ZvQmzwJ+eLOpxR+2chdUExp8ogQyq3v9l68rX4jCwjg0+PYKAEEzcU4wHFY2ZcdIHfWL1okD3SszT0" +
        "GZMyKdZJrg1L2AeerB4tC0C4crp2EHGo5FYdSP7lwkeR2ocmnR+Ia8CYviUJlzOjhRZef9xuVOn3" +
        "qcjow3fQgivsAmKKkg4+sA8F8/GWeDiGNhg8JM8KtFPMYWWkx5TVFX5t73kiNRJqjlIGVXtGZFCV4g" +
        "zt0xcDopuZ6xz8r9Rzafpf9ywev8jh8592gHFIqpStZPuJxmDDMrNN0uBE3V+oscdoIzTJbRJ/t+" +
        "sVvqnReJOgDJKk10fjisJwqyZBmnmn2BSFj8BvVtCMEbkuPOKdzw7eA11GPs04Qw8zWtkaZWwiO/w" +
        "wpojqN6K0jY5RnNZA7vn4hPSul+nKCkVnVwQvg1zVxcSCtqORmB9KrJaBbssbCQivGJVJfVTt+hYx" +
        "Otq4ZvWl8f4QAQZ0zGPffCgl9s6yT4vlvIdpu4YhBwA25wtQWZsc6GJYGWHyvSdeuh3mmUI9DSq" +
        "13Fsp8C1MU3tqc04/df9LoTUXVXI5INOw/e8C7Hd+5CvbkMEFnnrUUmskEw==";

    private static readonly int[][] AesCon2 =
    {
        new[] { 1, 0, 2, 3 }, new[] { 2, 0, 3, 1 },
        new[] { 0, 1, 3, 2 }, new[] { 1, 0, 2, 3 },
    };

    private static readonly int[][] AesOrder =
    {
        new[] { 0, 9, 14, 11, 4, 13, 2, 7, 8, 1, 6, 15, 12, 5, 10, 3 },
        new[] { 0, 9, 14, 15, 4, 13, 2, 7, 8, 1, 6, 3, 12, 5, 10, 11 },
        new[] { 0, 9, 14, 7, 4, 13, 2, 11, 8, 1, 6, 3, 12, 5, 10, 15 },
        new[] { 0, 9, 14, 11, 4, 13, 2, 7, 8, 1, 6, 15, 12, 5, 10, 3 },
    };

    private static readonly uint[] AesInitWord =
    {
        0xCA025DDC, 0x823DC546, 0xC9420583, 0xC298225F,
    };

    private static byte[] _allSbox;
    private static readonly object SboxLock = new object();

    private static byte[] AllSbox()
    {
        if (_allSbox != null) return _allSbox;
        lock (SboxLock)
        {
            _allSbox ??= Convert.FromBase64String(SboxB64);
            return _allSbox;
        }
    }

    private readonly byte[] _box;
    private readonly int[] _con2;
    private readonly int[] _order;
    private readonly byte[][] _keys;

    public AesV3(byte[] key, int khronos)
    {
        int word = khronos & 3;
        byte[] allbox = AllSbox();
        _box = new byte[256];
        Buffer.BlockCopy(allbox, word * 256, _box, 0, 256);
        _con2 = AesCon2[word];
        _order = AesOrder[word];
        _keys = ExpandAes(key, word);
    }

    private static byte[] XorBytes(byte[] a, byte[] b)
    {
        var outBuf = new byte[a.Length];
        for (int i = 0; i < outBuf.Length; i++)
            outBuf[i] = (byte)(a[i] ^ b[i % b.Length]);
        return outBuf;
    }

    private static byte[][] ExpandAes(byte[] key, int word)
    {
        uint init = AesInitWord[word];
        byte[] allbox = AllSbox();
        var box = new byte[256];
        Buffer.BlockCopy(allbox, word * 256, box, 0, 256);

        byte[] initial = new byte[16];
        for (int i = 0; i < 4; i++)
        {
            initial[i * 4 + 0] = (byte)(init & 0xFF);
            initial[i * 4 + 1] = (byte)((init >> 8) & 0xFF);
            initial[i * 4 + 2] = (byte)((init >> 16) & 0xFF);
            initial[i * 4 + 3] = (byte)((init >> 24) & 0xFF);
        }

        var expanded = new byte[16 + 32];
        byte[] xored = XorBytes(initial, key);
        Buffer.BlockCopy(xored, 0, expanded, 0, Math.Min(16, xored.Length));

        int rounds = 8;
        for (int i = 4; i < 12; i++)
        {
            int at = 4 * (i - 1);
            byte a = expanded[at], b = expanded[at + 1], c = expanded[at + 2], d = expanded[at + 3];
            if ((i & 3) == 0)
            {
                byte t = (byte)(((init >> (rounds & 24)) ^ box[b]) & 0xFF);
                b = box[c];
                c = box[d];
                d = box[a];
                a = t;
            }
            rounds += 2;
            expanded[at + 4] = (byte)(a ^ expanded[at - 12]);
            expanded[at + 5] = (byte)(b ^ expanded[at - 11]);
            expanded[at + 6] = (byte)(c ^ expanded[at - 10]);
            expanded[at + 7] = (byte)(d ^ expanded[at - 9]);
        }

        var keys = new byte[12][];
        for (int i = 0; i < 12; i++)
        {
            keys[i] = new byte[4];
            Buffer.BlockCopy(expanded, i * 4, keys[i], 0, 4);
        }
        return keys;
    }

    // ---------------------------------------------------------- 轮运算

    private void AddCon(byte[][] state, byte[][] keys)
    {
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                state[i][j] ^= keys[i][_con2[j]];
    }

    private void Sub(byte[][] state)
    {
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                state[i][j] = _box[state[i][j]];
        var old = new byte[4][];
        for (int i = 0; i < 4; i++) old[i] = (byte[])state[i].Clone();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                state[i][j] = old[_con2[i]][j];
    }

    private void Shift(byte[][] state)
    {
        var flat = new byte[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                flat[i * 4 + j] = state[i][j];
        for (int i = 0; i < 16; i++)
            state[i / 4][i % 4] = flat[_order[i]];
    }

    private void ShiftCon(byte[][] state)
    {
        var old = new byte[4][];
        for (int i = 0; i < 4; i++) old[i] = (byte[])state[i].Clone();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                state[i][j] = old[i][_con2[j]];
    }

    private static byte Xt(byte x)
    {
        int v = (x << 1) & 0xFF;
        if ((x & 0x80) != 0) v ^= 0x1B;
        return (byte)(v & 0xFF);
    }

    private static void MixColumns(byte[][] state)
    {
        for (int i = 0; i < 4; i++)
        {
            byte t = (byte)(state[0][i] ^ state[1][i] ^ state[2][i] ^ state[3][i]);
            byte u = state[0][i];
            state[0][i] ^= (byte)(t ^ Xt((byte)(state[0][i] ^ state[1][i])));
            state[1][i] ^= (byte)(t ^ Xt((byte)(state[1][i] ^ state[2][i])));
            state[2][i] ^= (byte)(t ^ Xt((byte)(state[2][i] ^ state[3][i])));
            state[3][i] ^= (byte)(t ^ Xt((byte)(state[3][i] ^ u)));
        }
    }

    private byte[] Block(byte[] value)
    {
        var state = new byte[4][];
        for (int i = 0; i < 4; i++)
        {
            state[i] = new byte[4];
            for (int j = 0; j < 4; j++)
                state[i][j] = value[i * 4 + j];
        }

        AddCon(state, new[] { _keys[0], _keys[1], _keys[2], _keys[3] });
        for (int i = 1; i <= 2; i++)
        {
            Sub(state);
            Shift(state);
            if (i == 1)
            {
                ShiftCon(state);
                MixColumns(state);
            }
            AddCon(state, new[] { _keys[i * 4], _keys[i * 4 + 1], _keys[i * 4 + 2], _keys[i * 4 + 3] });
        }
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                state[i][j] ^= _keys[4 + i][j];

        var outBuf = new byte[16];
        for (int i = 0; i < 4; i++)
            Buffer.BlockCopy(state[i], 0, outBuf, i * 4, 4);
        return outBuf;
    }

    // ---------------------------------------------------------- 对外

    public byte[] Encrypt(byte[] data, byte[] iv)
    {
        byte[] source = (byte[])data.Clone();
        var plaintext = new byte[32];
        for (int i = 0; i < 31; i++)
        {
            int at = i * 8;
            int n0 = (source[at] >> 4) & 2;
            int n1 = n0 | (source[at + 1] & 64);
            int n2 = n1 | ((source[at + 2] >> 2) & 1);
            int n3 = n2 | ((source[at + 3] << 3) & 128);
            int n4 = n3 | ((source[at + 4] >> 1) & 4);
            int n5 = n4 | ((source[at + 5] << 3) & 16);
            int n6 = n5 | ((source[at + 6] << 5) & 32);
            plaintext[i] = (byte)(n6 | ((source[at + 7] >> 4) & 8));
        }
        plaintext[31] = 1;

        // ★ Python 版 blocks 是**字节序列**（append 整个 16 字节块），
        //   后面按下标取单个字节用 —— 这里保持同样的语义。
        var blocks = new List<byte>();
        byte[] previous = (byte[])iv.Clone();
        for (int off = 0; off < 32; off += 16)
        {
            var chunk = new byte[16];
            Buffer.BlockCopy(plaintext, off, chunk, 0, 16);
            byte[] value = Block(XorBytes(chunk, previous));
            blocks.AddRange(value);
            previous = value;
        }

        var outBuf = (byte[])source.Clone();
        for (int i = 0; i < 31; i++)
        {
            int at = i * 8;
            byte k = blocks[i];
            outBuf[at] &= 0xDF; outBuf[at] |= (byte)((k << 4) & 0x20);
            outBuf[at + 1] &= 0xBF; outBuf[at + 1] |= (byte)(k & 0x40);
            outBuf[at + 2] &= 0xFB; outBuf[at + 2] |= (byte)((k << 2) & 0x04);
            outBuf[at + 3] &= 0xEF; outBuf[at + 3] |= (byte)((k >> 3) & 0x10);
            outBuf[at + 4] &= 0xF7; outBuf[at + 4] |= (byte)((k + k) & 0x08);
            outBuf[at + 5] &= 0xFD; outBuf[at + 5] |= (byte)((k >> 3) & 0x02);
            outBuf[at + 6] &= 0xFE; outBuf[at + 6] |= (byte)((k >> 5) & 0x01);
            outBuf[at + 7] &= 0x7F; outBuf[at + 7] |= (byte)((k << 4) & 0x80);
        }

        var result = new byte[1 + outBuf.Length];
        result[0] = blocks[31];
        Buffer.BlockCopy(outBuf, 0, result, 1, outBuf.Length);
        return result;
    }
}

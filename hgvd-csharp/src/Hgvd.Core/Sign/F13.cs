using System;
using System.Collections.Generic;

namespace Hgvd.Core.Sign;

/// <summary>
/// F13 哈希（medusa 头依赖）。
///
/// ★ 对应 Python 版 <c>hgvd/sign/core.py</c> 的
///   <c>_hash_f13</c> / <c>_branch0_f13</c> / <c>_branch2_f13</c> /
///   <c>_md5_sum_v3</c> / <c>_get_iv</c> / <c>_sum_md5</c>。
/// ★ 常量表在 <see cref="F13Tables"/>（由 Python 版自动生成）。
/// </summary>
internal static class F13
{
    internal static uint GetIv(uint iv, byte[] data)
    {
        uint value = iv;
        for (int i = 0; i < data.Length; i++)
        {
            if ((i & 1) == 0)
            {
                value = (uint)(((value >> 4) ^ value ^ (value << 6) ^ data[i]) & 0xFFFFFFFF);
                continue;
            }
            uint inner = (uint)((((value >> 7) ^ value ^ ((data[i] | (value << 12)) & 0xFFFFFFFF)) & 0xFFFFFFFF));
            value = ~inner & 0xFFFFFFFF;
        }
        return value;
    }

    private static uint SumMd5(byte[] data)
    {
        uint check = 0x20220420;
        for (int i = 0; i < 12; i++)
        {
            uint temp;
            if ((i & 1) == 0)
            {
                temp = (uint)((check >> 3) ^ check);
                check = (uint)((data[i] ^ (check << 7)) & 0xFFFFFFFF);
            }
            else
            {
                temp = (uint)((check >> 5) ^ check);
                check = (uint)((data[i] | (check << 11)) & 0xFFFFFFFF);
                check = ~check & 0xFFFFFFFF;
            }
            check ^= temp;
        }
        return (uint)(((check | 4) ^ 0x01000000) & 0xFFFFFFFF);
    }

    private static uint[] ExpandWords(byte[] data)
    {
        var di = new List<uint>();
        for (int off = 0; off < data.Length - 3; off += 4)
            di.Add(Sm3.ReadU32Be(data, off));
        uint di0 = di[0];
        for (int i = 0; i < 112; i++)
        {
            uint di1 = di[i + 1];
            uint di14 = di[i + 14];
            uint r1 = Util.Rol32(di1, 14) ^ Util.Rol32(di1, 25) ^ (di1 >> 3);
            uint r2 = Util.Rol32(di14, 13) ^ Util.Rol32(di14, 15) ^ (di14 >> 10);
            di.Add((uint)((di0 + di[i + 9] + r1 + r2) & 0xFFFFFFFF));
            di0 = di1;
        }
        return di.ToArray();
    }

    /// <summary>
    /// 轮函数混合。
    /// ★ <paramref name="rounds"/> 是**单个分支**的 11 个整数：
    ///   rounds[0]  = 轮数
    ///   rounds[1..10] = 10 个状态槽位各自对应的数组下标
    ///   （对应 Python 版 <c>idx(k) = rounds[k]</c>，k 是槽位号 1~10）
    /// </summary>
    private static uint[] MixRounds(uint[] state, int[] rounds, uint[] di,
                                     uint[] tt, uint ivV1)
    {
        var st = (uint[])state.Clone();
        int Rounds = rounds[0];
        int I1 = rounds[1], I2 = rounds[2], I3 = rounds[3], I4 = rounds[4];
        int I5 = rounds[5], I6 = rounds[6], I7 = rounds[7], I8 = rounds[8];
        int I9 = rounds[9], I10 = rounds[10];

        for (int i = 0; i < Rounds; i++)
        {
            int baseOff = (int)((ivV1 + (uint)i) & 0xFFFFFFFF);
            uint w = di[baseOff & 127];
            uint n1 = (uint)((((st[I3] ^ st[I4]) & st[I1]) ^ st[I3]));
            uint n2 = Util.Rol32(st[I1], 26) ^ Util.Rol32(st[I1], 21) ^ Util.Rol32(st[I1], 7);
            uint n4 = (uint)((w + n1 + n2 + tt[baseOff & 63] + st[I5]) & 0xFFFFFFFF);
            uint n5 = Util.Rol32(st[I2], 30) ^ Util.Rol32(st[I2], 19) ^ Util.Rol32(st[I2], 10);
            uint n6 = (st[I2] & st[I6]) | ((st[I2] | st[I6]) & st[I7]);
            uint n7 = (uint)((n5 + n6) & 0xFFFFFFFF);
            uint old = st[I9];
            uint last = st[st.Length - 1];

            var next = new uint[st.Length];
            next[0] = last;
            Array.Copy(st, 0, next, 1, st.Length - 1);
            st = next;

            st[I10] = (uint)((n7 + n4) & 0xFFFFFFFF);
            st[I8] = (uint)((old + n4) & 0xFFFFFFFF);
        }
        return st;
    }

    private static byte[] Branch0F13(byte[] querySm3, byte[] bodyMd5, byte[] ts,
                                     int khronos, int ivV0)
    {
        uint ivV1 = F13Tables.BRANCH0_IV[Math.Min(ivV0, F13Tables.BRANCH0_IV.Length - 1)];
        int countV1 = (int)((ivV1 + (uint)khronos + 1) & 255);
        uint countV2 = (uint)((ivV1 + (uint)khronos) & 0xFFFFFFFF);

        var tt = new uint[F13Tables.BRANCH0_TT.Length];
        for (int i = 0; i < tt.Length; i++) tt[i] = Util.Ror32(F13Tables.BRANCH0_TT[i], countV1);

        int n0 = (int)((countV2 + 2) & 7);
        byte[] seed = { 0xFA, 0x45, 0x61, 0xD7 };
        var pad = new byte[seed.Length];
        for (int i = 0; i < seed.Length; i++)
            pad[i] = (byte)(((seed[i] | (seed[i] << 8)) >> n0) & 255);

        byte[] data = Util.Concat(querySm3, bodyMd5, ts, pad,
            Util.FromHex("00000000000001a0"));
        uint[] di = ExpandWords(data);

        var init = new uint[F13Tables.BRANCH0_INIT.Length];
        for (int i = 0; i < init.Length; i++)
            init[i] = Util.Ror32(F13Tables.BRANCH0_INIT[i], (int)(countV2 & 31));

        uint[] d = MixRounds(init, F13Tables.BRANCH0_ROUNDS[ivV0], di, tt, ivV1);

        var ret = new List<byte>();
        for (int i = 0; i < 8; i++)
            ret.AddRange(Util.Be32((int)((d[i] + init[i]) & 0xFFFFFFFF)));
        for (int i = 0; i < 16; i++)
            ret[i] ^= ret[16 + i];

        var outBuf = ret.GetRange(0, 16).ToArray();
        return Util.Concat(outBuf, Util.Le32((int)SumMd5(outBuf)));
    }

    private static uint Md5V3Step(int kind, uint a, uint b, uint c, uint d,
                                   uint m, int shift, uint constant)
    {
        uint f;
        switch (kind)
        {
            case 0: f = (b & c) | (~b & d); break;
            case 1: f = (b & d) | (c & ~d); break;
            case 2: f = b ^ c ^ d; break;
            default: f = c ^ (b | ~d); break;
        }
        return (uint)((b + Util.Rol32((uint)((a + f + m + constant) & 0xFFFFFFFF), shift)) & 0xFFFFFFFF);
    }

    private static byte[] Md5SumV3(byte[] message, uint countV2, byte[] orders, int countV1)
    {
        var sv = new uint[F13Tables.BRANCH2_SV.Length];
        for (int i = 0; i < sv.Length; i++)
            sv[i] = Util.Ror32(F13Tables.BRANCH2_SV[i], countV1);

        uint[] start =
        {
            Util.Ror32(0x79E0F2FB, (int)countV2),
            Util.Ror32(0xC8B52570, (int)countV2),
            Util.Ror32(0xEBC2F8CD, (int)countV2),
            Util.Ror32(0x7C104D93, (int)countV2),
        };
        int endCount = (int)((countV2 + 6) & 255);
        uint[] end =
        {
            Util.Ror32(0x19BE4866, endCount),
            Util.Ror32(0xE85986B4, endCount),
            Util.Ror32(0xE19B326E, endCount),
            Util.Ror32(0x71D1D7D4, endCount),
        };

        var m = new uint[16];
        for (int i = 0; i < 16; i++)
            m[i] = BitConverter.ToUInt32(message, i * 4);

        uint a = start[0], b = start[1], c = start[2], d = start[3];
        // ★ Python 是 `shifts[kind] * 4` —— 4 个值**重复 4 次**变 16 个。
        //   直接写 4 个会在 sh[i]（i 到 15）越界。
        int[][] shifts =
        {
            new[] { 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22 },
            new[] { 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20 },
            new[] { 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23 },
            new[] { 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21 },
        };

        void Run(int kind, int offset)
        {
            int[] sh = shifts[kind];
            for (int i = 0; i < 16; i++)
            {
                int phase = i & 3;
                uint w = m[orders[offset + i]];
                uint k = sv[offset + i];
                switch (phase)
                {
                    case 0: a = Md5V3Step(kind, a, b, c, d, w, sh[i], k); break;
                    case 1: d = Md5V3Step(kind, d, a, b, c, w, sh[i], k); break;
                    case 2: c = Md5V3Step(kind, c, d, a, b, w, sh[i], k); break;
                    default: b = Md5V3Step(kind, b, c, d, a, w, sh[i], k); break;
                }
            }
        }

        Run(0, 0); Run(1, 16); Run(2, 32); Run(3, 48);

        var ret = new List<byte>();
        ret.AddRange(Util.Le32((int)((start[0] + a) ^ end[0])));
        ret.AddRange(Util.Le32((int)((start[1] + b) ^ end[1])));
        ret.AddRange(Util.Le32((int)((start[2] + c) ^ end[2])));
        ret.AddRange(Util.Le32((int)((start[3] + d) ^ end[3])));
        byte[] retBytes = ret.ToArray();
        return Util.Concat(retBytes, Util.Le32((int)SumMd5(retBytes)));
    }

    private static byte[] Branch2F13(uint iv, byte[] querySm3, byte[] bodyMd5,
                                     byte[] ts, int khronos)
    {
        uint v = (uint)((iv & 13) * 86);
        int ivV0 = (int)((((v >> 15) & 255) + ((v >> 8) & 255)));
        uint[] n1s = { 0x8980F29B, 0xEB549C7F, 0xB08726DB, 0xD40CB5E6, 0xE8F559E4 };
        uint n1 = n1s[Math.Min(ivV0, n1s.Length - 1)];
        int countV1 = (int)((n1 + (uint)khronos + 1) & 255);
        uint countV2 = (uint)((n1 + (uint)khronos) & 0xFFFFFFFF);
        int shift = (int)((countV2 + 5) & 7);

        byte[] seed = { 0x84, 0x96, 0x77, 0x9D, 0xD4, 0x15, 0x0B, 0xF8 };
        var pad = new byte[seed.Length];
        for (int i = 0; i < seed.Length; i++)
            pad[i] = (byte)(((seed[i] | (seed[i] << 8)) >> shift) & 255);

        byte[] data = Util.Concat(querySm3, bodyMd5, ts, pad, Util.FromHex("a0010000"));
        int begin = ivV0 << 6;
        // ★ Python 用切片（越界会自动截断），C# 的 Array.Copy 会抛 ——
        //   这里手工做安全拷贝，语义与 Python 一致。
        var orders = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            int idx = begin + i;
            orders[i] = idx < F13Tables.BRANCH2_ORDERS.Length
                ? F13Tables.BRANCH2_ORDERS[idx]
                : (byte)0;
        }
        return Md5SumV3(data, countV2, orders, countV1);
    }

    /// <summary>供测试：计算 F13 哈希（公开入口）。</summary>
    public static byte[] HashForTest(byte[] querySm3, byte[] bodyMd5, byte[] ts, int khronos)
        => Hash(querySm3, bodyMd5, ts, khronos);

    public static byte[] Hash(byte[] querySm3, byte[] bodyMd5, byte[] ts, int khronos)
    {
        uint iv = GetIv(GetIv(GetIv(0x20230928, querySm3), bodyMd5), ts);
        int low = (int)(iv & 15);
        int ivV0 = (low * 171) >> 9;
        int branch = low - ivV0 * 3;

        if (branch == 2)
            return Branch2F13(iv, querySm3, bodyMd5, ts, khronos);

        if (branch != 0)
        {
            byte[] summed = Util.Concat(querySm3, bodyMd5);
            return Util.Concat(
                querySm3[..16],
                bodyMd5,
                Util.Le32((int)SumMd5(summed)));
        }

        return Branch0F13(querySm3, bodyMd5, ts, khronos, ivV0);
    }
}

using System;
using System.Collections.Generic;

namespace Hgvd.Core.Sign;

/// <summary>
/// protobuf 手写编码（medusa / argus 的信封用）。
///
/// ★ 只实现签名链需要的几种 wire type：
///   - 0 = varint
///   - 2 = length-delimited
///   - 5 = 32-bit float
/// ★ sint 字段用 zigzag 编码；argus 用的是**不**做 zigzag 的 varint。
/// </summary>
public static class Proto
{
    public static byte[] Varint(ulong value)
    {
        var outBuf = new List<byte>();
        value &= 0xFFFFFFFFFFFFFFFFUL;
        while (value > 127)
        {
            outBuf.Add((byte)((value & 127) | 128));
            value >>= 7;
        }
        outBuf.Add((byte)value);
        return outBuf.ToArray();
    }

    /// <summary>
    /// zigzag 编码。
    /// ★ 注意语义：负数要编码成**正数**（``~(v &lt;&lt; 1)``），
    ///   不是 64 位补码 —— Python 版 <c>(v &lt;&lt; 1) ^ (v &gt;&gt; 63)</c> 在
    ///   任意精度整数下算出来就是 ``0x1B206F`` 这种小正数。
    ///   写成 64 位补码会得到 ``0xFFF1B206F``，服务端直接拒签。
    /// </summary>
    public static ulong ZigZag(long v) => (ulong)(v < 0 ? ~(v << 1) : (v << 1));

    public static byte[] Key(int tag, int wire) => Varint((ulong)((tag << 3) | wire));

    /// <summary>length-delimited 字段。★ 空 body 也**必须**输出（argus 用）。</summary>
    public static byte[] Bytes(int tag, byte[] body)
    {
        body ??= Array.Empty<byte>();
        return Util.Concat(Key(tag, 2), Varint((ulong)body.Length), body);
    }

    public static byte[] String(int tag, string s) => Bytes(tag, Util.Bytes(s ?? ""));

    /// <summary>sint 字段（zigzag 编码的 varint）。</summary>
    /// ★ 用 64 位 —— 字段值可能超过 32 位（如 1728388016635），
    ///   截成 32 位会让 varint 少 2~3 字节，整个 medusa 头就废了。
    public static byte[] SInt(int tag, long value) =>
        Util.Concat(Key(tag, 0), Varint(ZigZag(value)));

    /// <summary>普通 varint 字段（**不**做 zigzag）—— argus 用。</summary>
    public static byte[] VarintField(int tag, ulong value) =>
        Util.Concat(Key(tag, 0), Varint(value));

    public static byte[] Float(int tag, float value)
    {
        var b = new byte[4];
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, b, 0, 4);
        return Util.Concat(Key(tag, 5), b);
    }

    /// <summary>把多段拼起来。</summary>
    public static byte[] Concat(params byte[][] parts) => Util.Concat(parts);
}

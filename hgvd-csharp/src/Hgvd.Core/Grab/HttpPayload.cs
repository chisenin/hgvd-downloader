using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Hgvd.Core.Grab;

/// <summary>把抓到的原始 HTTP 响应还原成 JSON。</summary>
/// <remarks>
/// ★ 顺序不能反：chunked 是传输层编码，gzip 是内容层编码，
///   必须先解 chunked 再解 gzip。反了会得到一堆乱码（Python 侧踩过）。
/// </remarks>
public static class HttpPayload
{
    private static readonly byte[] Sep = { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };

    public static JsonDocument Parse(byte[] rawResponse)
    {
        if (rawResponse == null || rawResponse.Length == 0) return null;
        int at = IndexOf(rawResponse, Sep);
        if (at < 0) return null;

        string head = Encoding.ASCII.GetString(rawResponse, 0, at);
        var body = new byte[rawResponse.Length - at - Sep.Length];
        Buffer.BlockCopy(rawResponse, at + Sep.Length, body, 0, body.Length);

        if (head.IndexOf("transfer-encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            body = Dechunk(body);
        if (head.IndexOf("content-encoding: gzip", StringComparison.OrdinalIgnoreCase) >= 0)
            body = Gunzip(body);
        if (body.Length == 0) return null;

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static byte[] Dechunk(byte[] data)
    {
        using var outBuf = new MemoryStream();
        int i = 0;
        while (i < data.Length)
        {
            int nl = IndexOf(data, new[] { (byte)'\r', (byte)'\n' }, i);
            if (nl < 0) break;
            string line = Encoding.ASCII.GetString(data, i, nl - i).Trim();
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line.Substring(0, semi);
            if (!int.TryParse(line, System.Globalization.NumberStyles.HexNumber, null, out int size))
                break;
            i = nl + 2;
            if (size == 0) break;
            if (i + size > data.Length) size = data.Length - i;
            outBuf.Write(data, i, size);
            i += size + 2;                                  // 跳过 chunk 末尾的 \r\n
        }
        return outBuf.ToArray();
    }

    public static byte[] Gunzip(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var outBuf = new MemoryStream();
            gz.CopyTo(outBuf);
            return outBuf.ToArray();
        }
        catch (InvalidDataException)
        {
            return data;                                    // 不是 gzip，原样返回
        }
    }

    private static int IndexOf(byte[] hay, byte[] needle, int from = 0)
    {
        if (hay.Length < needle.Length) return -1;
        for (int i = from; i <= hay.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (hay[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Core.Grab;

/// <summary>抓到的一次请求/响应（原始字节，未改动）。</summary>
public sealed class CapturedExchange
{
    public string Host = "";
    public string Target = "";      // 请求行里的 path（含 query）
    public byte[] Request = Array.Empty<byte>();
    public byte[] Response = Array.Empty<byte>();
}

/// <summary>HTTP/1.1 中间人代理：只为抓流服务，不做通用代理。</summary>
/// <remarks>
/// ★ 设计取舍（与 `tools/metasec/mitm.py` 一致，别再各写一套）：
///   · 对客户端只宣告 http/1.1（ALPN），省掉 h2 帧解析；
///   · **每连接只跑一轮**，转发完就关 —— 抓流只要响应，不值得做连接池；
///   · 请求与响应都**原样转发**，不重新编码、不改 header ——
///     App 对响应字节有校验，重新序列化必炸。
/// </remarks>
public sealed class CaptureProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<CapturedExchange> _onExchange;
    private readonly Action<string> _log;
    private Task _accept;

    public int Port { get; }

    public CaptureProxy(int port, Action<CapturedExchange> onExchange, Action<string> log = null)
    {
        Port = port;
        _onExchange = onExchange ?? (_ => { });
        _log = log ?? (_ => { });
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        _listener.Start();
        _accept = AcceptLoopAsync(_cts.Token);
        _log($"代理已监听 0.0.0.0:{Port}");
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------ 连接处理

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => HandleAsync(client, ct));
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var net = client.GetStream();
                var reader = new ConnReader(net);
                var head = await reader.ReadHeadAsync(ct);
                if (head.Length == 0) return;

                var firstLine = FirstLine(head);
                if (firstLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                {
                    var host = firstLine.Split(' ')[1].Split(':')[0];
                    await net.WriteAsync(
                        Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);

                    using var tls = new SslStream(net, false);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        // ★ 这两行是上次被拒的关键：带 CA 链的上下文 + 持久化过 PFX 的私钥
                        ServerCertificateContext = MitmCa.ContextFor(host),
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        ApplicationProtocols = new List<SslApplicationProtocol>
                        {
                            SslApplicationProtocol.Http11,
                        },
                    }, ct);

                    await PumpAsync(tls, host, ct);
                }
                else
                {
                    // 明文 HTTP：请求行里是绝对 URL
                    await PumpPlainAsync(net, reader, head, ct);
                }
            }
            catch (Exception ex)
            {
                _log($"！连接异常 {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task PumpAsync(Stream clientTls, string connectHost, CancellationToken ct)
    {
        var reader = new ConnReader(clientTls);
        var head = await reader.ReadHeadAsync(ct);
        if (head.Length == 0) return;
        var body = await reader.ReadBodyAsync(head, ct);
        var request = Concat(head, body);

        var text = Encoding.ASCII.GetString(head);
        var host = HeaderValue(text, "Host");
        if (string.IsNullOrEmpty(host)) host = connectHost;
        host = host.Split(':')[0];
        var target = FirstLine(head).Split(' ');

        byte[] response;
        using (var upstream = new TcpClient())
        {
            await upstream.ConnectAsync(host, 443, ct);
            using var ssl = new SslStream(upstream.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, ct);

            await ssl.WriteAsync(request, ct);
            var up = new ConnReader(ssl);
            var respHead = await up.ReadHeadAsync(ct);
            var respBody = respHead.Length > 0 ? await up.ReadBodyAsync(respHead, ct) : Array.Empty<byte>();
            response = Concat(respHead, respBody);
        }

        if (response.Length > 0) await clientTls.WriteAsync(response, ct);

        _onExchange(new CapturedExchange
        {
            Host = host,
            Target = target.Length > 1 ? target[1] : "",
            Request = request,
            Response = response,
        });
    }

    private async Task PumpPlainAsync(Stream net, ConnReader reader, byte[] head, CancellationToken ct)
    {
        var body = await reader.ReadBodyAsync(head, ct);
        var request = Concat(head, body);
        var text = Encoding.ASCII.GetString(head);
        var parts = FirstLine(head).Split(' ');
        var url = parts.Length > 1 ? parts[1] : "";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return;

        var uri = new Uri(url);
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(uri.Host, uri.Port, ct);
        var s = upstream.GetStream();
        await s.WriteAsync(request, ct);
        var up = new ConnReader(s);
        var respHead = await up.ReadHeadAsync(ct);
        var respBody = respHead.Length > 0 ? await up.ReadBodyAsync(respHead, ct) : Array.Empty<byte>();
        var response = Concat(respHead, respBody);
        if (response.Length > 0) await net.WriteAsync(response, ct);

        _onExchange(new CapturedExchange
        {
            Host = uri.Host,
            Target = uri.PathAndQuery,
            Request = request,
            Response = response,
        });
    }

    // ------------------------------------------------------------ 小工具

    private static string FirstLine(byte[] head)
    {
        var text = Encoding.ASCII.GetString(head);
        int i = text.IndexOf("\r\n", StringComparison.Ordinal);
        return i > 0 ? text.Substring(0, i) : text;
    }

    private static string HeaderValue(string head, string name)
    {
        foreach (var line in head.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line.Substring(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line.Substring(colon + 1).Trim();
        }
        return null;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        if (b == null || b.Length == 0) return a;
        var outBuf = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, outBuf, 0, a.Length);
        Buffer.BlockCopy(b, 0, outBuf, a.Length, b.Length);
        return outBuf;
    }

    /// <summary>带残余缓冲的连接读取器（head 之后可能已经读进 body 的前半段）。</summary>
    private sealed class ConnReader
    {
        private static readonly byte[] Sep = { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
        private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };
        private readonly Stream _s;
        private byte[] _pending = Array.Empty<byte>();

        public ConnReader(Stream s) => _s = s;

        public async Task<byte[]> ReadHeadAsync(CancellationToken ct)
        {
            while (true)
            {
                int at = IndexOf(_pending, Sep);
                if (at >= 0)
                {
                    int end = at + Sep.Length;
                    var head = _pending.AsSpan(0, end).ToArray();
                    _pending = _pending.AsSpan(end).ToArray();
                    return head;
                }
                if (!await FillAsync(ct))
                {
                    var rest = _pending;
                    _pending = Array.Empty<byte>();
                    return rest;
                }
            }
        }

        public async Task<byte[]> ReadBodyAsync(byte[] head, CancellationToken ct)
        {
            var text = Encoding.ASCII.GetString(head);
            if (text.IndexOf("transfer-encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                return await ReadChunkedAsync(ct);

            int len = ContentLength(text);
            if (len > 0) return await ReadExactAsync(len, ct);
            return await ReadToEndAsync(ct);
        }

        private async Task<byte[]> ReadChunkedAsync(CancellationToken ct)
        {
            var outBuf = new MemoryStream();
            while (true)
            {
                var line = await ReadLineAsync(ct);
                if (line.Length == 0) break;
                outBuf.Write(line, 0, line.Length);

                var sizeText = Encoding.ASCII.GetString(line).Trim();
                int semi = sizeText.IndexOf(';');
                if (semi >= 0) sizeText = sizeText.Substring(0, semi);
                if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber,
                                  null, out int size)) break;

                if (size == 0)
                {
                    // trailer 到空行
                    while (true)
                    {
                        var t = await ReadLineAsync(ct);
                        if (t.Length == 0) break;
                        outBuf.Write(t, 0, t.Length);
                        if (t.Length <= 2) break;
                    }
                    break;
                }

                var chunk = await ReadExactAsync(size, ct);
                outBuf.Write(chunk, 0, chunk.Length);
                var tail = await ReadExactAsync(2, ct);        // \r\n
                outBuf.Write(tail, 0, tail.Length);
            }
            return outBuf.ToArray();
        }

        private async Task<byte[]> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                int at = IndexOf(_pending, Crlf);
                if (at >= 0)
                {
                    int end = at + 2;
                    var line = _pending.AsSpan(0, end).ToArray();
                    _pending = _pending.AsSpan(end).ToArray();
                    return line;
                }
                if (!await FillAsync(ct))
                {
                    var rest = _pending;
                    _pending = Array.Empty<byte>();
                    return rest;
                }
            }
        }

        private async Task<byte[]> ReadExactAsync(int n, CancellationToken ct)
        {
            while (_pending.Length < n)
            {
                if (!await FillAsync(ct)) break;
            }
            int take = Math.Min(n, _pending.Length);
            var part = _pending.AsSpan(0, take).ToArray();
            _pending = _pending.AsSpan(take).ToArray();
            return part;
        }

        private async Task<byte[]> ReadToEndAsync(CancellationToken ct)
        {
            while (await FillAsync(ct)) { }
            var all = _pending;
            _pending = Array.Empty<byte>();
            return all;
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            var tmp = new byte[16 * 1024];
            int n = await _s.ReadAsync(tmp, 0, tmp.Length, ct);
            if (n <= 0) return false;
            var merged = new byte[_pending.Length + n];
            Buffer.BlockCopy(_pending, 0, merged, 0, _pending.Length);
            Buffer.BlockCopy(tmp, 0, merged, _pending.Length, n);
            _pending = merged;
            return true;
        }

        private static int ContentLength(string head)
        {
            var v = HeaderValue(head, "Content-Length");
            return int.TryParse(v, out int n) ? n : 0;
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            if (hay.Length < needle.Length) return -1;
            for (int i = 0; i <= hay.Length - needle.Length; i++)
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
}

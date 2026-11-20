using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core.Grab;

namespace Hgvd.Tests;

/// <summary>
/// 纯本地 TLS 对照探针：不做 HTTP、不碰模拟器，只验证「证书 + TLS 服务端」能不能握手。
///
/// 用法：`Hgvd.Tests.exe --tlsprobe [端口] [秒数]`
/// 然后另一个终端：`openssl s_client -connect 127.0.0.1:<port> -servername localhost`
///
/// ★ 存在的意义：把「安卓拒证书」和「Schannel 自己起不来」分开。
///   如果 openssl 都连不上 C# 服务端 → 是服务端/证书问题，与 App 无关；
///   如果 openssl 能连上而 App 不能 → 是 App 侧信任/固定问题，换库也没用。
/// </summary>
public static class TlsProbe
{
    public static int Run(string[] args)
    {
        int port = 18443;
        int seconds = 90;
        if (args.Length > 1 && int.TryParse(args[1], out int p)) port = p;
        if (args.Length > 2 && int.TryParse(args[2], out int s)) seconds = s;

        Console.WriteLine("=== TLS 本地对照探针（纯本地，不碰模拟器）===");
        Console.WriteLine($"监听 127.0.0.1:{port}，SNI=localhost");
        Console.WriteLine($"对应客户端命令：openssl s_client -connect 127.0.0.1:{port} -servername localhost -brief\n");

        var ca = MitmCa.Ensure();
        Console.WriteLine($"CA: {ca.Subject}, 私钥={ca.HasPrivateKey}");
        Console.WriteLine("模式：读 CONNECT 的 host，逐域名现签（与真实代理一致）\n");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        int n = 0;

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }

                int id = ++n;
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        string host = "localhost";
                        try
                        {
                            // 与真实 CaptureProxy 一致：先收 CONNECT，再按 host 现签证书
                            var net = client.GetStream();
                            var headBuf = new byte[4096];
                            int hn = await net.ReadAsync(headBuf, 0, headBuf.Length, cts.Token);
                            string head = System.Text.Encoding.ASCII.GetString(headBuf, 0, hn);
                            if (head.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                            {
                                host = head.Substring(8).Split(' ')[0].Split(':')[0];
                                await net.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                                    "HTTP/1.1 200 Connection established\r\n\r\n"), cts.Token);
                            }
                            var tls = new SslStream(net, false);
                            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                            {
                                ServerCertificateContext = MitmCa.ContextFor(host),
                                ClientCertificateRequired = false,
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                                ApplicationProtocols = new List<SslApplicationProtocol>
                                {
                                    SslApplicationProtocol.Http11,
                                },
                            }, cts.Token);

                            Console.WriteLine($"[{id}] ✓ {host} 握手成功: {tls.SslProtocol} "
                                              + $"cipher={tls.CipherAlgorithm}");
                            // 等对方发点东西，然后回一句
                            var buf = new byte[1024];
                            try { await tls.ReadAsync(buf, 0, buf.Length, cts.Token); } catch { }
                            var msg = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK");
                            await tls.WriteAsync(msg, cts.Token);
                        }
                        catch (Exception ex)
                        {
                            var parts = new List<string>();
                            Exception cur = ex;
                            int depth = 0;
                            while (cur != null && depth++ < 4)
                            {
                                parts.Add($"{cur.GetType().Name}: {cur.Message}");
                                cur = cur.InnerException;
                            }
                            Console.WriteLine($"[{id}] ✗ {host}: {string.Join(" | ", parts)}");
                        }
                    }
                });
            }
        });

        Console.WriteLine($"探针运行 {seconds} 秒。Ctrl-C 或等待结束。\n");
        try { Thread.Sleep(seconds * 1000); } catch (ThreadAbortException) { }
        cts.Cancel();
        listener.Stop();
        Console.WriteLine("探针结束。");
        return 0;
    }
}

using System;
using System.IO;
using Hgvd.Core.Grab;

namespace Hgvd.Tests;

/// <summary>实机验证入口：起 MITM 代理，看安卓能不能握手、能不能抓到 preload。</summary>
/// <remarks>
/// 用法：`Hgvd.Tests.exe --mitmtest [--port 8899]`
/// 跑起来后另开一个终端设模拟器代理并重启红果，然后在这个窗口看输出。
/// </remarks>
public static class MitmTest
{
    public static int Run(string[] args)
    {
        int port = 8899;
        int seconds = 150;
        for (int i = 1; i + 1 < args.Length; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out int p)) port = p;
            if (args[i] == "--seconds" && int.TryParse(args[i + 1], out int s)) seconds = s;
        }

        // 先自检证书链能不能建起来（这一步过了，剩下的才是网络问题）
        var ca = MitmCa.Ensure();
        Console.WriteLine($"CA        : {ca.Subject}");
        Console.WriteLine($"CA 到期   : {ca.NotAfter:yyyy-MM-dd}");
        Console.WriteLine($"CA 私钥   : {(ca.HasPrivateKey ? "有" : "★没有（握手必失败）")}");
        var ctx = MitmCa.ContextFor("api5-normal-sinfonlinea.fqnovel.com");
        Console.WriteLine($"叶证书上下文: {(ctx != null ? "OK" : "NULL")}");
        Console.WriteLine($"CA 目录   : {MitmCa.CaDir}");
        Console.WriteLine();

        var proxy = new CaptureProxy(port, ex =>
        {
            var t = ex.Target ?? "";
            bool watched = t.Contains("multi_video_model") || t.Contains("multi_video_detail")
                           || t.Contains("video_detail") || t.Contains("video_playback")
                           || t.Contains("album_detail");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {(watched ? "⇢" : "·")} {ex.Host}{t}  {ex.Response.Length} 字节");
            if (!watched || ex.Response.Length == 0) return;

            try
            {
                Directory.CreateDirectory("_ref/csharp");
                var tag = t.Split('?')[0].Trim('/').Replace('/', '_');
                File.WriteAllBytes(Path.Combine("_ref/csharp", tag + ".resp.bin"), ex.Response);
            }
            catch (IOException) { }
        }, m => Console.WriteLine("  " + m));

        proxy.Start();
        Console.WriteLine($"代理已启动 :{port}（ALPN=http/1.1），跑 {seconds} 秒。\n");
        System.Threading.Thread.Sleep(seconds * 1000);
        proxy.Stop();
        Console.WriteLine("已停止。");
        return 0;
    }
}

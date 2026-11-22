using System;
using System.Linq;
using Hgvd.Core;

namespace Hgvd.Tests;

/// <summary>直连验证：用**播放专用签名**打 video_model/v1（v1.0.0 的老路径）。</summary>
/// <remarks>
/// 用法：`Hgvd.Tests.exe --playtest [seriesId]`
/// ★ 这个入口用来实机确认播放接口的直链口径（2026-11-22 抓流裁掉后，
///   它是唯一仍在的实机取链探针）。
/// </remarks>
public static class PlayTest
{
    public static int Run(string[] args)
    {
        string sid = args.Length > 1 ? args[1] : "7709143158319221785";
        var svc = new HongguoService();
        Console.WriteLine($"登录态: {(svc.LoggedIn ? "有" : "无")}   series: {sid}");
        if (!svc.LoggedIn) return 1;

        try
        {
            var model = svc.Client.PlaybackAsync(sid, 1, default).GetAwaiter().GetResult();
            Console.WriteLine($"streams = {model.Streams.Count}");
            foreach (var s in model.Streams.Take(3))
            {
                var url = s.Url ?? "";
                Console.WriteLine($"  {s.Definition} {s.Height}p enc={s.Encrypted}  "
                                  + url.Substring(0, Math.Min(90, url.Length)));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"失败: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"  inner: {ex.InnerException.Message}");
        }
        return 0;
    }
}

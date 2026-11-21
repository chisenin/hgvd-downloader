using System;
using System.Collections.Generic;
using System.Text.Json;
using Hgvd.Core;
using Hgvd.Core.Grab;

namespace Hgvd.Tests;

/// <summary>
/// 抓流准入判据的离线回归：**白名单命中是唯一准入条件**。
/// </summary>
/// <remarks>
/// ★ 2026-11-21 用户复核定案的目标口径：
///   本工具只下载「保存在收藏夹的教材/合集类视频」。
///   ⇒ 抓流抓到的 vid 必须在白名单里才下；
///     推荐流、宣发短片、收藏夹外的教学内容、短剧一律跳过；
///     **没有任何绕过白名单的开关**。
///   这里守三件事：
///     ① 白名单内的 vid → 收下并带正确的剧名/集号；
///     ② 白名单外的 vid → 拒收（推荐流、短剧、别的课）；
///     ③ 「类型拿不到」不是准入理由（那是未知，不是教材）。
/// </remarks>
public static class GrabWhitelistTests
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "  ✓ " : "  ✗ ") + name + (ok ? "" : "  " + detail));
        if (!ok) _fail++;
    }

    /// <summary>造一个 preload 响应：data[vid].video_model 是嵌套 JSON 字符串。</summary>
    private static byte[] PreloadResponse(string vid, string mainUrl, double duration = 120)
    {
        string vm = JsonSerializer.Serialize(new
        {
            status = 10,
            video_duration = duration,
            video_list = new[]
            {
                new
                {
                    main_url = mainUrl,
                    backup_url = "",
                    video_meta = new { definition = "1080p", vheight = 1080, bitrate = 2_000_000 },
                },
            },
        });
        string body = JsonSerializer.Serialize(new
        {
            code = 0,
            data = new Dictionary<string, object> { [vid] = new { video_model = vm } },
        });
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(body);
        byte[] head = System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
            + raw.Length + "\r\n\r\n");
        byte[] all = new byte[head.Length + raw.Length];
        Buffer.BlockCopy(head, 0, all, 0, head.Length);
        Buffer.BlockCopy(raw, 0, all, head.Length, raw.Length);
        return all;
    }

    public static int Run()
    {
        Console.WriteLine("── 抓流白名单准入（只下收藏夹里的教材/合集类）──");
        _fail = 0;

        var wl = new Dictionary<string, WhitelistEntry>
        {
            ["vid_ok"] = new WhitelistEntry
            {
                Book = "《奇趣百科》", Index = 7, Title = "第七集", Duration = 200,
            },
        };

        // ① 白名单内的 vid → 收下
        {
            var jobs = new List<GrabJob>();
            var src = new GrabSource(wl, jobs.Add, _ => { });
            src.Handle(new CapturedExchange
            {
                Target = "/novel/player/multi_video_model/preload/v1?x=1",
                Response = PreloadResponse("vid_ok", "https://cdn/ok.mp4", 200),
            });
            Check("白名单内的 vid 收下", jobs.Count == 1 && src.Accepted == 1,
                  $"jobs={jobs.Count} accepted={src.Accepted}");
            if (jobs.Count == 1)
            {
                Check("剧名/集号/集名取自白名单",
                      jobs[0].Book == "《奇趣百科》" && jobs[0].Index == 7
                      && jobs[0].Title == "第七集",
                      $"{jobs[0].Book} / {jobs[0].Index} / {jobs[0].Title}");
                Check("直链取自响应", jobs[0].Url == "https://cdn/ok.mp4", jobs[0].Url);
            }
        }

        // ② 白名单外的 vid → 拒收（推荐流宣发片 / 短剧 / 别的课都走这条路）
        {
            foreach (string vid in new[] { "vid_reco", "vid_drama", "vid_other" })
            {
                var jobs = new List<GrabJob>();
                var src = new GrabSource(wl, jobs.Add, _ => { });
                src.Handle(new CapturedExchange
                {
                    Target = "/novel/player/multi_video_model/preload/v1?x=1",
                    Response = PreloadResponse(vid, "https://cdn/no.mp4"),
                });
                Check($"白名单外的 vid（{vid}）不收下",
                      jobs.Count == 0 && src.Rejected == 1,
                      $"jobs={jobs.Count} rejected={src.Rejected}");
            }
        }

        // ③ 「类型拿不到」不是准入理由：白名单外的一律拒收，没有「按特征猜」的旁路
        {
            var jobs = new List<GrabJob>();
            var src = new GrabSource(wl, jobs.Add, _ => { });
            // 未加密、时长很长（旧的 take_unknown 特征）—— 仍然必须拒收
            src.Handle(new CapturedExchange
            {
                Target = "/novel/player/multi_video_model/preload/v1?x=1",
                Response = PreloadResponse("vid_unknown", "https://cdn/unknown.mp4", 3600),
            });
            Check("白名单外 + 未加密 + 长时长也不收下（无旁路特征）",
                  jobs.Count == 0 && src.Rejected == 1,
                  $"jobs={jobs.Count} rejected={src.Rejected}");
        }

        // ④ 非取流接口直接忽略
        {
            var jobs = new List<GrabJob>();
            var src = new GrabSource(wl, jobs.Add, _ => { });
            src.Handle(new CapturedExchange
            {
                Target = "/reading/bookapi/bookshelf/video/list/v?x=1",
                Response = PreloadResponse("vid_ok", "https://cdn/ok.mp4"),
            });
            Check("非取流接口不处理", jobs.Count == 0 && src.Accepted == 0);
        }

        // ⑤ 同一 vid 重复出现只收一次
        {
            var jobs = new List<GrabJob>();
            var src = new GrabSource(wl, jobs.Add, _ => { });
            var ex = new CapturedExchange
            {
                Target = "/novel/player/multi_video_model/preload/v1?x=1",
                Response = PreloadResponse("vid_ok", "https://cdn/ok.mp4", 200),
            };
            src.Handle(ex);
            src.Handle(ex);
            Check("同一 vid 不重复收下", jobs.Count == 1, $"jobs={jobs.Count}");
        }

        Console.WriteLine($"── GrabWhitelistTests fail={_fail} ──");
        return _fail;
    }
}

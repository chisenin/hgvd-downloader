using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hgvd.Core.Api;

namespace Hgvd.Core.Grab;

/// <summary>
/// 读取「自动抓剧」（tools/metasec/hook_grab.py --dry）产出的直链清单 JSON，
/// 转成可直接下载的 Episode（预置直链 + CENC 密钥）。
///
/// 清单结构（Python json.dump）：
/// <code>
/// {
///   "&lt;vid&gt;": {
///     "vm": { ... video_model ... },
///     "streams": [ { "url", "backup_url", "definition", "bitrate", "spade_a", "kid",
///                    "encrypted", "duration", ... }, ... ],
///     "duration": 123.0
///   },
///   ...
/// }
/// </code>
/// </summary>
public static class GrabManifest
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析结果：一组 Episode（按清单顺序，Index 从 1 递增）。</summary>
    public sealed class Result
    {
        public List<Episode> Episodes = new();
        public string Title = "";
        public int Skipped;          // 无有效直链的 vid 数
        public string Error = "";    // 解析失败原因（文件不存在/格式坏）
    }

    /// <summary>
    /// 从清单文件解析出可下载的分集。
    /// <paramref name="seriesId"/>/<paramref name="title"/> 由界面提供（清单里没有剧级信息）。
    /// <paramref name="title"/> 为空时尝试从每集 vm 里推断。
    /// </summary>
    public static Result Load(string path, string seriesId, string title = "",
                              string quality = "1080p")
    {
        var r = new Result();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            r.Error = "清单文件不存在：" + path;
            return r;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (Exception e)
        {
            r.Error = "清单 JSON 解析失败：" + e.Message;
            return r;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                r.Error = "清单根节点不是对象（vid → 分集）";
                return r;
            }

            int idx = 0;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string vid = prop.Name;
                if (prop.Value.ValueKind != JsonValueKind.Object) { r.Skipped++; continue; }

                var streamsEl = Prop(prop.Value, "streams");
                if (streamsEl == null || streamsEl.Value.ValueKind != JsonValueKind.Array)
                { r.Skipped++; continue; }

                // 选最高码率（与 Python hook_grab.py 下载分支一致）。
                // 若界面指定了清晰度，优先按 definition 精确匹配，否则取码率最高。
                JsonElement? chosen = null;
                long bestBitrate = -1;
                foreach (var s in streamsEl.Value.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    string url = Str(s, "url");
                    if (string.IsNullOrEmpty(url)) continue;

                    bool match = string.IsNullOrEmpty(quality) ||
                                 string.Equals(Str(s, "definition"), quality, StringComparison.OrdinalIgnoreCase);
                    long br = Long(s, "bitrate");
                    // 指定清晰度时：匹配项优先；都没匹配上则退回码率最高。
                    if (chosen == null || (match && !IsMatch(chosen.Value, quality)) ||
                        (match == IsMatch(chosen.Value, quality) && br > bestBitrate))
                    {
                        chosen = s;
                        bestBitrate = br;
                    }
                }
                if (chosen == null) { r.Skipped++; continue; }

                var cs = chosen.Value;
                string spade = Str(cs, "spade_a");
                string key = "";
                if (!string.IsNullOrEmpty(spade))
                {
                    try { key = Drm.DeriveContentKey(spade); } catch { }
                }

                double dur = NumValue(Prop(prop.Value, "duration"));
                if (dur <= 0) dur = Double(cs, "duration");

                idx++;
                r.Episodes.Add(new Episode
                {
                    Vid = vid,
                    Index = idx,
                    Title = string.IsNullOrEmpty(r.Title) ? $"第 {idx} 集" : $"第 {idx} 集",
                    Duration = dur,
                    SeriesId = seriesId,
                    SeriesTitle = title,
                    VideoIdType = 0,                 // 短剧（预置直链时仅作记录）
                    DirectUrl = Str(cs, "url"),
                    DirectBackupUrl = Str(cs, "backup_url"),
                    DirectContentKey = key,
                });
            }
        }

        r.Title = title;
        return r;
    }

    private static bool IsMatch(JsonElement s, string quality)
        => !string.IsNullOrEmpty(quality) &&
           string.Equals(Str(s, "definition"), quality, StringComparison.OrdinalIgnoreCase);

    private static JsonElement? Prop(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
           ? v : (JsonElement?)null;

    private static string Str(JsonElement obj, string name)
    {
        var v = Prop(obj, name);
        return v.HasValue && v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() ?? "" : "";
    }

    private static long Long(JsonElement obj, string name)
    {
        var v = Prop(obj, name);
        return v.HasValue && v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt64(out var n)
            ? n : 0;
    }

    private static double NumValue(JsonElement? v)
        => v.HasValue && v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetDouble(out var n)
           ? n : 0;

    private static double Double(JsonElement? obj, string name)
    {
        if (!obj.HasValue) return 0;
        var v = Prop(obj.Value, name);
        return NumValue(v);
    }
}

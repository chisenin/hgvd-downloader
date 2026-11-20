using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hgvd.Xqd;

/// <summary>清单里的一条：标题 / 链接 / 营 / 期望时长。</summary>
public sealed class LinkItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Camp { get; set; } = "";
    public string Url { get; set; } = "";
    public double Duration { get; set; }

    public bool HasUrl => XqdUtil.IsHttp(Url);
    public override string ToString() => $"<Link {Title} {Url}>";
}

/// <summary>
/// 链接清单解析 —— 兴趣岛侧唯一的「课表来源」（不做 ADB 自动化）。
///
/// 支持三类输入，可混合粘贴：
///  1. 纯文本，一行一条：
///        课名 | https://…/xxx.m3u8
///        课名&lt;TAB&gt;https://…          （从 Excel 复制过来就是这种）
///        课名 https://…                 （空格分隔也行）
///        https://…                      （只有链接时用文件名当标题）
///        # 注释 / 空行
///  2. JSON 数组：[{"title":"…","url":"…","camp":"…"}]
///  3. JSON 对象：
///        {"课名":"https://…"}                 —— 键是标题
///        {"<fileId>":{"url":"…","camp":"…"}}  —— xqd 的 links.json（键是 fileId）
///        {"<fileId>":{"name":"…"}}            —— xqd 的 names.json（没有 url）
///     后两种按 fileId 合并 —— 同时导入 links.json 与 names.json 就能自动补全课名。
/// </summary>
public static class LinkParser
{
    private static readonly Regex LeadingMarker =
        new(@"^\s*(?:[-*•·]|\d+[.)、])\s+", RegexOptions.Compiled);

    private static readonly Regex TailDuration =
        new(@"[\[\(（]\s*(\d{1,2}(?::\d{1,2}){1,2})\s*[\]\)）]\s*$", RegexOptions.Compiled);

    private static readonly Regex KeyDuration =
        new(@"\s*(?:时长|duration)\s*[=:：]\s*(\d{1,2}(?::\d{1,2}){1,2}|\d+(?:\.\d+)?)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>解析一段文本（自动识别 JSON / 纯文本）。</summary>
    public static List<LinkItem> Parse(string content, List<string> errors = null)
    {
        var items = new List<LinkItem>();
        if (string.IsNullOrWhiteSpace(content)) return items;

        string text = content.Trim().TrimStart('\uFEFF');
        if (text.StartsWith("{", StringComparison.Ordinal) ||
            text.StartsWith("[", StringComparison.Ordinal))
        {
            try { ParseJson(text, items, errors); }
            catch (Exception e) { errors?.Add("JSON 解析失败：" + e.Message); }
            return Dedupe(items);
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("#", StringComparison.Ordinal) ||
                line.StartsWith("//", StringComparison.Ordinal)) continue;

            line = LeadingMarker.Replace(line, "").Trim();
            if (line.Length == 0) continue;

            var item = ParseLine(line, errors);
            if (item != null) items.Add(item);
        }
        return Dedupe(items);
    }

    /// <summary>把多份清单合并（按 fileId / 标题补全），先出现的优先。</summary>
    public static List<LinkItem> Merge(IEnumerable<List<LinkItem>> sources)
    {
        var byKey = new Dictionary<string, LinkItem>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var list in sources)
        {
            if (list == null) continue;
            foreach (var it in list)
            {
                string key = KeyOf(it);
                if (!byKey.TryGetValue(key, out var cur))
                {
                    byKey[key] = Clone(it);
                    order.Add(key);
                    continue;
                }
                if (string.IsNullOrEmpty(cur.Url) && !string.IsNullOrEmpty(it.Url)) cur.Url = it.Url;
                if (string.IsNullOrEmpty(cur.Title) && !string.IsNullOrEmpty(it.Title)) cur.Title = it.Title;
                if (string.IsNullOrEmpty(cur.Camp) && !string.IsNullOrEmpty(it.Camp)) cur.Camp = it.Camp;
                if (cur.Duration <= 0 && it.Duration > 0) cur.Duration = it.Duration;
            }
        }

        var merged = new List<LinkItem>();
        foreach (string k in order)
        {
            var it = byKey[k];
            if (it.HasUrl) merged.Add(it);   // 只有名字没有链接的（names.json）不单独成行
        }
        return merged;
    }

    // ══════════════════════════════════════════════════════════════

    private static LinkItem ParseLine(string line, List<string> errors)
    {
        // 1) 结尾的时长：[12:34] / (1:02:03) / 时长=754
        double duration = 0;
        var md = KeyDuration.Match(line);
        if (md.Success)
        {
            duration = ParseDurationToken(md.Groups[1].Value);
            line = line.Substring(0, md.Index).Trim();
        }
        else
        {
            var mt = TailDuration.Match(line);
            if (mt.Success)
            {
                duration = ParseDurationToken(mt.Groups[1].Value);
                line = line.Substring(0, mt.Index).Trim();
            }
        }

        string title, url;

        int bar = line.IndexOf('|');
        if (bar >= 0)
        {
            title = line.Substring(0, bar).Trim();
            url = line.Substring(bar + 1).Trim();
        }
        else if (line.IndexOf('\t') >= 0)
        {
            var parts = line.Split('\t');
            title = parts[0].Trim();
            url = string.Join("", parts.Skip(1)).Trim();
        }
        else if (XqdUtil.IsHttp(line))
        {
            title = "";
            url = line;
        }
        else
        {
            int http = line.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (http > 0)
            {
                title = line.Substring(0, http).Trim();
                url = line.Substring(http).Trim();
            }
            else
            {
                errors?.Add("无法识别的行：" + Shorten(line));
                return null;
            }
        }

        if (!XqdUtil.IsHttp(url))
        {
            errors?.Add("链接不是 http/https：" + Shorten(line));
            return null;
        }

        // 链接里可能粘进尾部空白 / 引号
        url = url.Trim().Trim('"', '\'', ',', '，', ';', '；');

        if (string.IsNullOrEmpty(title)) title = TitleFromUrl(url);
        title = CleanTitle(title);

        return new LinkItem { Title = title, Url = url, Duration = duration };
    }

    private static void ParseJson(string text, List<LinkItem> items, List<string> errors)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray())
                if (el.ValueKind == JsonValueKind.Object) items.Add(ItemFromObject(el, ""));
            return;
        }

        if (root.ValueKind != JsonValueKind.Object) return;

        // 单条对象（自己就有 url 字段）
        if (FindString(root, "url", "m3u8", "src", "playUrl", "play_url", "link") != null)
        {
            items.Add(ItemFromObject(root, ""));
            return;
        }

        // 键是标题（{"课名":"https://…"}）或键是 fileId（xqd 的 links.json / names.json）
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Object)
            {
                var it = ItemFromObject(p.Value, p.Name);
                if (it != null) items.Add(it);
            }
            else if (p.Value.ValueKind == JsonValueKind.String)
            {
                string v = p.Value.GetString() ?? "";
                if (XqdUtil.IsHttp(v))
                    items.Add(new LinkItem { Id = p.Name, Title = CleanTitle(p.Name), Url = v });
            }
        }
    }

    private static LinkItem ItemFromObject(JsonElement el, string fallbackId)
    {
        string url = FindString(el, "url", "m3u8", "src", "playUrl", "play_url", "link") ?? "";
        string name = FindString(el, "title", "name", "course", "courseName", "课名", "lesson") ?? "";
        string camp = FindString(el, "camp", "营", "className", "category") ?? "";
        string id = FindString(el, "id", "fileId", "file_id", "topicId", "topic_id") ?? fallbackId;

        double dur = 0;
        if (el.TryGetProperty("duration", out var d))
        {
            if (d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var x)) dur = x;
            else if (d.ValueKind == JsonValueKind.String) dur = ParseDurationToken(d.GetString());
        }

        if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(name)) return null;

        return new LinkItem
        {
            Id = id ?? "",
            Title = CleanTitle(name),
            Camp = (camp ?? "").Trim(),
            Url = url.Trim(),
            Duration = dur,
        };
    }

    private static string FindString(JsonElement el, params string[] names)
    {
        foreach (string n in names)
            foreach (var p in el.EnumerateObject())
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) &&
                    p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
        return null;
    }

    /// <summary>
    /// 单份清单内部去重。
    /// ★ 这里**不能**过滤「没有链接」的条目 —— names.json 只有课名，
    ///   要靠 <see cref="Merge"/> 与 links.json 按 fileId 合并；过滤掉就永远补不上课名。
    /// </summary>
    private static List<LinkItem> Dedupe(List<LinkItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<LinkItem>();
        foreach (var it in items)
            if (seen.Add(KeyOf(it))) list.Add(Clone(it));
        return list;
    }

    private static string KeyOf(LinkItem it)
    {
        if (!string.IsNullOrEmpty(it.Id)) return "id:" + it.Id;
        if (!string.IsNullOrEmpty(it.Url)) return "url:" + it.Url;
        return "title:" + (it.Title ?? "");
    }

    private static LinkItem Clone(LinkItem it) => new()
    {
        Id = it.Id, Title = it.Title, Camp = it.Camp, Url = it.Url, Duration = it.Duration,
    };

    /// <summary>「课程名.m3u8?sign=…」→ 课程名；fileId 这种纯数字串则保留原样。</summary>
    private static string TitleFromUrl(string url)
    {
        string name = XqdUtil.BaseNameOf(url);
        foreach (string ext in new[] { ".m3u8", ".mp4", ".ts", ".mp3", ".m4a", ".aac" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            { name = name.Substring(0, name.Length - ext.Length); break; }
        if (name.Length == 0) name = "未命名";
        return name;
    }

    private static string CleanTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        string s = title.Trim().Trim('"', '\'', '“', '”');
        // 用户从资源管理器复制常带扩展名
        foreach (string ext in new[] { ".mp4", ".m3u8", ".ts", ".mp3", ".m4a", ".aac" })
            if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            { s = s.Substring(0, s.Length - ext.Length); break; }
        return s.Trim();
    }

    /// <summary>12:34 / 1:02:03 / 754（秒）→ 秒。</summary>
    public static double ParseDurationToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        token = token.Trim();
        if (token.IndexOf(':') < 0)
            return double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out var sec)
                ? sec : 0;
        double total = 0;
        foreach (string part in token.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return 0;
            total = total * 60 + v;
        }
        return total;
    }

    private static string Shorten(string s) =>
        s.Length <= 60 ? s : s.Substring(0, 57) + "...";
}

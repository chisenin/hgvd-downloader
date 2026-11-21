using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hgvd.Core;

/// <summary>清单里的一集（只存重建界面与下载所需的最小字段）。</summary>
public sealed class QueueEpisode
{
    public string Vid { get; set; } = "";
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public double Duration { get; set; }
    public int VideoIdType { get; set; } = -1;
}

/// <summary>清单里的一组（一部剧）。</summary>
public sealed class QueueGroup
{
    public string SeriesId { get; set; } = "";
    public string SeriesTitle { get; set; } = "";
    /// <summary>短剧（批量下载时默认跳过；重建后不能再从收藏夹推断，必须存）。</summary>
    public bool ShortDrama { get; set; }
    public List<QueueEpisode> Episodes { get; set; } = new();
}

/// <summary>
/// 红果侧的下载清单持久化：<c>%USERPROFILE%\.hgv_tool\queue.json</c>。
///
/// ★ 为什么需要：合并后右栏是**两源共用**的清单，兴趣岛的任务会落盘
///   （<c>xqd\jobs.json</c>），红果的原来只在内存 —— 重启后同一张表只剩一半，
///   「共用清单」在重启这一刻是假的。
/// ★ 只存「清单成员」，不存运行状态：重启后一律回到「待下载」，
///   已下载的由 SkipExisting（文件 + 时长）重新判定。
/// </summary>
public sealed class QueueStore
{
    public QueueStore(string path = null)
    {
        Path = string.IsNullOrEmpty(path) ? Paths.QueuePath : path;
    }

    public string Path { get; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public List<QueueGroup> Load()
    {
        try
        {
            if (!File.Exists(Path)) return new List<QueueGroup>();
            var list = JsonSerializer.Deserialize<List<QueueGroup>>(File.ReadAllText(Path), Opts);
            if (list == null) return new List<QueueGroup>();
            return list
                .Where(g => g != null && !string.IsNullOrEmpty(g.SeriesId) && g.Episodes.Count > 0)
                .ToList();
        }
        catch
        {
            // 文件坏了不能让程序起不来 —— 当成空清单
            return new List<QueueGroup>();
        }
    }

    public void Save(IEnumerable<QueueGroup> groups)
    {
        try
        {
            var list = groups.Where(g => g.Episodes.Count > 0).ToList();
            string dir = System.IO.Path.GetDirectoryName(Path) ?? ".";
            Directory.CreateDirectory(dir);
            // 先写临时文件再替换：写到一半断电不会留下半截 JSON
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Opts));
            File.Move(tmp, Path, true);
        }
        catch
        {
            // 存不下不影响下载（与 XqdStore 一致）
        }
    }

    public void Clear()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { }
    }
}

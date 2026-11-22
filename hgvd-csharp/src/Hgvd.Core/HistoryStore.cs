using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hgvd.Core;

/// <summary>
/// 一条下载历史。
/// ★ 字段名刻意与 Python 版（<c>hgvd/engine/history.py</c>）对齐（path/title/episode/
///   series_id/vid/state/size/duration/ts），两个实现共用 <c>history.json</c>；
///   <c>source</c> 是本实现新增的，Python 读的时候会忽略。
/// </summary>
public sealed class HistoryEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("episode")] public string Episode { get; set; } = "";
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("vid")] public string Vid { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("ts")] public double Ts { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";

    [JsonIgnore]
    public string AtText => Ts <= 0
        ? "--"
        : DateTimeOffset.FromUnixTimeSeconds((long)Ts).LocalDateTime.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string SourceText => string.IsNullOrEmpty(Source) ? "—" : Source;

    [JsonIgnore]
    public string SizeText => Size > 0 ? Util.FmtSize(Size) : "--";

    [JsonIgnore]
    public string TitleText => string.IsNullOrEmpty(Episode)
        ? Title
        : (string.IsNullOrEmpty(Title) ? Episode : Title + " · " + Episode);
}

/// <summary>
/// 下载历史（两源共用 <c>%USERPROFILE%\.hgv_tool\history.json</c>）。
/// 最新在前，按成品路径去重，最多 500 条（与 Python 版一致）。
/// </summary>
public sealed class HistoryStore
{
    public const int Max = 500;

    public HistoryStore(string path = null)
    {
        Path = string.IsNullOrEmpty(path) ? Paths.HistoryPath : path;
    }

    public string Path { get; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// ★ 并发下载时多路会同时写历史（读-改-写），必须串行化，否则后写的覆盖先写的丢记录。
    ///   Add 内部调 Load/Save 是重入，所以用同一把锁的 NoLock 私有版本。
    /// </summary>
    private static readonly object WriteLock = new();

    public List<HistoryEntry> Load()
    {
        lock (WriteLock) return LoadNoLock();
    }

    private List<HistoryEntry> LoadNoLock()
    {
        try
        {
            if (!File.Exists(Path)) return new List<HistoryEntry>();
            var list = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(Path), Opts);
            return list?.Where(e => e != null).ToList() ?? new List<HistoryEntry>();
        }
        catch { return new List<HistoryEntry>(); }
    }

    public void Save(IEnumerable<HistoryEntry> items)
    {
        lock (WriteLock) SaveNoLock(items);
    }

    private void SaveNoLock(IEnumerable<HistoryEntry> items)
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path) ?? ".";
            Directory.CreateDirectory(dir);
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items.Take(Max).ToList(), Opts));
            File.Move(tmp, Path, true);
        }
        catch { }
    }

    /// <summary>记一条：同一成品路径的旧记录会被替换，新的排最前。</summary>
    public void Add(HistoryEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Path)) return;
        if (entry.Ts <= 0) entry.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (WriteLock)
        {
            var list = LoadNoLock();
            list.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, entry);
            SaveNoLock(list);
        }
    }

    public void Clear()
    {
        lock (WriteLock) { try { if (File.Exists(Path)) File.Delete(Path); } catch { } }
    }
}

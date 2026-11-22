using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hgvd.Core;

/// <summary>
/// 一条下载记账。按 <see cref="Path"/>（成品绝对路径）索引 —— 不用 vid，
/// 因为换剧名/换账号下同一集 vid 会变，而成品路径才是「这一集下没下」的唯一事实。
/// </summary>
public sealed class ProgressEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    /// <summary>状态：started / done / failed / interrupted。</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("duration")] public double Duration { get; set; }
    /// <summary>直链（仅诊断留档；★ 下次不会拿它下载 —— 会过期）。</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("backup")] public string Backup { get; set; } = "";
    /// <summary>写入时刻（Unix 毫秒）。</summary>
    [JsonPropertyName("ts")] public long Ts { get; set; }
    /// <summary>写入时的账号标识（install_id / user_id）。换号后直链记账作废。</summary>
    [JsonPropertyName("account")] public string Account { get; set; } = "";

    [JsonIgnore]
    public bool IsDone => State == "done";
}

/// <summary>
/// 下载进度记账（<c>%USERPROFILE%\.hgv_tool\progress.json</c>）。
///
/// ★ 用途：下载前一次性预检 —— 记账说完成且磁盘 size 对得上 → 直接跳过，
///   不用每集起一个 ffprobe 进程。实测 100 集已存在 = 串行 4.5s 全花在这。
/// ★ 进度（下了多少字节）**不记在这里** —— .part 文件大小才是权威，
///   记两份会在崩溃后出现两个真相。
/// ★ 写完立即落盘（原子替换）；不设条数上限（一部剧就上百集）。
/// </summary>
public sealed class ProgressStore
{
    public ProgressStore(string path = null)
    {
        Path = string.IsNullOrEmpty(path) ? Paths.ProgressPath : path;
    }

    public string Path { get; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// ★ 并发下载时多路同时写记账（读-改-写），必须串行化，否则后写覆盖先写丢记录。
    ///   Upsert/UpsertMany 内部调 LoadMap/SaveMap 是重入，故用同锁的 NoLock 私有版本。
    /// </summary>
    private static readonly object WriteLock = new();

    /// <summary>读全表，按路径索引（大小写不敏感，Windows 文件系统语义）。</summary>
    public Dictionary<string, ProgressEntry> LoadMap()
    {
        lock (WriteLock) return LoadMapNoLock();
    }

    private Dictionary<string, ProgressEntry> LoadMapNoLock()
    {
        var map = new Dictionary<string, ProgressEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(Path)) return map;
            var list = JsonSerializer.Deserialize<List<ProgressEntry>>(File.ReadAllText(Path), Opts);
            if (list == null) return map;
            foreach (var e in list)
                if (e != null && !string.IsNullOrEmpty(e.Path)) map[e.Path] = e;
        }
        catch { /* 文件坏了当空表，别让程序起不来 */ }
        return map;
    }

    /// <summary>原子替换写全表：先写 .tmp 再 Move，断电不会留下半截 JSON。</summary>
    public void SaveMap(Dictionary<string, ProgressEntry> map)
    {
        lock (WriteLock) SaveMapNoLock(map);
    }

    private void SaveMapNoLock(Dictionary<string, ProgressEntry> map)
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path) ?? ".";
            Directory.CreateDirectory(dir);
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(map.Values.ToList(), Opts));
            File.Move(tmp, Path, true);
        }
        catch { /* 存不下不影响下载 */ }
    }

    /// <summary>记一条（同路径覆盖）。立即落盘 —— 崩溃时至少留有「开始 + 直链」。</summary>
    public void Upsert(ProgressEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Path)) return;
        if (entry.Ts <= 0) entry.Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (WriteLock)
        {
            var map = LoadMapNoLock();
            map[entry.Path] = entry;
            SaveMapNoLock(map);
        }
    }

    /// <summary>批量更新若干条（一次读 + 一次写，避免循环里反复读盘）。</summary>
    public void UpsertMany(IEnumerable<ProgressEntry> entries)
    {
        lock (WriteLock)
        {
            var map = LoadMapNoLock();
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Path)) continue;
                if (e.Ts <= 0) e.Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                map[e.Path] = e;
            }
            SaveMapNoLock(map);
        }
    }

    /// <summary>在锁内对整表做读-改-写（供 MarkInterrupted 等批量状态迁移用）。</summary>
    public void Mutate(Action<Dictionary<string, ProgressEntry>> action)
    {
        if (action == null) return;
        lock (WriteLock)
        {
            var map = LoadMapNoLock();
            action(map);
            SaveMapNoLock(map);
        }
    }
}

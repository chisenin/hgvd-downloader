using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hgvd.Core;

namespace Hgvd.Xqd;

/// <summary>
/// 兴趣岛侧的持久化 —— 全部落在 <c>%USERPROFILE%\.hgv_tool\xqd\</c>：
///   jobs.json      链接清单（标题 / URL / 状态）
///   parts\&lt;16hex&gt;\ 分片缓存（按成品路径的 md5 分目录，跨会话续传）
/// ★ 与参考项目原来的 <c>.xqd_tool</c> 隔离，避免两个工具互相覆盖配置。
/// </summary>
public sealed class XqdStore
{
    public XqdStore(string root = null)
    {
        Root = string.IsNullOrEmpty(root) ? Path.Combine(Paths.ToolDir, "xqd") : root;
    }

    public string Root { get; }
    public string PartsRoot => Path.Combine(Root, "parts");
    public string JobsPath => Path.Combine(Root, "jobs.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string PartsDirFor(string outPath)
    {
        string key = XqdUtil.Md5Hex16(Path.GetFullPath(outPath ?? ""));
        return Path.Combine(PartsRoot, key);
    }

    public List<XqdJob> Load()
    {
        try
        {
            if (!File.Exists(JobsPath)) return new List<XqdJob>();
            var list = JsonSerializer.Deserialize<List<XqdJob>>(File.ReadAllText(JobsPath), Opts);
            if (list == null) return new List<XqdJob>();

            foreach (var j in list)
            {
                if (string.IsNullOrEmpty(j.Id)) j.Id = XqdJob.MakeId(j.Title, j.Url);
                // 上次没跑完的状态一律回到「待下载」，否则会永远显示「下载中」
                if (XqdState.IsRunning(j.State) || j.State == XqdState.Paused)
                    j.State = XqdState.Pending;
            }
            // ★ 课表任务没有 URL（下载时才解析），不能按「有链接」筛掉
            return list.Where(j => !string.IsNullOrEmpty(j.Url) || j.FromCatalog).ToList();
        }
        catch { return new List<XqdJob>(); }
    }

    public void Save(IEnumerable<XqdJob> jobs)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var list = jobs.ToList();
            foreach (var j in list) j.AddedAt = j.AddedAt > 0 ? j.AddedAt : Now();
            File.WriteAllText(JobsPath, JsonSerializer.Serialize(list, Opts));
        }
        catch { /* 存不下不影响下载 */ }
    }

    /// <summary>分片缓存占用（字节），给界面显示「缓存 x MB」。</summary>
    public long PartsBytes()
    {
        try
        {
            if (!Directory.Exists(PartsRoot)) return 0;
            long sum = 0;
            foreach (string f in Directory.EnumerateFiles(PartsRoot, "*", SearchOption.AllDirectories))
            {
                try { sum += new FileInfo(f).Length; } catch { }
            }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>清掉某个成品对应的分片缓存。</summary>
    public void PurgeParts(string outPath)
    {
        try
        {
            string dir = PartsDirFor(outPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}

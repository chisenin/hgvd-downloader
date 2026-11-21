using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Hgvd.Core.Auth;

/// <summary>一台 adb 设备。<c>adb devices -l</c> 的一行。</summary>
public sealed class AdbDevice
{
    public string Serial = "";
    public string State = "";
    public string Model = "";
    public string Product = "";
    public string DevName = "";
    public string Transport = "";

    public bool Online => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    /// <summary>模拟器（雷电/MuMu 都给 emulator-xxxx；网络设备是 ip:port）。</summary>
    public bool IsEmulator => Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase);

    public string Describe
    {
        get
        {
            var parts = new List<string> { Serial, Online ? "在线" : State };
            if (Model.Length > 0) parts.Add(Model);
            return string.Join(" · ", parts.Where(s => s.Length > 0));
        }
    }
}

/// <summary>一次 shell 执行的结果。</summary>
public sealed class ShellResult
{
    public int Code;
    public string Out = "";
    public string Err = "";
    public bool Ok => Code == 0;
    /// <summary>stdout + stderr，排障用。</summary>
    public string All
    {
        get
        {
            if (Err.Length == 0) return Out;
            if (Out.Length == 0) return Err;
            return Out.TrimEnd() + "\n" + Err.TrimEnd();
        }
    }
}

/// <summary>
/// 通用 adb 层：**定位 adb.exe → 列设备 → 执行 shell**。
///
/// ★★ 为什么要单独一层：以前到处是「MuMuManager.exe 的写死路径 + MuMu 专用 shell」，
///    换雷电、换机器、换盘符就全废。这里把所有「跟机器有关的东西」收在一处：
///      · adb.exe 的位置 —— 配置覆盖 / 环境变量 / 正在运行的模拟器进程 / 卸载注册表 /
///        各盘常见安装目录 / PATH，逐级回退，全都不吃写死路径；
///      · 设备选择 —— 用 serial，不用实例号（雷电是 emulator-5554，MuMu 是 ip:port）。
///
/// ★ 命令用 base64 包装再 `| sh`：SQL 里同时有单引号和双引号，
///   直接拼进 <c>su -c "..."</c> 会被设备端 shell 撕碎（实测报 sqlite 语法错）。
///   base64 只有字母数字 +/=，任何引号都不会冲突出。
/// </summary>
public static class AdbDevices
{
    private const string AdbExe = "adb.exe";
    private static readonly string[] EnvKeys = { "HGVD_ADB", "HGV_ADB" };

    private static string _cached;
    private static string _cachedSource;

    /// <summary>清掉缓存（换机器 / 用户改了配置后调用）。</summary>
    public static void Reset()
    {
        _cached = null;
        _cachedSource = null;
    }

    private static string CachePath => Path.Combine(Paths.ToolDir, "adb_path.txt");

    // ---------------------------------------------------------------- 定位 adb

    /// <summary>
    /// 找到 adb.exe。找不到抛异常，异常里带**试过哪些地方**（用户报障时一句话定位）。
    /// </summary>
    public static string FindAdb(string configured = "")
    {
        if (TryFindAdb(configured, out string path, out string trace)) return path;
        throw new InvalidOperationException(
            "没找到 adb.exe（模拟器调试桥）。\n"
            + "· 请确认雷电模拟器已安装并**至少启动过一次**；\n"
            + "· 或在设置里手动指定 adb.exe / 在配置里填 AdbPath；\n"
            + "· 已查找：\n  " + trace);
    }

    /// <summary>试找 adb.exe；<paramref name="trace"/> 记录每个候选来源与结果，便于排障。</summary>
    public static bool TryFindAdb(string configured, out string path, out string trace)
    {
        path = "";
        var traceSb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string p, string src) in Candidates(configured))
        {
            if (!seen.Add((p ?? "").Trim())) continue;
            string hit = AdbIn(p);
            if (hit.Length == 0)
            {
                traceSb.Append(src).Append(" → 未命中\n  ");
                continue;
            }
            traceSb.Append(src).Append(" → ").Append(hit).Append('\n');
            trace = traceSb.ToString().Trim();
            path = hit;
            Remember(hit, src);
            return true;
        }

        trace = traceSb.ToString().Trim();
        return false;
    }

    /// <summary>上次定位到的 adb 来源（界面上显示「用的是哪个 adb」）。</summary>
    public static string LastSource => _cachedSource ?? "";

    private static string ReadCache()
    {
        try
        {
            string s = File.ReadAllText(CachePath).Trim();
            return s.Length > 0 && File.Exists(s) ? s : "";
        }
        catch { return ""; }
    }

    private static void Remember(string path, string source)
    {
        _cached = path;
        _cachedSource = source;
        try
        {
            Directory.CreateDirectory(Paths.ToolDir);
            File.WriteAllText(CachePath, path);
        }
        catch { }
    }

    /// <summary>
    /// 按可靠性排序的候选来源。★ 顺序即优先级。
    ///
    /// ★★ 2026-11-21：本项目**正式支持的模拟器是雷电**（MuMu 降为兜底），
    ///   所以「雷电专属来源」排在缓存和通用来源之前 —— 否则本机装着两个模拟器时
    ///   会随手命中 MuMu 的 adb，两套 adb server 版本不同就容易打架。
    ///   这里排的是**来源优先级**，不是写死路径，换机器照样能探测到。
    /// </summary>
    private static IEnumerable<(string, string)> Candidates(string configured)
    {
        // ① 显式配置（用户/调用方最清楚）
        string cfg = (configured ?? "").Trim().Trim('"');
        if (cfg.Length > 0) yield return (cfg, "配置指定");

        // ② 环境变量：售后/高级用户的最后手段
        foreach (string key in EnvKeys)
        {
            string v = (Environment.GetEnvironmentVariable(key) ?? "").Trim().Trim('"');
            if (v.Length > 0) yield return (v, "环境变量 " + key);
        }

        // ③ 雷电专属：进程 / 注册表 / 安装目录（本项目主用）
        foreach (var c in LdPlayerCandidates()) yield return c;

        // ④ 上次找到的（同一台机器上最快；放在雷电之后，避免旧缓存把主用模拟器顶掉）
        string cache = ReadCache();
        if (cache.Length > 0) yield return (cache, "上次缓存");

        // ⑤ 其他模拟器与通用来源（兜底）
        foreach (var c in FallbackCandidates()) yield return c;
    }

    /// <summary>雷电专属来源。命中任意一条就够，剩下的不会被用到。</summary>
    private static IEnumerable<(string, string)> LdPlayerCandidates()
    {
        // 正在跑的雷电进程：装到哪都躲不掉（MainModule 拿不到就跳过）
        foreach (string p in ProcessHints(new[] { "dnplayer", "ldconsole", "Ld9BoxHeadless", "LdVBoxHeadless" }))
            yield return (p, "雷电进程");

        // 卸载注册表：「雷电模拟器14」的 DisplayIcon 直接指向 dnplayer.exe
        foreach (string p in RegistryHints(IsLdPlayerName))
            yield return (p, "雷电注册表");

        // 各盘常见安装目录（<盘>:\leidian\LDPlayerXX 是官方安装器默认落点）
        foreach (string p in LdFolderHints())
            yield return (p, "雷电安装目录");
    }

    /// <summary>其他模拟器 / PATH 兜底。</summary>
    private static IEnumerable<(string, string)> FallbackCandidates()
    {
        foreach (string p in ProcessHints(new[]
                 { "MuMuNxMain", "MuMuNxDevice", "MuMuPlayer", "NemuPlayer", "MuMuManager" }))
            yield return (p, "其他模拟器进程");

        foreach (string p in RegistryHints(LooksLikeEmulator))
            yield return (p, "其他模拟器注册表");

        foreach (string p in OtherFolderHints())
            yield return (p, "其他常见目录");

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            string d = dir.Trim().Trim('"');
            if (d.Length == 0) continue;
            yield return (d, "PATH");
        }
    }

    /// <summary>
    /// 从一个**提示路径**（可能是 adb.exe 本身、模拟器主程序、或任意目录）推出 adb.exe。
    /// 找不到返回空串。
    /// </summary>
    public static string AdbIn(string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return "";
        try
        {
            hint = hint.Trim().Trim('"');

            if (File.Exists(hint))
            {
                if (string.Equals(Path.GetFileName(hint), AdbExe, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(hint);
                // 主程序 exe → 它的目录（雷电 dnplayer.exe 与 adb.exe 同目录）
                string dirOfExe = Path.GetDirectoryName(hint) ?? "";
                foreach (string c in InDir(dirOfExe)) if (File.Exists(c)) return c;
            }

            if (Directory.Exists(hint))
                foreach (string c in InDir(hint)) if (File.Exists(c)) return c;

            // 兜底：当作「反斜杠结尾的目录」再试一次
            if (!hint.EndsWith("\\", StringComparison.Ordinal) && Directory.Exists(hint + "\\"))
                foreach (string c in InDir(hint + "\\")) if (File.Exists(c)) return c;
        }
        catch { }
        return "";
    }

    private static IEnumerable<string> InDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) yield break;
        yield return Path.Combine(dir, AdbExe);
        yield return Path.Combine(dir, "platform-tools", AdbExe);
        yield return Path.Combine(dir, "nx_main", AdbExe);
        // 主程序在子目录时（如 <root>\shell\drivers），回上一层找
        string up = Path.GetDirectoryName(dir.TrimEnd('\\')) ?? "";
        if (up.Length > 0 && !string.Equals(up, dir, StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(up, AdbExe);
            yield return Path.Combine(up, "nx_main", AdbExe);
        }
    }

    /// <summary>指定进程名 → 主程序路径（拿不到 MainModule 时静默跳过）。</summary>
    private static IEnumerable<string> ProcessHints(string[] names)
    {
        foreach (string name in names)
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (Process p in ps)
            {
                string file = "";
                try { file = p.MainModule != null ? p.MainModule.FileName : ""; }
                catch { }
                finally { p.Dispose(); }
                if (file.Length > 0) yield return file;
            }
        }
    }

    /// <summary>卸载注册表里 DisplayName 命中 <paramref name="match"/> 的项 → InstallLocation / DisplayIcon。</summary>
    private static IEnumerable<string> RegistryHints(Func<string, bool> match)
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows() || match == null) return list;

        string[] keys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        RegistryKey[] hives = { Registry.LocalMachine, Registry.CurrentUser };

        foreach (RegistryKey hive in hives)
        {
            foreach (string sub in keys)
            {
                RegistryKey root = null;
                try { root = hive.OpenSubKey(sub); } catch { }
                if (root == null) continue;

                string[] names;
                try { names = root.GetSubKeyNames(); }
                catch { names = Array.Empty<string>(); }

                foreach (string name in names)
                {
                    RegistryKey app = null;
                    try
                    {
                        app = root.OpenSubKey(name);
                        string display = (app?.GetValue("DisplayName") as string) ?? "";
                        if (!match(display)) continue;

                        string loc = (app.GetValue("InstallLocation") as string) ?? "";
                        if (loc.Length > 0) list.Add(loc);

                        string icon = (app.GetValue("DisplayIcon") as string) ?? "";
                        if (icon.Length > 0) list.Add(icon);
                    }
                    catch { }
                    finally { app?.Dispose(); }
                }
                root.Dispose();
            }
        }
        return list;
    }

    private static bool LooksLikeEmulator(string display)
    {
        if (string.IsNullOrEmpty(display)) return false;
        string d = display.ToLowerInvariant();
        return d.Contains("雷电") || d.Contains("ldplayer") || d.Contains("mumu")
               || d.Contains("nemu") || d.Contains("模拟器");
    }

    /// <summary>注册表 DisplayName 是否是雷电（「雷电模拟器14」/ LDPlayer）。</summary>
    private static bool IsLdPlayerName(string display)
    {
        if (string.IsNullOrEmpty(display)) return false;
        string d = display.ToLowerInvariant();
        return d.Contains("雷电") || d.Contains("ldplayer") || d.Contains("xuanzhi");
    }

    /// <summary>
    /// 雷电常见目录。★ 只做**浅层**枚举（不递归整盘），避免启动变慢。
    /// 覆盖：&lt;盘&gt;:\leidian\LDPlayerXX、&lt;盘&gt;:\LDPlayer*、Program Files\LDPlayer* / XuanZhi
    /// </summary>
    private static IEnumerable<string> LdFolderHints()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        foreach (string root in SafeDriveRoots())
        {
            foreach (string sub in SafeDirs(root, "leidian")) yield return sub;
            foreach (string sub in SafeDirs(root, "LDPlayer*")) yield return sub;

            foreach (string pf in new[] { "Program Files", "Program Files (x86)" })
            {
                string baseDir = Path.Combine(root, pf);
                foreach (string sub in SafeDirs(baseDir, "LDPlayer*")) yield return sub;
                foreach (string sub in SafeDirs(baseDir, "XuanZhi")) yield return sub;
            }
        }
    }

    /// <summary>其他模拟器（MuMu/Nox 等）的常见目录，仅作兜底。</summary>
    private static IEnumerable<string> OtherFolderHints()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        foreach (string root in SafeDriveRoots())
        {
            foreach (string sub in SafeDirs(root, "Netease")) yield return sub;
            foreach (string sub in SafeDirs(root, "MuMu*")) yield return sub;
            foreach (string sub in SafeDirs(root, "Nox*")) yield return sub;

            foreach (string pf in new[] { "Program Files", "Program Files (x86)" })
            {
                string baseDir = Path.Combine(root, pf);
                foreach (string sub in SafeDirs(baseDir, "Netease")) yield return sub;
                foreach (string sub in SafeDirs(baseDir, "MuMu*")) yield return sub;
                foreach (string sub in SafeDirs(baseDir, "Nox")) yield return sub;
            }
        }
    }

    private static string[] SafeDriveRoots()
    {
        var list = new List<string>();
        try
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    list.Add(d.RootDirectory.FullName);
                }
                catch { }
            }
        }
        catch { }
        return list.ToArray();
    }

    /// <summary>★ 不能用 yield 直接写在 try/catch 里（CS1626），先收进 list 再返回。</summary>
    private static IEnumerable<string> SafeDirs(string path, string pattern)
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(path))
            {
                try { list.AddRange(Directory.GetDirectories(path, pattern)); }
                catch { }
            }
        }
        catch { }
        return list;
    }

    // ---------------------------------------------------------------- 列设备

    /// <summary>列设备（解析 <c>adb devices -l</c>）。</summary>
    public static List<AdbDevice> List(string adb = "", string configured = "", int timeoutMs = 25000)
    {
        string exe = adb.Length > 0 ? adb : FindAdb(configured);
        var result = new List<AdbDevice>();
        if (exe.Length == 0) return result;
        ShellResult r = Run(exe, new[] { "devices", "-l" }, timeoutMs);
        return ParseDevices(r.All);
    }

    /// <summary>
    /// 解析 <c>adb devices -l</c> 的文本（抽出来是为了能离线回归，不碰真设备）。
    /// 行形如：<c>emulator-5554  device product:X model:Y device:Z transport_id:6</c>
    /// </summary>
    public static List<AdbDevice> ParseDevices(string text)
    {
        var result = new List<AdbDevice>();
        if (string.IsNullOrEmpty(text)) return result;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("* daemon", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("adb server", StringComparison.OrdinalIgnoreCase)) continue;

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var dev = new AdbDevice { Serial = parts[0], State = parts[1] };
            for (int i = 2; i < parts.Length; i++)
            {
                string kv = parts[i];
                int c = kv.IndexOf(':');
                if (c <= 0) continue;
                string k = kv.Substring(0, c), v = kv.Substring(c + 1);
                switch (k)
                {
                    case "model": dev.Model = v; break;
                    case "product": dev.Product = v; break;
                    case "device": dev.DevName = v; break;
                    case "transport_id": dev.Transport = v; break;
                }
            }
            result.Add(dev);
        }
        return result;
    }

    /// <summary>
    /// 选一台可用设备：**优先模拟器（emulator-*）**，其次任意在线设备。
    /// 多台模拟器时取 serial 排序最小的（稳定，不会每次换）。
    /// </summary>
    public static AdbDevice Pick(IEnumerable<AdbDevice> devices, string preferSerial = "")
    {
        var online = (devices ?? Enumerable.Empty<AdbDevice>()).Where(d => d.Online).ToList();
        if (online.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferSerial))
        {
            var exact = online.FirstOrDefault(
                d => string.Equals(d.Serial, preferSerial.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        var emu = online.Where(d => d.IsEmulator).OrderBy(d => d.Serial, StringComparer.Ordinal).ToList();
        if (emu.Count > 0) return emu[0];
        return online.OrderBy(d => d.Serial, StringComparer.Ordinal).First();
    }

    // ---------------------------------------------------------------- 执行

    /// <summary>
    /// 执行一条 shell 命令。
    /// <paramref name="root"/> = true 时套 <c>su -c</c>（雷电/MuMu 都可），失败不抛、由调用方看 Code/Err。
    /// </summary>
    public static ShellResult Shell(string serial, string cmd, bool root = false,
                                    int timeoutMs = 30000, string adb = "", string configured = "")
    {
        if (string.IsNullOrWhiteSpace(serial))
            return new ShellResult { Code = -1, Err = "没有指定 adb 设备（serial 为空）" };

        string exe;
        try { exe = adb.Length > 0 ? adb : FindAdb(configured); }
        catch (Exception e) { return new ShellResult { Code = -1, Err = e.Message }; }

        string pipeline = Base64Pipe(cmd);
        string deviceCmd = root
            ? "su -c \"echo " + pipeline + "\""
            : "echo " + pipeline;

        return Run(exe, new[] { "-s", serial, "shell", deviceCmd }, timeoutMs);
    }

    /// <summary><c>base64 -d | sh</c> 包装：命令里任何引号都不会被设备端 shell 撕碎。</summary>
    public static string Base64Pipe(string cmd)
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cmd ?? ""));
        return b64 + " | base64 -d | sh";
    }

    /// <summary>该设备有没有 root（能 `su -c id` 拿到 uid=0）。</summary>
    public static bool HasRoot(string serial, string adb = "", string configured = "")
    {
        ShellResult r = Shell(serial, "id", root: true, timeoutMs: 12000, adb: adb, configured: configured);
        return r.Ok && r.Out.Contains("uid=0");
    }

    private static ShellResult Run(string exe, string[] args, int timeoutMs)
    {
        var r = new ShellResult();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        Process p = null;
        try
        {
            p = Process.Start(psi);
            if (p == null) { r.Code = -1; r.Err = "启动失败：" + exe; return r; }

            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                r.Code = -2;
                r.Err = "超时（" + timeoutMs + "ms）";
                r.Out = outSb.ToString();
                return r;
            }
            try { p.WaitForExit(); } catch { }

            r.Code = p.ExitCode;
            r.Out = outSb.ToString();
            r.Err = errSb.ToString();
            return r;
        }
        catch (Exception e)
        {
            r.Code = -1;
            r.Err = e.Message;
            return r;
        }
        finally { p?.Dispose(); }
    }
}

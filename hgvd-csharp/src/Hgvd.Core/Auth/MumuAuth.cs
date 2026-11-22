using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Hgvd.Core.Sign;

namespace Hgvd.Core.Auth;

/// <summary>
/// 从 MuMu 模拟器里读红果 App 的登录态。
///
/// ★ 这比扫码快得多，也不用过滑块 —— 你本来就在 App 里收藏剧，App 一定是登录的。
/// ★ 读取方式：MuMu 的**内部 RPC**（<c>MuMuManager.exe sh -v N -c "..."</c>），
///   不是 adbd、不是 root shell。
/// ★ 登录态在 App 的 shared_prefs 里，值是**明文**：
///   - cookieStore.xml           → sessionid / sid_tt / install_id / odin_tt
///   - token_shared_preference.xml → X-Tt-Token
///   - applog_stats.xml          → device_id
/// ★ `app_webview/Default/Cookies` 里**只有** passport_csrf_token，没有 sessionid —— 别去那里找。
/// </summary>
public static class MumuAuth
{
    public const string Package = "com.phoenix.read";
    private const string DataRoot = "/data/data/com.phoenix.read";
    private const string Prefs = DataRoot + "/shared_prefs";
    private const string ManagerExe = "MuMuManager.exe";

    /// <summary>
    /// ★★ MuMu 的安装目录**因机器而异**：MuMu 6 是 Netease\MuMu，
    ///    MuMu Player 12 是 Netease\MuMuPlayer-12.0，还有装在盘根或
    ///    Program Files\MuMu Player 12 的。旧代码只认一个写死路径
    ///    → 本机「找不到 MuMuManager.exe」→ 拿不到授权。
    /// </summary>
    private static readonly string[] RelativeCandidates =
    {
        Path.Combine("nx_main", ManagerExe),
        Path.Combine("shell", ManagerExe),
        Path.Combine("nx_device", "shell", ManagerExe),
        ManagerExe,
    };

    /// <summary>显式覆盖用环境变量（装到非常规目录时最省事）。</summary>
    private static readonly string[] EnvOverrides =
    {
        "HGVD_MUMU_MANAGER", "HGV_MUMU_MANAGER", "MUMU_MANAGER",
    };

    /// <summary>模拟器实例信息。</summary>
    public sealed class Instance
    {
        public int Index;
        public bool Started;
        public string Name = "";
    }

    /// <summary>
    /// 定位 MuMuManager.exe。
    ///
    /// ★ 面向**公开发布**：使用者多、安装位置千变万化，不能依赖用户设环境变量。
    ///   官方安装包一定会留下这些痕迹，按可靠性排序：
    ///   ① 上次找到的缓存 → ② 正在运行的 MuMu 进程（授权时模拟器必然开着，最权威）
    ///   → ③ 开始菜单/桌面的快捷方式（安装包必建，且直接指向 nx_main）
    ///   → ④ 卸载注册表 InstallLocation → ⑤ 各盘 Program Files / Netease / 盘根枚举
    ///   → ⑥ PATH → ⑦ 环境变量（仅给售后/高级用户用）。
    /// </summary>
    public static string FindMumu()
    {
        foreach (string hint in HintCandidates())
        {
            string hit = ManagerIn(hint);
            if (hit.Length > 0) { Remember(hit); return hit; }
        }

        throw new InvalidOperationException(
            "没找到 MuMu 模拟器（MuMuManager.exe）。\n"
            + "· 请确认已安装网易 MuMu，并且**至少启动过一次**（安装包会建快捷方式/注册表项）；\n"
            + "· 若装在非常规目录，可在本程序里点「手动指定」选到 MuMuManager.exe，只需选一次。");
    }

    /// <summary>把用户手动选中的 MuMuManager.exe 记下来（下次直接用）。</summary>
    public static bool SetManagerPath(string path)
    {
        path = CleanHint(path ?? "");
        if (!File.Exists(path)) return false;
        if (!string.Equals(Path.GetFileName(path), ManagerExe, StringComparison.OrdinalIgnoreCase))
            return false;
        Remember(path);
        return true;
    }

    // ---------------------------------------------------------- 查找实现

    private static string CachePath => Path.Combine(Paths.ToolDir, "mumu_manager.txt");

    private static string ReadCache()
    {
        try
        {
            string s = File.ReadAllText(CachePath).Trim();
            return s.Length > 0 && File.Exists(s) ? s : "";
        }
        catch { return ""; }
    }

    private static void Remember(string path)
    {
        try
        {
            Directory.CreateDirectory(Paths.ToolDir);
            File.WriteAllText(CachePath, path);
        }
        catch { }
    }

    private static IEnumerable<string> HintCandidates()
    {
        string cache = ReadCache();
        if (cache.Length > 0) yield return cache;

        foreach (string h in ProcessHints()) yield return h;
        foreach (string h in ShortcutHints()) yield return h;
        foreach (string h in RegistryHints()) yield return h;
        foreach (string h in FolderHints()) yield return h;

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            string d = dir.Trim().Trim('"');
            if (d.Length == 0) continue;
            string p = Path.Combine(d, ManagerExe);
            if (File.Exists(p)) yield return p;
        }

        // ⑦ 环境变量：只作为高级用户/售后的最后一根稻草
        foreach (string env in EnvOverrides)
        {
            string v = (Environment.GetEnvironmentVariable(env) ?? "").Trim();
            if (v.Length > 0) yield return CleanHint(v);
        }
    }

    /// <summary>
    /// 正在运行的 MuMu 进程 → exe 路径。
    /// ★ 授权的前提就是模拟器开着，所以这条最可靠；
    ///   即使 MuMu 装在任意奇怪目录也能拿到。若拿不到（权限不足）就跳过。
    /// </summary>
    private static IEnumerable<string> ProcessHints()
    {
        string[] names =
        {
            "MuMuNxMain", "MuMuNxDevice", "MuMuPlayer", "MuMuMultiPlayer",
            "MuMuManager", "NemuPlayer",
        };
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

    /// <summary>
    /// 开始菜单 / 桌面快捷方式 → TargetPath。
    /// ★ 官方安装包必建快捷方式（本机实测
    ///   <c>…\Start Menu\Programs\MuMu模拟器\MuMu模拟器.lnk</c> → <c>…\nx_main\MuMuNxMain.exe</c>），
    ///   即使 MuMu 装在 D:\某个人建的目录里也能命中。
    /// </summary>
    private static IEnumerable<string> ShortcutHints()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string[] dirs =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        foreach (string target in ShortcutTargets(dirs)) yield return target;
    }

    /// <summary>
    /// 给定若干目录（递归），把名字含 MuMu 的 .lnk 解析成 TargetPath。
    /// ★ 抽出来是为了能用临时目录 + 真实 .lnk 做回归（ShortcutHints 扫的是系统目录）。
    /// </summary>
    internal static List<string> ShortcutTargets(IEnumerable<string> dirs)
    {
        var targets = new List<string>();
        if (!OperatingSystem.IsWindows()) return targets;

        object shell = null;
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t != null) shell = Activator.CreateInstance(t);
        }
        catch { }
        if (shell == null) return targets;

        foreach (string dir in dirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (string link in files)
            {
                string nm = Path.GetFileNameWithoutExtension(link) ?? "";
                if (!nm.ToLowerInvariant().Contains("mumu")) continue;
                string target = ResolveShortcut(shell, link);
                if (target.Length > 0) targets.Add(target);
            }
        }
        return targets;
    }

    /// <summary>用 WScript.Shell 解析 .lnk 的 TargetPath（失败返回空）。</summary>
    /// <remarks>
    /// ★ 这里**刻意不用 <c>dynamic</c>**：dynamic 会把 <c>Microsoft.CSharp</c> 的运行时
    ///   binder 拖进来，而 binder 靠大量反射去找成员 —— IL 裁剪（<c>PublishTrimmed</c>）
    ///   下是典型盲区，裁掉了要到运行时才抛 <c>MissingMethodException</c>。
    ///   本方法只需要「调一个方法、读一个属性」，用 <c>InvokeMember</c> 足够，
    ///   而且整条调用链对 linker 是静态可见的。
    /// </remarks>
    private static string ResolveShortcut(object shell, string link)
    {
        if (shell == null) return "";
        object sc = null;
        try
        {
            sc = shell.GetType().InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
            if (sc == null) return "";
            object target = sc.GetType().InvokeMember(
                "TargetPath", BindingFlags.GetProperty, null, sc, null);
            return target as string ?? "";
        }
        catch { return ""; }
        finally
        {
            // WScript.Shell 每次 CreateShortcut 都返回一个新的 COM 对象，用完必须放
            if (sc != null && Marshal.IsComObject(sc))
            {
                try { Marshal.ReleaseComObject(sc); } catch { }
            }
        }
    }

    /// <summary>注册表里的安装位置/图标/卸载命令行清洗成纯路径。</summary>
    internal static string CleanHint(string raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            if (end > 0) s = s.Substring(1, end - 1);
        }
        if (s.Contains(',') && !File.Exists(s) && !Directory.Exists(s))
        {
            // DisplayIcon 形如 ``D:\...\uninstall.exe,0``
            s = s.Substring(0, s.LastIndexOf(',')).Trim().Trim('"');
        }
        if (s.Contains(" -") && !File.Exists(s) && !Directory.Exists(s))
            s = s.Substring(0, s.IndexOf(" -", StringComparison.Ordinal)).Trim().Trim('"');
        return s;
    }

    /// <summary>在给定目录（或它的上 N 层父目录）里找 MuMuManager.exe。</summary>
    internal static string ManagerIn(string root, int levels = 3)
    {
        if (string.IsNullOrWhiteSpace(root)) return "";
        DirectoryInfo dir;
        try
        {
            var fi = new FileInfo(root);
            if (fi.Exists)
            {
                if (string.Equals(fi.Name, ManagerExe, StringComparison.OrdinalIgnoreCase))
                    return fi.FullName;
                dir = fi.Directory!;
            }
            else
            {
                if (!Directory.Exists(root)) return "";
                dir = new DirectoryInfo(root);
            }
        }
        catch { return ""; }

        for (int i = 0; i <= levels && dir != null; i++)
        {
            foreach (string rel in RelativeCandidates)
            {
                string cand = Path.Combine(dir.FullName, rel);
                if (File.Exists(cand)) return cand;
            }
            dir = dir.Parent;
        }
        return "";
    }

    /// <summary>卸载注册表里 DisplayName 含 MuMu 的条目 → 安装路径候选。</summary>
    private static IEnumerable<string> RegistryHints()
    {
        // ★ 平台守卫：注册表只在 Windows 有（也顺便消掉 CA1416 警告）
        if (!OperatingSystem.IsWindows()) yield break;
        RegistryKey[] hives = { Registry.LocalMachine, Registry.CurrentUser };
        string[] subs =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\Netease",
            @"SOFTWARE\WOW6432Node\Netease",
        };
        foreach (RegistryKey hive in hives)
        {
            foreach (string sub in subs)
            {
                RegistryKey key = null;
                try { key = hive.OpenSubKey(sub); } catch { }
                if (key == null) continue;
                try
                {
                    foreach (string name in key.GetSubKeyNames())
                    {
                        RegistryKey sk = null;
                        try { sk = key.OpenSubKey(name); } catch { }
                        if (sk == null) continue;
                        try
                        {
                            string display = "";
                            try { display = (sk.GetValue("DisplayName") as string) ?? ""; } catch { }
                            // Netease 分支没有 DisplayName，用子键名兜底判断
                            if (!((display + " " + name).ToLowerInvariant().Contains("mumu")))
                                continue;
                            foreach (string vn in new[]
                            {
                                "InstallLocation", "DisplayIcon", "UninstallString",
                                "InstallPath", "Path",
                            })
                            {
                                string val = "";
                                try { val = (sk.GetValue(vn) as string) ?? ""; } catch { }
                                val = CleanHint(val);
                                if (val.Length > 0) yield return val;
                            }
                        }
                        finally { sk.Close(); }
                    }
                }
                finally { key.Close(); }
            }
        }
    }

    /// <summary>各盘 Program Files / Netease / 盘根下名字含 MuMu 的目录。</summary>
    private static IEnumerable<string> FolderHints()
    {
        var parents = new List<string>();
        string pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? "";
        string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "";
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        if (pf.Length > 0) parents.Add(pf);
        if (pf86.Length > 0) parents.Add(pf86);
        if (local.Length > 0) parents.Add(Path.Combine(local, "Programs"));

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string r = drive.RootDirectory.FullName;
                parents.Add(r);
                parents.Add(Path.Combine(r, "Program Files"));
                parents.Add(Path.Combine(r, "Program Files (x86)"));
                parents.Add(Path.Combine(r, "Programs"));
            }
            catch { }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string parent in parents)
        {
            if (string.IsNullOrEmpty(parent) || !seen.Add(parent) || !Directory.Exists(parent))
                continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(parent); }
            catch { continue; }
            foreach (string full in dirs)
            {
                string name = Path.GetFileName(full) ?? "";
                string low = name.ToLowerInvariant();
                if (low.Contains("mumu") || low == "nemu")
                {
                    yield return full;
                }
                else if (low == "netease")
                {
                    string[] subdirs;
                    try { subdirs = Directory.GetDirectories(full); }
                    catch { continue; }
                    foreach (string sub in subdirs)
                    {
                        string sname = Path.GetFileName(sub) ?? "";
                        if (sname.ToLowerInvariant().Contains("mumu")) yield return sub;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 执行 MuMu 内部 RPC 的 sh 命令并返回 stdout。
    /// ★ 给兴趣岛侧复用：它要读自己 App 的 WebView Cookie 库，
    ///   定位 MuMu / 列实例 / 执行命令这套逻辑没必要抄两遍。
    /// </summary>
    public static string RunShell(string mumu, int index, string cmd, int timeoutMs = 60000)
        => Sh(mumu, index, cmd, timeoutMs);

    /// <summary>
    /// 试找 MuMu；找不到返回空串（不抛）。
    /// ★ 2026-11-21 起本项目**正式模拟器是雷电**，MuMu 只是兜底，
    ///   调用方不该再因为「没装 MuMu」直接失败。
    /// </summary>
    public static string TryFindMumu()
    {
        try { return FindMumu(); } catch { return ""; }
    }

    /// <summary>MuMu 路径是否可用（空串/文件不存在都算不可用）。</summary>
    private static bool Usable(string mumu)
    {
        try { return !string.IsNullOrWhiteSpace(mumu) && File.Exists(mumu); }
        catch { return false; }
    }

    private static string Sh(string mumu, int index, string cmd, int timeoutMs = 30000)
    {
        // ★ 兼容层：MuMu 不在（已改用雷电）→ 同样的 shell 命令走 adb。
        if (!Usable(mumu)) return ShViaAdb(cmd, timeoutMs);

        var psi = new ProcessStartInfo(mumu)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "sh", "-v", index.ToString(), "-c", cmd })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("启动 MuMuManager 失败");
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.Append(e.Data).Append('\n'); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(); } catch { } }
        return sb.ToString();
    }

    /// <summary>
    /// adb 兼容执行（雷电）：命令与 MuMu 那条完全一样（cat prefs / am start / settings put），
    /// 所以上层的登录态读取、App 启动等调用都能原样跑。
    /// ★ 先试 root（读 <c>/data/data/&lt;pkg&gt;</c> 必须 root），失败再退回普通 shell。
    /// </summary>
    private static string ShViaAdb(string cmd, int timeoutMs)
    {
        try
        {
            AppConfig cfg = AppConfig.Load();
            string adb = AdbDevices.FindAdb(cfg.AdbPath);
            AdbDevice dev = AdbDevices.Pick(AdbDevices.List(adb, cfg.AdbPath), cfg.AdbSerial);
            if (dev == null) return "";

            ShellResult r = AdbDevices.Shell(dev.Serial, cmd, root: true, timeoutMs: timeoutMs, adb: adb);
            if (r.Out.Trim().Length == 0)
                r = AdbDevices.Shell(dev.Serial, cmd, root: false, timeoutMs: timeoutMs, adb: adb);
            return r.Out;
        }
        catch { return ""; }
    }

    public static List<Instance> ListInstances(string mumu)
    {
        var list = new List<Instance>();

        // ★ 兼容层：没有 MuMu → 用 adb 设备（雷电）造一个虚拟实例。
        //   实例号只用来拼 RunShell 的命令，adb 分支会忽略它，所以固定 0 即可。
        if (!Usable(mumu))
        {
            try
            {
                AppConfig cfg = AppConfig.Load();
                AdbDevice dev = AdbDevices.Pick(AdbDevices.List("", cfg.AdbPath), cfg.AdbSerial);
                if (dev != null)
                    list.Add(new Instance { Index = 0, Started = true, Name = dev.Serial });
            }
            catch { }
            return list;
        }

        // MuMuManager 的 info 命令返回 JSON
        // ★ 0~7 全探一遍，不能「前面没启动就收工」—— 用户可能只开了实例 2。
        for (int i = 0; i < 8; i++)
        {
            string txt;
            try { txt = RunInfo(mumu, i); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(txt) || !txt.Contains("\"index\"")) continue;

            int index = ExtractInt(txt, "index") ?? i;
            bool started = ExtractBool(txt, "is_android_started") ?? false;
            string name = ExtractString(txt, "name") ?? $"实例 {index}";
            list.Add(new Instance { Index = index, Started = started, Name = name });
        }
        return list;
    }

    private static string RunInfo(string mumu, int index)
    {
        var psi = new ProcessStartInfo(mumu)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("info");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add(index.ToString());
        using var proc = Process.Start(psi)!;
        string s = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(20000);
        return s;
    }

    private static int? ExtractInt(string json, string key)
    {
        // ★ MuMu 的 index 是字符串形式的：``"index": "1"``，所以要允许引号
        var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"?(\\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static bool? ExtractBool(string json, string key)
    {
        var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*(true|false)");
        return m.Success ? m.Groups[1].Value == "true" : null;
    }

    private static string ExtractString(string json, string key)
    {
        var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// 读取 App 的登录态。
    /// </summary>
    /// <returns>cookie 字典 + 设备身份。</returns>
    public static (Dictionary<string, string> Cookies, DeviceIdentity Device)
        ReadAppState(int index, string mumu)
    {
        // ★★ 必须用**绝对路径**：MuMu 的内部 RPC 不是以 /data/data 为当前目录，
        //    相对路径 ``com.phoenix.read/...`` 会静默返回空（实测踩过，授权失败的主因之一）。
        string cs = Sh(mumu, index, $"cat {Prefs}/cookieStore.xml 2>/dev/null");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException($"实例 {index} 里读不到 cookieStore.xml（App 装了吗？）");

        string token = Sh(mumu, index,
            $"cat {Prefs}/token_shared_preference.xml 2>/dev/null");
        string stats = Sh(mumu, index,
            $"cat {Prefs}/applog_stats.xml 2>/dev/null");

        var cookies = new Dictionary<string, string>();
        foreach (string name in new[] { "sessionid", "sid_tt", "install_id", "odin_tt" })
        {
            string v = ExtractPrefValue(cs, name);
            if (!string.IsNullOrEmpty(v)) cookies[name] = v;
        }
        if (!cookies.ContainsKey("sessionid"))
            throw new InvalidOperationException($"实例 {index} 的 App 没有登录态（没有 sessionid）");

        string csrf = ExtractPrefValue(cs, "passport_csrf_token");
        if (string.IsNullOrEmpty(csrf))
            csrf = ExtractPrefValue(token, "passport_csrf_token");
        if (string.IsNullOrEmpty(csrf))
        {
            // ★ CsrfTokenManager_sp.xml 里是**明文** ``<string name="csrf_token">…</string>``，
            //   不是 Java 序列化的十六进制（不能走 ExtractPrefValue）。
            csrf = SimplePrefValue(
                Sh(mumu, index, $"cat {Prefs}/CsrfTokenManager_sp.xml 2>/dev/null"),
                "csrf_token");
        }
        if (!string.IsNullOrEmpty(csrf)) cookies["passport_csrf_token"] = csrf;

        var device = new DeviceIdentity
        {
            Iid = cookies.TryGetValue("install_id", out var iid) ? iid : "",
            DeviceId = ExtractPrefValue(stats, "device_id"),
            Cdid = "",
            Openudid = "",
            VersionName = "7.3.9.32",
            VersionCode = "73932",
        };
        return (cookies, device);
    }

    /// <summary>普通 prefs XML（``<string name="k">v</string>``，值是明文）。</summary>
    private static string SimplePrefValue(string xml, string key)
    {
        if (string.IsNullOrEmpty(xml)) return "";
        var m = Regex.Match(xml, $"name=\"{Regex.Escape(key)}\">([^<]*)<");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>
    /// 从 shared_prefs 的 XML 里取某个键的值。
    /// ★ App 用的是 **Java 序列化 + XML 转义 + 十六进制** 三重包装，
    ///   所以要：XML 实体解码 → hex 解码 → 在字节里找可打印 ASCII。
    /// </summary>
    public static string ExtractPrefValue(string xml, string name)
    {
        if (string.IsNullOrEmpty(xml)) return "";
        string decoded = System.Net.WebUtility.HtmlDecode(xml);

        var m = Regex.Match(decoded, $@"\|{Regex.Escape(name)}""[^>]*>(.*?)</string>",
            RegexOptions.Singleline);
        if (!m.Success) return "";

        string hex = m.Groups[1].Value.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        if (hex.Length < 2 || hex.Length % 2 != 0) return "";
        try
        {
            byte[] bytes = Convert.FromHexString(hex);
            // 在字节里找长度 >= 8 的十六进制/字母数字串（sessionid / install_id 都是十六进制）
            var sb = new StringBuilder();
            var cur = new StringBuilder();
            foreach (byte b in bytes)
            {
                char c = (char)b;
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (ok) cur.Append(c);
                else
                {
                    if (cur.Length >= 8) { sb.Append(cur); break; }
                    cur.Clear();
                }
            }
            if (sb.Length == 0 && cur.Length >= 8) sb.Append(cur);
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>候选实例（已读到 cookie + 设备身份）。</summary>
    public sealed class Candidate
    {
        public int Index;
        public Dictionary<string, string> Cookies = new();
        public DeviceIdentity Device = new DeviceIdentity();
        public string Stamp = "";
        public AccountProbe Probe;

        public bool HasSession =>
            Cookies.TryGetValue("sessionid", out string sid) && !string.IsNullOrEmpty(sid);
    }

    /// <summary>一次「授权是否还有效」的在线探测结果。</summary>
    public sealed class AccountProbe
    {
        public bool Valid;
        /// <summary>网络/节点问题 → 不能据此判定授权失效。</summary>
        public bool Unknown;
        public string UserId = "";
        public string UserName = "";
        public string Error = "";

        public string Describe() =>
            Valid ? $"✓ 授权有效 uid={UserId}"
                  : (Unknown ? $"⚠ 无法在线验证（{Error}）" : $"✗ 授权失效（{Error}）");
    }

    /// <summary>
    /// 用 cookie 打一次 <c>passport/account/info</c>，判断这份授权到底还能不能用。
    /// ★ 多开时每台都可能装着已登录的红果，只按 mtime 猜会挑错；
    ///   服务端认不认这个 sessionid 才是唯一可靠判据（有效时还能顺带拿到 uid/昵称）。
    /// </summary>
    public static AccountProbe ProbeAccount(Dictionary<string, string> cookies)
    {
        var probe = new AccountProbe();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://hongguoduanju.com/");

            string url = "https://hongguoduanju.com/passport/account/info/v2/"
                + "?aid=513755&passport_jssdk_version=3.4.10&passport_jssdk_type=normal"
                + "&account_sdk_source=web&is_from_ttaccountsdk=1&language=zh&next="
                + Uri.EscapeDataString("https://hongguoduanju.com/");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Cookie",
                string.Join("; ", cookies.Where(kv => !string.IsNullOrEmpty(kv.Value))
                    .Select(kv => $"{kv.Key}={kv.Value}")));
            if (cookies.TryGetValue("passport_csrf_token", out string csrf) && csrf.Length > 0)
                req.Headers.TryAddWithoutValidation("x-tt-passport-csrf-token", csrf);

            using var resp = http.Send(req);
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                probe.Unknown = true;
                probe.Error = $"HTTP {(int)resp.StatusCode}";
                return probe;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d0) ? d0 : default;

            // ★ 字段可能是字符串也可能是数字（uid 就是长整型），两种都要接
            static string JStr(JsonElement obj, string name)
            {
                if (obj.ValueKind != JsonValueKind.Object
                    || !obj.TryGetProperty(name, out var v)) return "";
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.Number => v.GetRawText(),
                    _ => "",
                };
            }

            int errorCode = 0;
            string uid = "", name = "";
            if (data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("error_code", out var ec))
                {
                    if (ec.ValueKind == JsonValueKind.Number) errorCode = (int)ec.GetInt64();
                    else if (ec.ValueKind == JsonValueKind.String)
                        int.TryParse(ec.GetString(), out errorCode);
                }
                uid = JStr(data, "biz_user_id");
                if (uid.Length == 0) uid = JStr(data, "user_id");
                name = JStr(data, "name");
                if (name.Length == 0) name = JStr(data, "user_name");
            }

            if (errorCode != 0 || uid.Length == 0)
            {
                probe.Valid = false;
                probe.Error = errorCode != 0 ? $"服务端 code={errorCode}" : "账号信息为空";
                return probe;
            }
            probe.Valid = true;
            probe.UserId = uid;
            probe.UserName = name;
            return probe;
        }
        catch (Exception e)
        {
            // 解析失败/网络异常都归为「无法判定」，避免把好好的授权误杀
            probe.Unknown = true;
            probe.Error = e.Message.Length > 120 ? e.Message.Substring(0, 120) : e.Message;
            return probe;
        }
    }

    /// <summary>该实例里红果数据的最近修改时间（``YYYY-MM-DD HH:MM``）。</summary>
    private static string LatestStamp(string mumu, int idx)
    {
        foreach (string path in new[]
        {
            $"{DataRoot}/databases/", $"{DataRoot}/files/disk_collect/", $"{Prefs}/",
        })
        {
            try
            {
                string o = Sh(mumu, idx,
                    $"ls -lt {path} 2>/dev/null | awk 'NR==2{{print $6, $7}}'").Trim();
                if (o.Length > 0) return o;
            }
            catch { }
        }
        return "";
    }

    /// <summary>
    /// 枚举所有正在运行、且 App 里有 sessionid 的实例，按「最近活动」倒序。
    /// <paramref name="verify"/> 为 true 时逐台在线验证授权是否仍有效。
    /// </summary>
    public static List<Candidate> CollectCandidates(string mumu, bool verify = true)
    {
        var instances = ListInstances(mumu).Where(i => i.Started).ToList();
        if (instances.Count == 0)
            throw new InvalidOperationException(
                "没有正在运行的 MuMu 实例 —— 请先启动模拟器并打开红果 App");

        var list = new List<Candidate>();
        foreach (var inst in instances)
        {
            Candidate c = new Candidate { Index = inst.Index };
            try
            {
                var (cookies, device) = ReadAppState(inst.Index, mumu);
                c.Cookies = cookies;
                c.Device = device;
            }
            catch
            {
                continue;   // 这台没登录 / 读不到，跳过
            }
            c.Stamp = LatestStamp(mumu, inst.Index);
            if (verify) c.Probe = ProbeAccount(c.Cookies);
            list.Add(c);
        }
        list.Sort((a, b) => string.CompareOrdinal(b.Stamp, a.Stamp));
        return list;
    }

    /// <summary>
    /// 找一台**真能拿到授权**的实例。
    /// ★★ 多开时不能只按 mtime 猜：逐台验证，取第一台服务端认可的；
    ///   全程网络不通时退回「最近活动的那台」，不把用户挡在门外。
    /// </summary>
    public static int FindUsableInstance(string mumu, out Instance picked)
    {
        var instances = ListInstances(mumu);
        var candidates = CollectCandidates(mumu, verify: true);
        Candidate chosen = candidates.FirstOrDefault(c => c.Probe != null && c.Probe.Valid)
            ?? candidates.FirstOrDefault(c => c.HasSession);
        if (chosen == null)
            throw new InvalidOperationException(
                "找不到已登录的红果 App —— 请在模拟器里打开红果并登录");
        picked = instances.FirstOrDefault(i => i.Index == chosen.Index)
            ?? new Instance { Index = chosen.Index };
        return chosen.Index;
    }

    /// <summary>最近一次授权选中了哪台实例（界面日志用）。</summary>
    public static int LastPickedIndex { get; private set; } = -1;

    /// <summary>
    /// 边等边取：用户可能还没开安卓机 / 还没在 App 里登录。
    /// 每 2 秒扫一遍**正在运行**的实例，谁先出现「服务端认可的 sessionid」就返回谁。
    ///
    /// 返回实例号；<c>-1</c> = 超时或被取消。
    /// ★ 这里只负责「等到」，拿到后由调用方 <see cref="Login"/> 落盘。
    /// </summary>
    public static int WaitForAuth(string mumu, int timeoutSec, Action<string> onStatus = null,
        CancellationToken cancel = default)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, timeoutSec));
        int lastRunning = -1;
        int lastErrorLogged = 0;

        while (DateTime.UtcNow < deadline)
        {
            if (cancel.IsCancellationRequested) return -1;
            try
            {
                var insts = ListInstances(mumu);
                int running = insts.Count(i => i.Started);
                if (running == 0)
                {
                    if (lastRunning != 0)
                    {
                        onStatus?.Invoke("还没有安卓机在运行 —— 请打开 MuMu，启动那台装了红果的安卓机。");
                        lastRunning = 0;
                    }
                }
                else
                {
                    var cands = CollectCandidates(mumu, verify: true);
                    foreach (Candidate c in cands)
                    {
                        if (c.Probe != null && c.Probe.Valid)
                        {
                            onStatus?.Invoke($"✓ 实例 {c.Index} 已登录（uid={c.Probe.UserId}），正在读取…");
                            return c.Index;
                        }
                    }
                    if (lastRunning != running)
                    {
                        onStatus?.Invoke($"检测到 {running} 台安卓机在运行，正在等红果登录态…"
                            + "（请在 App 里确认已登录）");
                        lastRunning = running;
                    }
                }
            }
            catch (Exception e)
            {
                // 读失败不代表没登录，继续等；只在第一次（或换了错误）时提示
                int h = e.Message.GetHashCode();
                if (h != lastErrorLogged)
                {
                    onStatus?.Invoke("等待中：" + e.Message);
                    lastErrorLogged = h;
                }
            }
            Thread.Sleep(2000);
        }
        return -1;
    }

    /// <summary>完整登录：读登录态（多台时挑真正有效的那台）→ 保存凭据与设备档案。</summary>
    public static Account Login(int index = -1, bool save = true)
    {
        string mumu = TryFindMumu();   // ★ 没有 MuMu 时走 adb（雷电），不再直接抛
        var candidates = CollectCandidates(mumu, verify: index < 0);

        Candidate pick = null;
        Candidate fallback = null;
        var errors = new List<string>();
        foreach (Candidate c in candidates)
        {
            if (!c.HasSession) continue;
            if (index >= 0 && c.Index != index) continue;
            if (index >= 0) { pick = c; break; }                      // 用户显式指定，不再挑
            if (c.Probe != null && c.Probe.Valid) { pick = c; break; }
            if (c.Probe != null && c.Probe.Unknown) { fallback ??= c; continue; }
            errors.Add($"实例 {c.Index}: {c.Probe?.Error}");
        }

        if (pick == null && index < 0 && fallback != null) pick = fallback;
        if (pick == null)
            throw new InvalidOperationException(
                "找不到能用的红果 App 授权。\n"
                + (errors.Count > 0 ? string.Join("\n", errors) : "没有可读的已登录实例")
                + "\n· 请在 App 里确认已登录；多开时每台都要单独登录红果。");

        LastPickedIndex = pick.Index;
        var acc = new Account
        {
            Cookies = pick.Cookies,
            UserId = pick.Probe?.UserId ?? "",
            UserName = pick.Probe?.UserName ?? "",
        };
        if (save)
        {
            acc.Save();
            SaveDevice(pick.Device);
        }
        return acc;
    }

    /// <summary>保存设备身份：全局 latest + 按 install_id 归档（多实例互不覆盖）。</summary>
    public static void SaveDevice(DeviceIdentity device)
    {
        Paths.EnsureDir();
        var opts = DeviceJsonOpts();
        File.WriteAllText(Paths.DevicePath, JsonSerializer.Serialize(device, opts));

        if (string.IsNullOrEmpty(device.Iid)) return;
        Dictionary<string, DeviceIdentity> map;
        try
        {
            map = JsonSerializer.Deserialize<Dictionary<string, DeviceIdentity>>(
                File.ReadAllText(Paths.DevicesPath), opts) ?? new Dictionary<string, DeviceIdentity>();
        }
        catch { map = new Dictionary<string, DeviceIdentity>(); }
        map[device.Iid] = device;
        try
        {
            File.WriteAllText(Paths.DevicesPath, JsonSerializer.Serialize(map, opts));
        }
        catch { }
    }

    /// <summary>
    /// 取设备身份。给了 <paramref name="iid"/>（= cookie 里的 install_id）时，
    /// 优先用**同源**的那台实例归档的设备；找不到才退回全局 latest。
    /// </summary>
    public static DeviceIdentity LoadDevice(string iid = "")
    {
        // ★ DeviceIdentity 是**公共字段**（不是属性），System.Text.Json 默认不序列化字段
        //   ⇒ 必须 IncludeFields，否则读出来全是空串（多实例归档会静默失效）。
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
        };
        if (!string.IsNullOrEmpty(iid))
        {
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, DeviceIdentity>>(
                    File.ReadAllText(Paths.DevicesPath), opts);
                if (map != null && map.TryGetValue(iid, out DeviceIdentity one))
                    return one;
            }
            catch { }
        }
        try
        {
            if (File.Exists(Paths.DevicePath))
                return JsonSerializer.Deserialize<DeviceIdentity>(
                    File.ReadAllText(Paths.DevicePath), opts) ?? new DeviceIdentity();
        }
        catch { }
        return new DeviceIdentity();
    }

    private static JsonSerializerOptions DeviceJsonOpts() => new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // ★ 同上：DeviceIdentity 的字段必须显式纳入序列化
        IncludeFields = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

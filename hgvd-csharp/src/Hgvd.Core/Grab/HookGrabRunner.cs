using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Core.Grab;

/// <summary>
/// 「自动抓剧」启动器：在后台启动 tools/metasec/hook_grab.py（Python + frida，
/// 从雷电里的红果 App hook 出播放直链），实时回显日志，结束后产出直链清单 JSON 路径。
///
/// 设计取舍：
/// · C# 只负责「起进程 + 收日志 + 读清单」，抓链/解密逻辑仍复用已验证的 Python 工具；
/// · Python 用 --dry 只出清单，下载交给 C# 的 Downloader（复用进度/断点/历史链路）；
/// · 进程用 CancelToken 可随时取消（Kill 整棵进程树）。
/// </summary>
public sealed class HookGrabRunner
{
    /// <summary>探测到的 Python 解释器路径；空 = 没找到。</summary>
    public string PythonExe { get; private set; } = "";
    /// <summary>探测到的 hook_grab.py 路径；空 = 没找到。</summary>
    public string ScriptPath { get; private set; } = "";

    private readonly AppConfig _cfg;

    public HookGrabRunner(AppConfig cfg = null)
    {
        _cfg = cfg;
        Detect();
    }

    /// <summary>环境是否就绪（python + 脚本都在）。</summary>
    public bool Ready => !string.IsNullOrEmpty(PythonExe) && !string.IsNullOrEmpty(ScriptPath);

    /// <summary>检查环境，返回人类可读的问题描述；就绪返回空串。</summary>
    public string Check()
    {
        var misses = new List<string>();
        if (string.IsNullOrEmpty(PythonExe)) misses.Add("Python 解释器（python.exe）");
        if (string.IsNullOrEmpty(ScriptPath)) misses.Add("抓剧脚本（tools/metasec/hook_grab.py）");
        if (misses.Count == 0) return "";
        return "自动抓剧缺少依赖：" + string.Join("、", misses)
            + "\n· 需要：Python 3（装好 frida 模块）、雷电模拟器 14（root + frida-server 已启动）、adb 在 PATH。";
    }

    /// <summary>
    /// 启动一轮抓剧。
    /// <paramref name="seriesId"/> 剧 ID；<paramref name="title"/> 剧名（可空）；
    /// <paramref name="max"/> 最多集数；<paramref name="log"/> 日志回显（可能在后台线程调用）。
    /// 返回清单 JSON 路径（成功）或抛异常。
    /// </summary>
    public async Task<string> RunAsync(string seriesId, string title, int max,
                                       Action<string> log, CancellationToken ct)
    {
        if (!Ready) throw new InvalidOperationException(Check());

        string workDir = Path.Combine(Paths.ToolDir, "hookgrab");
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add("--series"); psi.ArgumentList.Add(seriesId);
        psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(workDir);
        psi.ArgumentList.Add("--max"); psi.ArgumentList.Add(max.ToString());
        psi.ArgumentList.Add("--dry");
        if (!string.IsNullOrEmpty(title))
        {
            psi.ArgumentList.Add("--title"); psi.ArgumentList.Add(title);
        }

        // ★ 2026-11-21：设备相关一律不在脚本里写死 —— 由这里把「用户配置 / 自动探测」的结果传下去。
        //   留空时脚本自己探测（adb 在 PATH、优先 emulator-*、frida 走 127.0.0.1 并自动 forward）。
        string adbPath = (_cfg?.AdbPath ?? "").Trim();
        if (adbPath.Length > 0) { psi.ArgumentList.Add("--adb"); psi.ArgumentList.Add(adbPath); }

        string serial = (_cfg?.AdbSerial ?? "").Trim();
        if (serial.Length == 0)
        {
            // 配置没指定就探一次，日志里能看出到底用了哪台设备（换机器排障全靠这行）
            try
            {
                Auth.AdbDevice dev = Auth.AdbDevices.Pick(
                    Auth.AdbDevices.List(adbPath, adbPath), adbPath);
                if (dev != null) serial = dev.Serial;
            }
            catch { }
        }
        if (serial.Length > 0) { psi.ArgumentList.Add("--serial"); psi.ArgumentList.Add(serial); }

        string frida = (_cfg?.HookFridaHost ?? "").Trim();
        if (frida.Length > 0) { psi.ArgumentList.Add("--frida-host"); psi.ArgumentList.Add(frida); }

        log?.Invoke($"→ 启动抓剧：series={seriesId} max={max} 设备={serial} "
                    + $"adb={(adbPath.Length > 0 ? adbPath : "自动")} 脚本={ScriptPath}");

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (output) output.AppendLine(e.Data);
            log?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (output) output.AppendLine("[err] " + e.Data);
            log?.Invoke("[err] " + e.Data);
        };

        ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        if (!proc.Start()) throw new InvalidOperationException("无法启动 Python 进程");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        int exit = await tcs.Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        log?.Invoke($"抓剧进程结束（exit={exit}）");

        // 找刚生成的清单：取最新的 *_直链清单.json
        var manifest = Directory.GetFiles(workDir, "*_直链清单.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (manifest == null)
            throw new InvalidOperationException("抓剧没有产出清单。请确认：红果已登录且停在播放页、"
                + "雷电 root + frida-server 已启动、adb 可用。详见上方日志。");
        return manifest;
    }

    // ---------------------------------------------------------------- 探测

    private string RepoRoot
    {
        get
        {
            if (!string.IsNullOrEmpty(_repoRoot)) return _repoRoot;
            // 脚本没找到时至少给个可用工作目录（脚本位置仍由 ScriptPath 全路径指定）
            return Environment.CurrentDirectory;
        }
    }
    private string _repoRoot;

    private void Detect()
    {
        PythonExe = FindPython();
        ScriptPath = FindScript(out _repoRoot);

        // 配置里显式指定优先（发行版单 exe 场景下自动探测可能找不到脚本）
        if (_cfg != null)
        {
            if (!string.IsNullOrWhiteSpace(_cfg.HookPython) && File.Exists(_cfg.HookPython.Trim()))
                PythonExe = _cfg.HookPython.Trim();
            if (!string.IsNullOrWhiteSpace(_cfg.HookScript) && File.Exists(_cfg.HookScript.Trim()))
            {
                ScriptPath = _cfg.HookScript.Trim();
                _repoRoot = new DirectoryInfo(ScriptPath).Parent?.Parent?.Parent?.FullName ?? _repoRoot;
            }
        }
    }

    private static string FindPython()
    {
        foreach (var name in new[] { "python", "python3", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name, Arguments = "--version",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p != null && p.WaitForExit(3000) && p.ExitCode == 0) return name;
            }
            catch { }
        }
        return "";
    }

    private static string FindScript(out string repoRoot)
    {
        repoRoot = "";
        var candidates = new List<string>();
        // 1) 配置里显式指定
        // 2) 可执行文件所在目录向上找 tools/metasec/hook_grab.py
        string baseDir = AppContext.BaseDirectory;
        var d = new DirectoryInfo(baseDir);
        // 从 bin/Debug/net8.0 向上最多 8 层能到仓库根（tools/metasec/hook_grab.py）
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string root = d.FullName;
            string script = Path.Combine(root, "tools", "metasec", "hook_grab.py");
            if (File.Exists(script)) { repoRoot = root; candidates.Add(script); break; }
        }
        // 3) 当前工作目录
        {
            string script = Path.Combine(Environment.CurrentDirectory, "tools", "metasec", "hook_grab.py");
            if (File.Exists(script) && !candidates.Contains(script))
            {
                if (string.IsNullOrEmpty(repoRoot)) repoRoot = Environment.CurrentDirectory;
                candidates.Add(script);
            }
        }
        return candidates.FirstOrDefault() ?? "";
    }

    private static string FindRepoRoot()
    {
        FindScript(out var root);
        return root;
    }
}

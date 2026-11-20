using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Xqd;

/// <summary>
/// 子进程封装（ffmpeg / ffprobe）。
///
/// ★ CreateNoWindow + Hidden：发行版是 WinExe，启动控制台程序会弹黑框。
/// ★ 一律 UTF-8 读输出；ffmpeg 的错误信息里常有中文路径。
/// </summary>
public static class Proc
{
    public sealed class RunResult
    {
        public int Code { get; set; } = -1;
        public string Out { get; set; } = "";
        public string Err { get; set; } = "";
        public bool Ok => Code == 0;
    }

    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (args != null)
            foreach (string a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>同步跑（给 ffprobe 这种毫秒级、无取消需求的场景）。</summary>
    public static RunResult Run(string exe, IEnumerable<string> args, int timeoutMs = 0)
    {
        var sb = new StringBuilder();
        var eb = new StringBuilder();
        try
        {
            using var p = new Process { StartInfo = StartInfo(exe, args) };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) eb.AppendLine(e.Data); };
            if (!p.Start()) return new RunResult { Err = "无法启动进程：" + exe };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (timeoutMs > 0)
            {
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    return new RunResult { Code = -2, Out = sb.ToString(), Err = "超时" };
                }
            }
            p.WaitForExit();   // 冲掉异步输出的尾巴
            return new RunResult { Code = p.ExitCode, Out = sb.ToString(), Err = eb.ToString() };
        }
        catch (Exception e)
        {
            return new RunResult { Err = e.Message, Out = sb.ToString() };
        }
    }

    /// <summary>异步跑（ffmpeg 合成，可取消）。取消时杀进程树。</summary>
    public static async Task<RunResult> RunAsync(string exe, IEnumerable<string> args,
                                                 int timeoutMs = 0,
                                                 CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var eb = new StringBuilder();
        using var p = new Process { StartInfo = StartInfo(exe, args) };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) eb.AppendLine(e.Data); };
        if (!p.Start()) return new RunResult { Err = "无法启动进程：" + exe };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = timeoutMs > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (cts != null) cts.CancelAfter(timeoutMs);

        try
        {
            await p.WaitForExitAsync(cts?.Token ?? ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            throw;
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
        }
        p.WaitForExit();
        return new RunResult { Code = p.ExitCode, Out = sb.ToString(), Err = eb.ToString() };
    }
}

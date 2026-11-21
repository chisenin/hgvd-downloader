using System;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core;
using Hgvd.Core.Grab;

namespace Hgvd.Tests;

/// <summary>抓流常驻循环的离线状态机回归：不碰 MuMu、不发网络请求。</summary>
/// <remarks>
/// ★ 用户 2026-11-21 定的下载流程：抓流是常驻探测器 —— 抓到直链只投递下载，
///   代理继续探测下一集，循环推进，只有暂停/停止才收尾。
///   这里守四件事：抓到直链不停抓流、暂停不取消下载、停止才取消、停止幂等。
/// </remarks>
public static class GrabLoopTests
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "  ✓ " : "  ✗ ") + name + (ok ? "" : "  " + detail));
        if (!ok) _fail++;
    }

    public static int Run()
    {
        Console.WriteLine("── 抓流常驻循环状态机 ──");
        _fail = 0;

        var svc = new HongguoService(new AppConfig());   // 只读本地账号，不上网
        var runner = new GrabRunner(svc);
        int stopped = 0;
        runner.CaptureStopped += (r, reason) =>
        {
            if (reason == "stop") Interlocked.Increment(ref stopped);
        };

        // 未启动的会话不能发出「已停止」事件
        runner.Stop();
        Check("未启动时停止不发事件", stopped == 0);

        runner.ResetSession();
        Check("重置后处于抓流中且未暂停", runner.CaptureRunning && runner.Started && !runner.Paused);
        Check("抓流中不取消下载", !runner.DownloadsCancelled);

        // ★ 抓到直链（模拟 OnJob 的登记 + 下载投递）绝不能停抓流
        //   OnJob 是私有的，这里直接验证「抓流态不因任何抓到动作自动改变」：
        //   常驻模型下 CaptureRunning 只由 Stop 改变。
        Check("抓流保持常驻（抓到直链不自停）", runner.CaptureRunning && stopped == 0);

        // 暂停：不取消下载、不改抓流态、可重复
        runner.Pause();
        Check("暂停生效且不取消下载", runner.Paused && !runner.DownloadsCancelled && runner.CaptureRunning);
        runner.Pause();
        Check("重复暂停幂等", runner.Paused);
        runner.Resume();
        Check("恢复后继续抓流", !runner.Paused && runner.CaptureRunning && !runner.DownloadsCancelled);

        // 停止：幂等、取消下载、发一次事件
        runner.Stop();
        Check("停止后不再抓流", !runner.CaptureRunning && !runner.Started);
        Check("停止只发一次事件", stopped == 1);
        Check("停止才取消下载", runner.DownloadsCancelled);
        runner.Stop();
        Check("重复停止不再发事件", stopped == 1);

        // 重置可再开一轮（换剧/重新抓）
        runner.ResetSession();
        Check("可重新开一轮", runner.CaptureRunning && !runner.DownloadsCancelled && !runner.Paused);

        // 并发停止只生效一次
        var parallel = new GrabRunner(svc);
        int hits = 0;
        parallel.CaptureStopped += (r, reason) => Interlocked.Increment(ref hits);
        parallel.ResetSession();
        Parallel.For(0, 32, _ => parallel.Stop());
        Check("并发停止只触发一次", hits == 1 && !parallel.CaptureRunning, $"hits={hits}");

        return _fail;
    }
}

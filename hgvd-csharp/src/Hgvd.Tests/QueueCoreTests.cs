using System;
using System.Threading;
using Hgvd.Core.Download;
using Hgvd.Core.Engine;
using Hgvd.Xqd;

namespace Hgvd.Tests;

/// <summary>
/// 两源共用的顶层工位池（2026-11-22 P3）：
/// 红果每集、兴趣岛每课都只占 1 格；暂停闸门两源共用一个。
/// </summary>
public static class QueueCoreTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else { _fail++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? " — " + detail : "")); }
    }

    public static int Run()
    {
        Console.WriteLine("\n[共用工位池] 一个池子 · 两源各占一格 · 一个暂停闸门");
        CapacityAndFairRelease();
        Cancellation();
        SharedPause();
        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }

    private static void CapacityAndFairRelease()
    {
        using var core = new DownloadQueueCore(2);
        var l1 = core.AcquireAsync().GetAwaiter().GetResult();
        var l2 = core.AcquireAsync().GetAwaiter().GetResult();
        var third = core.AcquireAsync();

        Check("满员时第三个工位等待", !third.IsCompleted, $"inUse={core.InUse}");
        l1.Dispose();
        l1.Dispose(); // 重复归还不能把池子撑大
        Check("归还一个后第三个拿到工位", third.Wait(1000), $"inUse={core.InUse}");

        var l3 = third.GetAwaiter().GetResult();
        Check("重复 Dispose 不多归还", core.InUse == 2, $"inUse={core.InUse}");
        l2.Dispose();
        l3.Dispose();
        Check("全部归还后池子空", core.InUse == 0, $"inUse={core.InUse}");
    }

    private static void Cancellation()
    {
        using var core = new DownloadQueueCore(1);
        var lease = core.AcquireAsync().GetAwaiter().GetResult();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool cancelled = false;
        try
        {
            core.AcquireAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { cancelled = true; }
        lease.Dispose();
        Check("排队时取消抛 OperationCanceledException", cancelled);
    }

    private static void SharedPause()
    {
        var gate = new PauseGate();
        var control = new XqdControl(gate);
        gate.Pause();
        Check("XqdControl 读到共享闸门的暂停", control.IsPaused && control.Paused);

        var wait = control.WaitIfPausedAsync();
        Check("共享暂停时 WaitIfPausedAsync 挂起", !wait.IsCompleted);
        gate.Resume();
        Check("共享恢复后 WaitIfPausedAsync 返回", wait.Wait(1000));
    }
}

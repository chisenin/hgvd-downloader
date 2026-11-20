using System;
using System.Threading;
using Hgvd.Core;
using Hgvd.Core.Grab;

namespace Hgvd.Tests;

/// <summary>端到端验证：起 GrabRunner，用户点一集，看是否真下到磁盘。</summary>
/// <remarks>用法：`Hgvd.Tests.exe --grabtest [秒数]`（默认 180 秒）</remarks>
public static class GrabTest
{
    public static int Run(string[] args)
    {
        int seconds = 180;
        if (args.Length > 1 && int.TryParse(args[1], out int s)) seconds = s;

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var svc = new HongguoService();
        Console.WriteLine($"登录态: {(svc.LoggedIn ? "有" : "无")}");
        if (!svc.LoggedIn) return 1;

        using var runner = new GrabRunner(svc,
            m => Console.WriteLine("  " + m),
            (job, res) => Console.WriteLine(res.Ok
                ? $"  ★ 成品：{res.OutPath}  {res.Size / 1048576.0:0.0} MB"
                : $"  ★ 失败：{res.Error}"));

        try
        {
            runner.StartAsync(cts.Token).GetAwaiter().GetResult();
            Console.WriteLine($"跑 {seconds} 秒，在 App 里点播教学集…");
            Thread.Sleep(seconds * 1000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"异常: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            runner.Stop();
            if (cts.IsCancellationRequested || !cts.TryReset())
            {
                // 超时后 Stop 已清代理；这里只做保险
                cts.Cancel();
            }
        }
        Console.WriteLine($"结束：成功 {runner.Done} · 失败 {runner.Failed}");
        return 0;
    }
}

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hgvd.Avalonia.Views;

namespace Hgvd.Avalonia;

/// <summary>
/// 应用入口。
/// ★ 支持命令行自检开关（与 WPF 版同名同义）：
///   --selftest [series_id]   加载收藏夹 → 展开分集 → 打印结果（不显示窗口）
///   --uitest                 界面交互自检（走真实事件绑定）
///   --batchtest [部数]       批量下载冒烟测试（老直连路径，接口加固后已不适合作门禁）
///   --tlsprobe [秒数]        本地 TLS 证书链门禁（验证 MITM 能发出完整链）
///   --dlprobe                本地下载链路门禁（Downloader + 命名 + ffprobe 校验）
///   --demo <series_id> [skip] 加载一部剧并真的开始下载（窗口正常显示，便于截图）
///   --authcheck              只查「能不能从 MuMu 拿到授权」，打印路径/实例/cookie 字段
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log(e.Exception?.ToString() ?? "未知异常");
            e.Handled = true;
        };

        string[] argv = Environment.GetCommandLineArgs();
        string mode = null, p1 = null, p2 = null;
        for (int i = 1; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) continue;
            mode = argv[i];
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) { p1 = argv[i + 1]; i++; }
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) { p2 = argv[i + 1]; i++; }
        }

        switch (mode)
        {
            case "--selftest":
                {
                    var w = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
                    RunThenShutdown(async () => await w.SelfTestAsync(p1 ?? ""));
                    return;
                }
            case "--tlsprobe":
                {
                    var w = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
                    RunThenShutdown(async () => await w.TlsProbeAsync(
                        int.TryParse(p1, out int ts) ? ts : 12));
                    return;
                }
            case "--dlprobe":
                {
                    var w = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
                    RunThenShutdown(async () => await w.DownloadProbeAsync());
                    return;
                }
            case "--batchtest":
                {
                    int cnt = 2;
                    if (int.TryParse(p1, out int c)) cnt = c;
                    var w = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
                    RunThenShutdown(async () =>
                    {
                        w.Show();
                        await w.BatchTestAsync(cnt);
                    });
                    return;
                }
            case "--uitest":
                {
                    // ★ 窗口要真的 Show 一次（Opacity=0 不可见），否则 ListBox 不会
                    //   生成行容器，勾选框根本找不到。
                    var w = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
                    w.MarkUiTest();          // ★ 先禁掉「恢复用户清单」，再 Show
                    RunThenShutdown(async () =>
                    {
                        w.Show();
                        await w.UiTestAsync();
                    });
                    return;
                }
            case "--authcheck":
                {
                    // ★ 只读不存：诊断用，不覆盖已有凭据
                    RunThenShutdown(() =>
                    {
                        Console.WriteLine("═══ MuMu 授权自检 ═══");
                        try
                        {
                            string mumu = Hgvd.Core.Auth.MumuAuth.TryFindMumu();
                            Console.WriteLine("模拟器通道: "
                                + (mumu.Length > 0 ? "MuMu " + mumu : "无 MuMu → 走 adb 设备（雷电）"));
                            foreach (var inst in Hgvd.Core.Auth.MumuAuth.ListInstances(mumu))
                                Console.WriteLine($"  实例 {inst.Index}: 启动={inst.Started} {inst.Name}");

                            foreach (var c in Hgvd.Core.Auth.MumuAuth.CollectCandidates(mumu, verify: true))
                                Console.WriteLine($"  实例 {c.Index}: {c.Probe?.Describe() ?? "未验证"}  "
                                    + $"uid={c.Probe?.UserId}  最近活动={c.Stamp}  iid={c.Device.Iid}");

                            var acc = Hgvd.Core.Auth.MumuAuth.Login(save: false);
                            Console.WriteLine($"✓ 选中实例 {Hgvd.Core.Auth.MumuAuth.LastPickedIndex}："
                                + $"{acc.Cookies.Count} 个 cookie 字段，uid={acc.UserId}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("✗ 失败：" + ex.Message);
                        }
                        return Task.CompletedTask;
                    });
                    return;
                }
            case "--xqdtest":
                {
                    // ★ 兴趣岛链路冒烟：真实链接 → 清单解析 → 分片下载 → ffmpeg 合成
                    var w = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
                    RunThenShutdown(async () =>
                    {
                        w.Show();
                        await w.XqdTestAsync(p1 ?? "", p2 ?? "");
                    });
                    return;
                }
            case "--xqdcatalog":
                {
                    // ★ 兴趣岛课表诊断：取授权 → 列营 → 拉课表 → 解析第一节地址（只读）
                    var w = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
                    RunThenShutdown(async () =>
                    {
                        w.Show();
                        await w.XqdCatalogTestAsync(p1 ?? "", p2 ?? "");
                    });
                    return;
                }
            case "--xqdui":
                {
                    // ★ 开发期：直接停在「兴趣岛课程」页并塞几条示例（临时清单，不写用户数据）
                    var w = new MainWindow();
                    w.Opened += async (_, _) =>
                    {
                        await Task.Delay(500);
                        w.PrepareXqdDemoForShot();
                    };
                    SetMainWindow(w);
                    w.Show();
                    return;
                }
            case "--demo":
                {
                    int skip = 0;
                    if (int.TryParse(p2, out int sk)) skip = sk;
                    var w = new MainWindow();
                    w.Opened += async (_, _) => await w.DemoAsync(p1 ?? "", skip);
                    SetMainWindow(w);
                    w.Show();
                    return;
                }
        }

        // 正常模式
        var main = new MainWindow();
        SetMainWindow(main);
        main.Show();
    }

    /// <summary>跑一段异步任务，结束后关掉应用。</summary>
    private void RunThenShutdown(Func<Task> body)
    {
        // ★ 必须在 **UI 线程** 上跑：
        //   w.Show() 和自检里的控件操作都要求 UI 线程
        //   （后台线程调 Show() 会抛 "Call from invalid thread"）。
        Dispatcher.UIThread.Post(async () =>
        {
            try { await body(); }
            catch (Exception ex) { Log(ex.ToString()); }
            ShutdownApp();
        });
    }

    /// <summary>退出应用（Avalonia 11 里 Application 没有 Shutdown，走生命周期）。</summary>
    private static void ShutdownApp()
    {
        if (Application.Current?.ApplicationLifetime
            is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime life)
            life.TryShutdown(0);
    }

    /// <summary>设置主窗口（Avalonia 11 的 MainWindow 在生命周期上，不在 Application 上）。</summary>
    private static void SetMainWindow(Window w)
    {
        if (Application.Current?.ApplicationLifetime
            is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime life)
            life.MainWindow = w;
    }

    internal static void Log(string msg)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".hgv_tool");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "gui-crash.log"),
                $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { }
    }
}


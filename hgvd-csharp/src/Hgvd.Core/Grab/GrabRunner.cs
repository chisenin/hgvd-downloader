using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core.Api;
using Hgvd.Core.Auth;
using Hgvd.Core.Engine;

namespace Hgvd.Core.Grab;

/// <summary>抓流的编排层：代理 + 白名单 + 下载，一条龙。</summary>
/// <remarks>
/// ★ 定位（2026-11-21 用户定的下载流程）：抓流是**常驻的直链探测器**，不是一次性取链工具。
///   点下载 → App 播一集 → 我们抓到直链就下这一集，同时代理继续开着探测下一集 →
///   循环推进，直到用户暂停或停止。
///   ⇒ **绝不能「抓到第一条就停代理」**：一次点播的相邻几集常分多个 preload 请求陆续到，
///     首条即停会把后面的连接全 cancel 掉（用户实测「只下 1 集 / 换集又不行」的根因）。
/// ★ 暂停 ≠ 停止：
///   · 暂停 = 暂停下载消费（保留已抓到/在途的直链与分片），代理与 App 都不动，
///     所以「继续」不用重启 App；暂停期间 App 若还在点播，抓到的直链排队等恢复。
///   · 停止 = 停 MITM + 清模拟器 http_proxy + 取消在途下载，会话结束。
/// ★ 教学类**不加密**，所以 DownloadRequest.ContentKey 恒空，不走 DRM 分支。
/// </remarks>
public sealed class GrabRunner : IDisposable
{
    private const string Package = "com.phoenix.read";
    private const string Splash = Package + "/com.dragon.read.pages.splash.SplashActivity";

    private readonly HongguoService _svc;
    private readonly Action<string> _log;
    private readonly Action<GrabJob, DownloadResult> _onDone;
    private readonly SemaphoreSlim _gate = new(2);          // 同时下 2 个，够快也不炸 CDN
    private readonly PauseGate _pause = new();              // 暂停/继续：保留进度与分片

    private CaptureProxy _proxy;
    private GrabSource _source;
    private CancellationTokenSource _cts;
    private string _mumu;
    private int _instance = -1;
    private bool _started;
    private int _stopped;                        // 0 = 会话活跃，1 = 已停止（幂等）
    private int _active;                         // 在途下载数
    private int _proxyGen;                       // 代理代次：防止旧会话的「清代理」误清新会话
    private Task _proxyCleanup = Task.CompletedTask;   // 上一次「清模拟器代理」的后台任务（串行化用）

    public int Port { get; set; } = 8899;
    public int Done;
    public int Failed;

    /// <summary>会话是否活跃（启动后到 Stop 之前恒 true，抓到直链不会让它变 false）。</summary>
    public bool Started => Volatile.Read(ref _stopped) == 0 && _started;
    /// <summary>MITM 代理是否还在抓（常驻模型中与 Started 一致；仅 Stop 后为 false）。</summary>
    public bool CaptureRunning => Started;
    public bool Running => _proxy != null && CaptureRunning;
    /// <summary>下载是否暂停。</summary>
    public bool Paused => _pause.IsPaused;
    /// <summary>在途下载数（停止后应归零）。</summary>
    public int ActiveDownloads => Volatile.Read(ref _active);
    /// <summary>下载 token 是否被取消（离线回归用：暂停/抓到直链都不能取消它，只有 Stop 能）。</summary>
    internal bool DownloadsCancelled => _cts?.IsCancellationRequested ?? false;
    public string ProxyAddress { get; private set; } = "";

    /// <summary>会话结束（用户点停止），reason 恒为 "stop"。抓到直链不再触发它。</summary>
    public event Action<GrabRunner, string> CaptureStopped;

    public GrabRunner(HongguoService svc, Action<string> log = null,
                      Action<GrabJob, DownloadResult> onDone = null)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _log = log ?? (_ => { });
        _onDone = onDone;
    }

    /// <summary>启动抓流：起代理 → 设模拟器代理 → 重启 App。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        Stop();
        ResetSession(ct);
        Interlocked.Increment(ref _proxyGen);    // 新会话 = 新代次
        // ★ 等上一次的「清代理」收尾，否则它可能把本会话刚设好的代理又清掉
        try { await _proxyCleanup.ConfigureAwait(false); } catch { }

        var (map, ts) = Whitelist.Load();
        if (map.Count == 0)
        {
            _log("· 白名单为空，后台开始建（收藏夹里的教学合集）…");
            _ = Task.Run(async () =>
            {
                try { await Whitelist.BuildAsync(_svc, _log, _cts.Token); }
                catch (Exception ex) { _log($"· 建白名单失败：{ex.Message}"); }
            }, _cts.Token);
        }
        else
        {
            _log($"· 白名单 {map.Count} 集（{DateTimeOffset.FromUnixTimeSeconds((long)ts):MM-dd HH:mm}）");
        }

        _source = new GrabSource(map, OnJob, _log);
        // ★ 闭包捕获**局部** source：Stop() 会把 _source 置 null，
        //   若捕获的是字段，MITM 在途连接回调时会 NRE。
        var source = _source;

        _proxy = new CaptureProxy(Port, ex => source.Handle(ex), _log);
        _proxy.Start();

        // ★ 2026-11-21：正式改用雷电 —— 有 MuMu 用 MuMu，没有就走 adb 设备（RunShell 内部自动分流）。
        _mumu = MumuAuth.TryFindMumu();
        _instance = _mumu.Length > 0 ? MumuAuth.FindUsableInstance(_mumu, out _) : 0;
        ProxyAddress = $"{LocalIp()}:{Port}";
        MumuAuth.RunShell(_mumu, _instance, $"settings put global http_proxy {ProxyAddress}");
        _log($"· 已设模拟器代理 {ProxyAddress}，重启红果…");

        MumuAuth.RunShell(_mumu, _instance, $"am force-stop {Package}");
        await Task.Delay(800, _cts.Token);
        if (!Started) return;                     // 启动过程中用户点了「停止」
        MumuAuth.RunShell(_mumu, _instance, $"am start -n {Splash}");
        _log("✓ 抓流已就绪：在 App 里点播教学课，抓到即下、边抓边下、循环推进（点暂停/停止才结束）");
    }

    /// <summary>重置一轮会话的状态（不碰设备，离线回归也能验证状态机）。</summary>
    internal void ResetSession(CancellationToken ct = default)
    {
        _started = true;
        Interlocked.Exchange(ref _stopped, 0);
        Done = Failed = 0;
        _pause.Resume();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>暂停下载：代理与 App 都不动，抓到的直链排队；继续时无需重启 App。</summary>
    public void Pause()
    {
        if (!Started) return;
        _pause.Pause();
        _log("✓ 已暂停：抓流仍在探测，抓到的直链会排队，点「继续」恢复下载");
    }

    /// <summary>继续下载。</summary>
    public void Resume()
    {
        if (!Started) return;
        _pause.Resume();
        _log("✓ 已继续下载");
    }

    /// <summary>用户停止：停 MITM + 取消在途下载（**立即返回**），清代理在后台做。</summary>
    /// <remarks>
    /// ★★ 清模拟器代理要走 MuMuManager.exe（同步进程调用，实测数秒起、上限 60s）。
    ///   它**绝不能**跑在 UI 线程上 —— 否则点「停止抓流」后界面冻结，
    ///   用户实测「点了，想停要比较长时间」就是这个。改成后台 + 代次校验后，
    ///   点「停止」立刻响应，清代理慢也不影响界面。
    /// </remarks>
    public void Stop()
    {
        if (!_started || Interlocked.Exchange(ref _stopped, 1) != 0) return;

        try { _proxy?.Stop(); } catch { }
        try { _cts?.Cancel(); } catch { }         // 只有停止才取消下载
        _pause.Resume();                          // 别让在途任务卡在暂停闸上

        var mumu = _mumu;
        var inst = _instance;
        var gen = Volatile.Read(ref _proxyGen);
        _proxyCleanup = Task.Run(() =>
        {
            if (mumu == null || inst < 0) return;
            // ★ 代次变了 = 用户已经开了新会话 ⇒ 别把新会话刚设好的代理清了
            if (Volatile.Read(ref _proxyGen) != gen)
            {
                _log("· 已开启新会话，跳过清代理");
                return;
            }
            try
            {
                MumuAuth.RunShell(mumu, inst, "settings put global http_proxy :0", 15000);
                _log("✓ 模拟器代理已清除");
            }
            catch (Exception ex) { _log("· 清除模拟器代理失败：" + ex.Message); }
        });

        _log("✓ 抓流已停止");
        try { CaptureStopped?.Invoke(this, "stop"); } catch { }
        _proxy = null;
        _source = null;
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------ 内部

    private void OnJob(GrabJob job)
    {
        // ★ 常驻模型：抓到直链**只投递下载**，绝不停代理 —— 后续相邻几集还要靠它抓。
        Interlocked.Increment(ref _active);
        _ = Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _pause.WaitAsync(_cts?.Token ?? default).ConfigureAwait(false);
                    var ep = new Episode
                    {
                        Vid = job.Vid,
                        Index = job.Index,
                        Title = job.Title,
                        Duration = job.Duration,
                        SeriesTitle = job.Book,
                    };
                    string outPath;
                    try
                    {
                        outPath = _svc.BuildOutPath(ep);
                    }
                    catch (Exception ex)
                    {
                        Failed++;
                        _log($"✗ {job.Book} {job.Index} 无法确定输出路径：{ex.Message}");
                        return;
                    }

                    var req = new DownloadRequest
                    {
                        Url = job.Url,
                        BackupUrl = job.BackupUrl,
                        OutPath = outPath,
                        ExpectDuration = job.Duration,
                        // ★ 教学类不加密，ContentKey 留空 —— 不要为它准备解密分支
                        ContentKey = "",
                    };
                    var cb = new DownloadCallbacks
                    {
                        OnPhase = p => _log($"  · {job.Book} {job.Index}. {p}"),
                    };
                    var res = await Downloader.DownloadAsync(req, cb, _cts?.Token ?? default)
                                            .ConfigureAwait(false);
                    if (res.Ok) Done++; else Failed++;
                    _log(res.Ok
                        ? $"✓ {job.Book}.{job.Index}. {job.Title}  {res.Size / 1048576.0:0.0} MB {(res.Skipped ? "已存在" : "完成")}"
                        : $"✗ {job.Book}.{job.Index}. {job.Title} 失败：{res.Error}");
                    _onDone?.Invoke(job, res);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Failed++;
                _log($"✗ {job.Book} {job.Index} 异常：{ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        });
    }

    /// <summary>给模拟器用的宿主 IPv4（桥接网络下模拟器能直连）。</summary>
    private static string LocalIp()
    {
        try
        {
            var cands = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .ToList();
            return cands.FirstOrDefault(x => x.StartsWith("192.168."))
                   ?? cands.FirstOrDefault()
                   ?? "127.0.0.1";
        }
        catch (NetworkInformationException)
        {
            return "127.0.0.1";
        }
    }
}

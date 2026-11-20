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
/// ★ 这里的定位是「主动取流」：用户在 App 里播一集，我们把 preload 的直链拦下来下载。
///   和「提取 App 缓存」互补 —— 缓存只覆盖播过的，抓流能顺带拿到相邻几集。
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

    private CaptureProxy _proxy;
    private GrabSource _source;
    private CancellationTokenSource _cts;
    private string _mumu;
    private int _instance = -1;

    public int Port { get; set; } = 8899;
    public int Done;
    public int Failed;
    public bool Running => _proxy != null;
    public string ProxyAddress { get; private set; } = "";

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
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

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

        _proxy = new CaptureProxy(Port, ex => _source.Handle(ex), _log);
        _proxy.Start();

        _mumu = MumuAuth.FindMumu();
        _instance = MumuAuth.FindUsableInstance(_mumu, out _);
        ProxyAddress = $"{LocalIp()}:{Port}";
        MumuAuth.RunShell(_mumu, _instance, $"settings put global http_proxy {ProxyAddress}");
        _log($"· 已设模拟器代理 {ProxyAddress}，重启红果…");

        MumuAuth.RunShell(_mumu, _instance, $"am force-stop {Package}");
        await Task.Delay(800, _cts.Token);
        MumuAuth.RunShell(_mumu, _instance, $"am start -n {Splash}");
        _log("✓ 抓流已就绪：在 App 里点一集教学课即可");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }

        if (_mumu != null && _instance >= 0)
        {
            try { MumuAuth.RunShell(_mumu, _instance, "settings put global http_proxy :0"); }
            catch { }
        }
        try { _proxy?.Stop(); } catch { }
        _proxy = null;
        _source = null;
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------ 内部

    private void OnJob(GrabJob job)
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
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
            catch (Exception ex)
            {
                Failed++;
                _log($"✗ {job.Book} {job.Index} 异常：{ex.Message}");
            }
            finally
            {
                _gate.Release();
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

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hgvd.Core.Auth;

namespace Hgvd.Avalonia.Views;

/// <summary>
/// 「拿不到授权」时的引导窗：告诉用户去开哪台安卓机、开哪个 App、确认登录，
/// 同时**在后台每 2–3 秒探一次**，读到可用登录态就自动关窗继续。
///
/// ★ 红果与兴趣岛共用这一个窗（状态查询由调用方传入）：两个源的等待体验必须一致。
/// ★ 只认「运行中实例里的 App 登录态」，等价于用户手动在 App 里登录这个动作本身。
/// </summary>
public sealed class WaitAuthWindow : Window
{
    private readonly TextBlock _hint = new TextBlock();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly Func<Action<string>, CancellationToken, Task<int>> _wait;
    private readonly string _timeoutHint;
    private bool _closed;

    /// <summary>等到的实例号；-1 = 取消/超时。</summary>
    public int FoundIndex { get; private set; } = -1;

    /// <summary>红果：沿用原来的探测（MumuAuth.WaitForAuth，内部每 2 秒扫一次）。</summary>
    public WaitAuthWindow(string mumu, IReadOnlyList<MumuAuth.Instance> instances)
        : this(
            "等待红果登录",
            "还没拿到红果授权 —— 请打开安卓机里的红果并确认已登录",
            "1. 在 MuMu 里启动装了红果的安卓机（多开时列表里任一台都行）\n"
            + "2. 打开「红果短剧」，确认已经登录（能看到收藏/首页内容）\n"
            + "3. 回到这里等几秒，读到登录态会自动继续，不用再点按钮",
            instances,
            "等待超时。可以先启动安卓机、在红果里登录，然后再点一次「获取授权」。",
            (say, ct) => Task.Run(() => MumuAuth.WaitForAuth(
                mumu, 180,
                s => Dispatcher.UIThread.Post(() => say(s)),
                ct), ct))
    {
    }

    /// <summary>通用构造：等待逻辑由调用方给（兴趣岛用它复用同一套引导）。</summary>
    public WaitAuthWindow(
        string title,
        string headText,
        string stepsText,
        IReadOnlyList<MumuAuth.Instance> instances,
        string timeoutHint,
        Func<Action<string>, CancellationToken, Task<int>> wait)
    {
        _wait = wait;
        _timeoutHint = timeoutHint;

        Title = title;
        Width = 580;
        Height = 320;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFFFFF"));

        var head = new TextBlock
        {
            Text = headText,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var steps = new TextBlock
        {
            Text = stepsText,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#444444")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var list = new TextBlock
        {
            Text = DescribeInstances(instances),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };

        _hint.Text = "准备中…";
        _hint.FontSize = 12;
        _hint.Foreground = new SolidColorBrush(Color.Parse("#1A73E8"));
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 12, 0, 0);

        var cancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(18, 6, 18, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        cancel.Click += (_, __) =>
        {
            _cts.Cancel();
            _closed = true;
            Close();
        };

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18), Spacing = 0 };
        panel.Children.Add(head);
        panel.Children.Add(steps);
        panel.Children.Add(list);
        panel.Children.Add(_hint);
        panel.Children.Add(cancel);
        Content = panel;

        Opened += async (_, __) => await PollAsync();
        Closed += (_, __) => _cts.Cancel();
    }

    private void SetHint(string text)
    {
        if (!_closed) _hint.Text = text;
    }

    private static string DescribeInstances(IReadOnlyList<MumuAuth.Instance> instances)
    {
        if (instances.Count == 0) return "当前没有检测到 MuMu 安卓机。";
        var sb = new System.Text.StringBuilder("检测到的安卓机：");
        foreach (var inst in instances)
            sb.Append($"\n· 实例 {inst.Index}（{inst.Name}）：{(inst.Started ? "已启动" : "未启动")}");
        return sb.ToString();
    }

    private async Task PollAsync()
    {
        int idx;
        try
        {
            idx = await _wait(SetHint, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SetHint("等待失败：" + ex.Message);
            return;
        }

        if (_closed) return;
        FoundIndex = idx;
        if (idx >= 0) Close();
        else SetHint(_timeoutHint);
    }
}

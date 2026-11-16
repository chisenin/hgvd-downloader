using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Hgvd.Avalonia.Views;

/// <summary>
/// 「关于」窗口（点左上角 logo 区域打开）。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        LblVersionLine.Text = $"版本 {AppVersion}  ·  {RuntimeInfo}";
    }

    /// <summary>程序集版本号（与 exe 名里的版本号一致）。</summary>
    public static string AppVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            return v == null ? "1.1.1" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static string RuntimeInfo =>
        $"运行于 .NET {Environment.Version.Major}.{Environment.Version.Minor}"
        + $" · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 设置主窗口 —— Avalonia 的 Owner 是 protected，
    /// 只能由窗口自己（或子类）设置。
    /// </summary>
    internal void SetOwner(Window owner) => Owner = owner;

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = $"红果视频下载器 {AppVersion}\n{RuntimeInfo}";
            var top = GetTopLevel(this);
            if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(text);
            BtnCopy.Content = "已复制 ✓";
        }
        catch (Exception ex)
        {
            BtnCopy.Content = "复制失败：" + ex.Message;
        }
    }
}

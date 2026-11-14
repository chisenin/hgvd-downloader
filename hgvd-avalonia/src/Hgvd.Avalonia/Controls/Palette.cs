using Avalonia;
using Avalonia.Media;

namespace Hgvd.Avalonia.Controls;

/// <summary>
/// 资源查询小工具 —— 让 ViewModel 也能拿到 App.axaml 里定义的画刷，
/// 避免同一份配色在 XAML 和 C# 里各写一遍。
/// </summary>
public static class Palette
{
    /// <summary>按 key 取画刷；取不到返回透明画刷（不抛异常）。</summary>
    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app != null
            && app.Resources.TryGetValue(key, out var v)
            && v is IBrush b)
            return b;
        return Brushes.Transparent;
    }

    /// <summary>十六进制字符串 → 画刷。</summary>
    public static IBrush FromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;
        try { return new SolidColorBrush(Color.Parse(hex)); }
        catch { return Brushes.Transparent; }
    }
}

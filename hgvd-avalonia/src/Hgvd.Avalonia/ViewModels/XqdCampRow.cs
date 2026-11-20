using System;
using Avalonia.Media;
using Hgvd.Avalonia.Controls;
using Hgvd.Xqd;

namespace Hgvd.Avalonia.ViewModels;

/// <summary>「我的课程」里的一个营（左栏一行）。</summary>
public sealed class XqdCampRow : NotifyBase
{
    private bool _isChecked;
    private double _titleWidth = 180;

    public XqdCampRow(Camp camp)
    {
        Camp = camp;
    }

    public Camp Camp { get; }

    public string Name => Camp.Name;
    public string Display => Camp.Name;
    public string Badge => Camp.Badge;
    public string Kind => Camp.Kind;

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public double TitleWidth
    {
        get => _titleWidth;
        set => Set(ref _titleWidth, value);
    }

    public string ToolTip =>
        $"{Camp.Name}\n{Camp.Kind} · {Camp.Count} 节\n"
        + (string.IsNullOrEmpty(Camp.CampId) ? "公开课（periodId=" + Camp.PeriodId + "）"
                                             : "campId=" + Camp.CampId);
}

using Hgvd.Xqd;

namespace Hgvd.Avalonia.ViewModels;

/// <summary>
/// 清单的 5 档视图筛选。抽成纯函数，界面与离线回归共用同一套判定。
/// </summary>
public static class EpViewFilter
{
    public const string All = "all";
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";

    public static bool Match(string state, string tag)
    {
        state ??= "";
        return tag switch
        {
            Pending => state == "待下载",
            Running => XqdState.IsRunning(state) || state == "已暂停",
            Done => state is "已完成" or "已存在",
            Failed => state == "失败",
            _ => true,
        };
    }
}

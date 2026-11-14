using System;
using Hgvd.Core;

namespace Hgvd.Tests;

/// <summary>
/// 文件名去重 + 画质默认值回归。
///
/// ★★ 为什么要有这个测试：
///   2026-11-14 用户反馈下载出来的文件名是
///   「《生物史前》.001.《生物史前》 P1.mp4」—— 同一段文字重复两遍。
///   根因：很多教学合集的**分集标题本身就带剧名**
///   （剧名《生物史前》、分集标题「《生物史前》 P1」），
///   而成品文件名是「&lt;剧名&gt;.&lt;序号&gt;.&lt;集名&gt;.mp4」——
///   两段拼一起自然就重复了。
/// </summary>
public static class NamingTests
{
    private static int _pass;
    private static int _fail;

    private static void Eq(string name, string expected, string actual)
    {
        bool ok = expected == actual;
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else
        {
            _fail++;
            Console.WriteLine($"  ✗ {name}");
            Console.WriteLine($"      期望 {expected}");
            Console.WriteLine($"      实际 {actual}");
        }
    }

    public static int Run()
    {
        Console.WriteLine("\n[命名] 文件名去重与画质默认值");

        // ---- 去重：标题里带剧名 ----
        Eq("去剧名前缀（带书名号）",
            "P1", Util.DedupTitle("《生物史前》", "《生物史前》 P1"));
        Eq("去剧名前缀（无书名号）",
            "第1课", Util.DedupTitle("李的AI教学专家", "李的AI教学专家 第1课"));
        Eq("不误伤无关标题",
            "植物之间的生存竞争", Util.DedupTitle("《生物史前》", "植物之间的生存竞争"));
        Eq("标题与剧名相同 → 集名省掉",
            "", Util.DedupTitle("《生物史前》", "《生物史前》"));
        Eq("不留孤立括号",
            "", Util.DedupTitle("神竟是外星人！《山海星河，我说长生之谜》",
                               "神竟是外星人！《山海星河，我说长生之谜》"));
        Eq("空剧名不处理", "标题", Util.DedupTitle("", "标题"));
        Eq("空标题", "", Util.DedupTitle("剧名", ""));

        // ---- 去重：括号不同时也要能识别 ----
        Eq("剧名带括号变体",
            "P2", Util.DedupTitle("《生物史前》", "生物史前 P2"));

        // ---- 画质默认值 ----
        Eq("默认画质是最高的 1080p", "1080p", new AppConfig().Quality);

        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }
}

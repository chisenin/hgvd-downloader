using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Hgvd.Core.Sign;

/// <summary>设备身份。</summary>
public sealed class DeviceIdentity
{
    public string DeviceId = "";
    public string Iid = "";
    public string Cdid = "";
    public string Openudid = "";
    public string VersionName = "7.3.9.32";
    public string VersionCode = "73932";
}

/// <summary>签名用的随机熵 —— 每次请求都要重新抽（服务端会校验新鲜度）。</summary>
public sealed class Entropy
{
    public long EnvLaunch;
    public long EnvPid;
    public long NowSecs;
    public int MessageRandom;
    public int Random;
    public int PackedRandom;
    public int GorgonRandom;
    public int HeliosRandom;

    public static Entropy Draw(long nowMillis)
    {
        var buf = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(buf);

        ulong n = BitConverter.ToUInt64(buf, 0);
        return new Entropy
        {
            EnvLaunch = 100 + (long)(n % 21),
            EnvPid = 10001 + (BitConverter.ToUInt16(buf, 8) % 2000),
            NowSecs = nowMillis / 1000,
            MessageRandom = BitConverter.ToInt32(buf, 0),
            Random = BitConverter.ToInt32(buf, 4),
            PackedRandom = BitConverter.ToInt32(buf, 8),
            GorgonRandom = BitConverter.ToUInt16(buf, 12),
            HeliosRandom = BitConverter.ToInt32(buf, 12),
        };
    }
}

/// <summary>
/// 抖音系请求签名入口。
///
/// ★ 对应 Python 版 <c>hgvd/sign/core.py</c> 的
///   <c>sign_request</c> / <c>sign_playback</c>。
/// </summary>
public static class Signer
{
    private const string Unreserved = "-_.!~*'()";

    private static string EncodeComponent(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (byte b in Util.Bytes(s))
        {
            char c = (char)b;
            bool safe = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                        (c >= '0' && c <= '9') || Unreserved.IndexOf(c) >= 0;
            if (safe) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public static string EncodeQuery(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var kv in pairs)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(EncodeComponent(kv.Key)).Append('=').Append(EncodeComponent(kv.Value ?? ""));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 判断某个 ticket 偏移会不会被服务端拒签。
    /// ★ 与 Python 版 <c>_branch_of</c> 一致：<c>_get_iv</c> 按**字节索引奇偶**
    ///   交替使用两种混合公式 —— 写错会导致 <c>_rticket</c> 偏移 1，
    ///   服务端按 branch 校验时直接拒签。
    /// </summary>
    private static uint BranchOf(byte[] queryBytes, byte[] bodyMd5, byte[] ts)
    {
        uint iv = 0x20230928;
        iv = F13.GetIv(iv, queryBytes);
        iv = F13.GetIv(iv, bodyMd5);
        iv = F13.GetIv(iv, ts);
        int low = (int)(iv & 15);
        return (uint)(low - ((low * 171) >> 9) * 3);
    }

    private static string PlaybackAid(string query)
    {
        foreach (string part in query.Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part.Substring(0, eq) != "aid") continue;
            string v = Uri.UnescapeDataString(part.Substring(eq + 1));
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return "1967";
    }

    /// <summary>给一个请求签名，返回 (query, headers)。</summary>
    public static (string Query, Dictionary<string, string> Headers) SignRequest(
        string path,
        IEnumerable<KeyValuePair<string, string>> pairs,
        long nowMillis,
        string deviceId,
        string versionName,
        byte[] body = null,
        Entropy entropy = null)
    {
        Entropy ent = entropy ?? Entropy.Draw(nowMillis);
        bool hasBody = body != null;
        byte[] bodyMd5 = hasBody ? Util.Md5(body) : new byte[16];

        var basePairs = new List<KeyValuePair<string, string>>(pairs);
        long ticket = 0;
        int khronos = 0;
        string query = "";

        // ★ 服务端对某些 branch 会拒签 —— 最多试 32 个 ticket 偏移。
        for (int offset = 0; offset < 32; offset++)
        {
            ticket = nowMillis + offset;
            khronos = (int)(ticket / 1000);
            var withTicket = new List<KeyValuePair<string, string>>(basePairs)
            {
                new("ts", khronos.ToString()),
                new("_rticket", ticket.ToString()),
            };
            query = EncodeQuery(withTicket);
            // ★★ 用 **SM3(query)** —— Python 版
            //   `sum_v = sm3_sum(query.encode("utf-8"))`，
            //   传给 `_branch_of` 的是 SM3 摘要（32 字节），不是原文。
            //   用原文会算出不同的 branch → ticket 偏移不对 → 服务端拒签。
            byte[] sumV = Sm3.Sum(Util.Bytes(query));
            if (BranchOf(sumV, bodyMd5, Util.Le32(khronos)) != 1) break;
        }
        if (ent.NowSecs == 0) ent.NowSecs = khronos;

        string rawUrl = "https://reading.snssdk.com" + path + "?" + query;

        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/json; charset=utf-8,application/x-protobuf",
            ["x-xs-from-web"] = "0",
            ["x-ss-req-ticket"] = ticket.ToString(),
            ["x-tt-request-tag"] = "t=0;n=0",
            ["sdk-version"] = "2",
            ["passport-sdk-version"] = "50561",
            ["x-vc-bdturing-sdk-version"] = "3.7.2.cn",
            ["x-khronos"] = khronos.ToString(),
            ["x-ladon"] = Convert.ToBase64String(Util.Le32(khronos)),
            ["x-argus"] = Convert.ToBase64String(Util.Le32(khronos)),
            ["x-gorgon"] = Gorgon.XGorgon(query, body, khronos, ent.GorgonRandom),
            ["x-helios"] = Gorgon.AssembleHelios(khronos, ent.HeliosRandom),
            ["x-medusa"] = Medusa.Assemble(rawUrl, body, khronos, deviceId, versionName,
                new Medusa.MedusaEntropy
                {
                    EnvLaunch = ent.EnvLaunch, EnvPid = ent.EnvPid,
                    NowSecs = ent.NowSecs, MessageRandom = ent.MessageRandom,
                    Random = ent.Random, PackedRandom = ent.PackedRandom,
                    GorgonRandom = ent.GorgonRandom, HeliosRandom = ent.HeliosRandom,
                }),
            ["x-tt-dt"] = "",
        };
        if (hasBody)
        {
            headers["Content-Type"] = "application/json; charset=UTF-8";
            headers["x-ss-stub"] = Util.Md5Hex(body).ToUpperInvariant();
        }
        return (query, headers);
    }

    /// <summary>
    /// 给 <c>video_model</c> 播放接口签名。
    ///
    /// ★ 与普通请求的差别：
    ///   1. <c>x-argus</c> / <c>x-ladon</c> 换成**真实**签名（不是 4 字节时间戳桩）
    ///   2. **去掉** <c>x-medusa</c> —— 带 Medusa 时服务端会返回空 body（实测）
    /// </summary>
    public static (string Query, Dictionary<string, string> Headers) SignPlayback(
        string path,
        IEnumerable<KeyValuePair<string, string>> pairs,
        long nowMillis,
        string deviceId,
        string versionName,
        byte[] body = null,
        Entropy entropy = null,
        string installId = "",
        string aid = "")
    {
        var (query, headers) = SignRequest(
            path, pairs, nowMillis, deviceId, versionName, body, entropy);
        int khronos = int.Parse(headers["x-khronos"]);
        byte[] bodyMd5 = body != null && body.Length > 0 ? Util.Md5(body) : new byte[16];
        string realAid = !string.IsNullOrEmpty(aid) ? aid : PlaybackAid(query);

        var rng = new Random();
        headers["x-argus"] = Argus.Header(
            query, bodyMd5, khronos, rng.Next(0, int.MaxValue), realAid, deviceId, installId);
        headers["x-ladon"] = Argus.LadonHeader(khronos, rng.Next(0, int.MaxValue), realAid);
        headers.Remove("x-medusa");
        return (query, headers);
    }
}

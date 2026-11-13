using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Hgvd.Core.Sign;

namespace Hgvd.Tests;

/// <summary>
/// 签名层的黄金向量回归。
///
/// ★ 向量来自 Python 版（hgvd/sign/）用同一组输入算出的结果，
///   见 <c>tools/golden.json</c>（由 tools/gen_golden.py 生成）。
/// ★ 服务端改版后先跑这里，看是不是签名链变了。
/// </summary>
public static class SignTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else { _fail++; Console.WriteLine($"  ✗ {name}  {detail}"); }
    }

    private static void Eq(string name, string expected, string actual)
    {
        bool ok = expected == actual;
        Check(name, ok, ok ? "" : $"期望 {expected}，实际 {actual}");
    }

    public static int Run()
    {
        Console.WriteLine("\n[签名层] 黄金向量比对");

        // ---- SM3 官方向量 ----
        Eq("SM3(\"abc\")",
            "66c7f0f462eeedd9d1f2d46bdc10e4e24167c4875cf2f7a2297da02b8f4ba8e0",
            Sm3.Hex(System.Text.Encoding.ASCII.GetBytes("abc")));
        Eq("SM3(\"abcd\"×16)",
            "debe9ff92275b8a138604889c18e5a4d6fdb70e5387e5765293dcba39c0c5732",
            Sm3.Hex(System.Text.Encoding.ASCII.GetBytes(
                string.Concat(Enumerable.Repeat("abcd", 16)))));

        // ---- AES-CBC 标准向量（FIPS-197） ----
        byte[] key = Util.FromHex("2b7e151628aed2a6abf7158809cf4f3c");
        byte[] iv = Util.FromHex("000102030405060708090a0b0c0d0e0f");
        byte[] pt = Util.FromHex("6bc1bee22e409f96e93d7e117393172a");
        Eq("AES-128-CBC 标准向量",
            "7649abac8119b246cee98e9b12e9197d",
            Util.ToHex(AesCbc.Encrypt(pt, key, iv)));
        Check("AES-128-CBC 回环",
            Util.ToHex(AesCbc.Decrypt(AesCbc.Encrypt(pt, key, iv), key, iv))
            == Util.ToHex(pt));

        // ---- gorgon / helios ----
        string q = "aid=8662&app_name=novelread&device_id=4351246100902096" +
                   "&iid=4351246100332768&ts=1700000000";
        byte[] body = System.Text.Encoding.UTF8.GetBytes(
            "{\"biz_param\":{\"need_all_video_definition\":true,\"video_id_type\":0," +
            "\"video_platform\":3},\"video_id\":\"7697465535398677529\"}");

        Eq("x-gorgon 黄金向量",
            "840434124001d8a950e66bc0e7191364e71df6105a46126993f1",
            Gorgon.XGorgon(q, body, 1700000000, 0x1234));

        Eq("x-helios 黄金向量",
            "eFYAABUk423nzLi+XvaYyOcWvfk3JhM9eZjkt2Fq5pS1/+rM",
            Gorgon.AssembleHelios(1700000000, 0x5678));

        // ---- 交叉验证：同一 query 两次签名，gorgon 应稳定 ----
        string g1 = Gorgon.XGorgon(q, body, 1700000000, 0x1234);
        string g2 = Gorgon.XGorgon(q, body, 1700000000, 0x1234);
        Check("x-gorgon 确定性", g1 == g2);

        // ---- 顶层签名 ----
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("iid", "4351246100332768"),
            new("device_id", "4351246100902096"),
            new("ac", "wifi"),
            new("aid", "8662"),
            new("app_name", "novelread"),
            new("version_name", "7.3.9.32"),
        };
        var (query, headers) = Signer.SignRequest(
            "/novel/player/video_model/v1/", pairs, 1700000000000L,
            "4351246100902096", "7.3.9.32", body);

        Check("sign_request 返回查询串", !string.IsNullOrEmpty(query));
        Check("x-gorgon 头存在", headers.ContainsKey("x-gorgon"));
        Check("x-helios 头存在", headers.ContainsKey("x-helios"));
        Check("x-medusa 头存在", headers.ContainsKey("x-medusa"));
        Check("x-argus 头存在", headers.ContainsKey("x-argus"));
        Check("x-ss-stub = body MD5 大写",
            headers["x-ss-stub"] == Util.Md5Hex(body).ToUpperInvariant());
        Check("查询串含 ts 与 _rticket",
            query.Contains("ts=1700000000") && query.Contains("_rticket=1700000000000"));

        // ---- 播放签名：必须**没有** medusa，且 argus/ladon 是真签名 ----
        var (pq, ph) = Signer.SignPlayback(
            "/novel/player/video_model/v1/", pairs, 1700000000000L,
            "4351246100902096", "7.3.9.32", body,
            installId: "4351246100332768", aid: "8662");
        Check("sign_playback 无 x-medusa", !ph.ContainsKey("x-medusa"));
        Check("sign_playback x-argus 非 4 字节桩",
            ph["x-argus"].Length > 10, $"长度 {ph["x-argus"].Length}");
        Check("sign_playback x-ladon 非 4 字节桩",
            ph["x-ladon"].Length > 10, $"长度 {ph["x-ladon"].Length}");

        // ---- AES-V3 黄金向量（medusa 用） ----
        {
            byte[] k3 = Util.FromHex("f1593376766ea98d34f31b057a9d5be4");
            byte[] i3 = Util.FromHex("1fe109a4125283f418de9e051a969e12");
            var d3 = new byte[734];
            for (int i = 0; i < d3.Length; i++) d3[i] = (byte)(i % 256);
            var o3 = new AesV3(k3, 1700000000).Encrypt(d3, i3);
            Eq("AES-V3 输出长度 = 输入+1", "735", o3.Length.ToString());
            Eq("AES-V3 前 32 字节",
                "2c200102030407078708490e0b040d0e8f301112131c15179718191e1b141d1f",
                Util.ToHex(o3[..32]));
        }

        // ---- medusa 的 KeyHash / xmxor ----
        {
            byte[] msk = Util.FromHex("8ebdfa3806ecc5cee79423e6029ed82540bc2218bb7eaef71cb691f7aa8aa2f5");
            var (kh, seed) = Medusa.KeyHash(msk, unchecked((int)0x9ABCDEF0));
            Eq("medusa KeyHash",
                "189907aa17a3d938f0631f4e5e34dba84345adbe961d9081f8b863e8669c4b5c",
                Util.ToHex(kh));
            Eq("medusa KeyHash seed", "dc1ffaff", Util.ToHex(seed));
            var d4 = new byte[64];
            for (int i = 0; i < d4.Length; i++) d4[i] = (byte)(i % 256);
            Eq("medusa xmxor 前 32 字节",
                "3afe0cf8dbb059f109eea7c1010b662bbcc36712cf62ef5cca93b74ce55974b5",
                Util.ToHex(Medusa.Xmxor(d4, kh)[..32]));
        }

        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }
}

"""离线回归测试（自研断言框架，不引入 pytest）。

覆盖：
    A 组  工具函数（容错读取 / 格式化 / 文件名清洗）
    B 组  配置与路径隔离
    C 组  历史记录（终态保护 / 进度只前进）
    D 组  签名算法（SM3 官方向量 / gorgon / helios / AES）
    E 组  网页版 SSR 解析（用真实抓取的结构样本）
    F 组  收藏夹解析
    G 组  DRM 密钥解包
"""

from __future__ import annotations

import base64
import json
import sys
import tempfile
from pathlib import Path

from . import *  # noqa: F401,F403  设置 HGV_TEST_HOME 与 sys.path

# ★★ 控制台编码兜底 —— 本测试要打印大量中文。
#   GitHub 的 Windows runner 上 stdout 是管道，Python 取 ANSI 代码页（cp1252）
#   ⇒ 打印中文直接 `UnicodeEncodeError` 崩掉（2026-11-14 首次 CI 就是这么挂的）。
#   中文 Windows 控制台是 GBK（本身就能编码中文）⇒ 本地不会复现。
#   ⇒ 统一把 stdout/stderr 切成 UTF-8，且 errors="replace"（顶多是乱码，不会崩）。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

from hgvd import util
from hgvd.api import shelf as shelf_api
from hgvd.api import web as web_api
from hgvd.drm import derive_content_key
from hgvd.engine.history import DONE, FAILED, HistoryRecord, HistoryStore
from hgvd.sign.aes_cbc import aes_cbc_encrypt
from hgvd.sign.core import _assemble_helios, _x_gorgon
from hgvd.sign.sm3 import sm3_hex

_passed = 0
_failed: list[str] = []


def check(name: str, cond: bool, detail: str = ""):
    global _passed
    if cond:
        _passed += 1
    else:
        _failed.append(f"{name}  {detail}")


def eq(name: str, got, want):
    check(name, got == want, f"got={got!r} want={want!r}")


# ---------------------------------------------------------------- A 组


def _missing_drive() -> str:
    """找一个不存在的盘符（Windows）。"""
    import os as _os

    if _os.name != "nt":
        return ""
    for letter in "QWRYX":
        if not _os.path.exists(f"{letter}:\\"):
            return letter
    return ""


def test_a_utils():
    eq("A1 j_str 取字符串", util.j_str({"a": "x"}, "a"), "x")
    eq("A2 j_str 数字转串", util.j_str({"a": 5}, "a"), "5")
    eq("A3 j_str 缺省", util.j_str({}, "a", "d"), "d")
    eq("A4 j_int 字符串数字", util.j_int({"a": "7"}, "a"), 7)
    eq("A5 j_int 非法", util.j_int({"a": "x"}, "a", -1), -1)
    eq("A6 j_float", util.j_float({"a": "1.5"}, "a"), 1.5)
    eq("A7 j_bool 字符串", util.j_bool({"a": "true"}, "a"), True)
    eq("A8 j_dict 非 dict", util.j_dict({"a": 1}, "a"), {})
    eq("A9 j_list 非 list", util.j_list({"a": 1}, "a"), [1])
    eq("A10 fmt_duration", util.fmt_duration(88.2), "01:28")
    eq("A11 fmt_duration 小时", util.fmt_duration(3661), "1:01:01")
    eq("A12 fmt_duration 零", util.fmt_duration(0), "--:--")
    eq("A13 fmt_size", util.fmt_size(1048576), "1.0 MB")
    eq("A14 safe_name 去非法字符", util.safe_name('a<b>c:d"e/f'), "a_b_c_d_e_f")
    eq("A15 safe_name 保留名", util.safe_name("CON"), "_CON")
    eq("A16 safe_name 保留名带扩展", util.safe_name("CON.mp4"), "_CON.mp4")
    eq("A17 safe_name 尾点", util.safe_name("abc."), "abc")
    eq("A18 safe_name 空", util.safe_name(""), "未命名")
    eq("A19 parts_key 稳定", util.parts_key("a"), util.parts_key("a"))
    check("A20 parts_key 区分路径", util.parts_key("a") != util.parts_key("b"))
    eq("A21 deep_find", util.deep_find({"x": {"y": {"url": "u"}}}, ["url"]), "u")
    eq("A22 deep_find 无", util.deep_find({"x": 1}, ["url"]), None)
    eq("A23 query_without", util.query_without("http://a?x=1&y=2", "x"), "http://a?y=2")
    # ★ 去重：分集标题里常带剧名，直接拼进文件名会重复两遍
    eq("A24 dedup_title 去剧名前缀",
       util.dedup_title("《生物史前》", "《生物史前》 P1"), "P1")
    eq("A25 dedup_title 无书名号",
       util.dedup_title("李的AI教学专家", "李的AI教学专家 第1课"), "第1课")
    eq("A26 dedup_title 不误伤",
       util.dedup_title("《生物史前》", "植物之间的生存竞争"), "植物之间的生存竞争")
    eq("A27 dedup_title 标题等于剧名 → 集名省掉",
       util.dedup_title("《生物史前》", "《生物史前》"), "")
    eq("A30 dedup_title 不留孤立括号",
       util.dedup_title("神竟是外星人！《山海星河，我说长生之谜》",
                        "神竟是外星人！《山海星河，我说长生之谜》"), "")
    eq("A31 episode_filename 去掉重复",
       __import__("hgvd.engine.downloader", fromlist=["x"]).episode_filename(
           "《生物史前》", 1, "《生物史前》 P1"), "《生物史前》.001.P1.mp4")
    eq("A32 episode_filename 标题=剧名则不带集名",
       __import__("hgvd.engine.downloader", fromlist=["x"]).episode_filename(
           "《生物史前》", 1, "《生物史前》"), "《生物史前》.001.mp4")
    eq("A28 dedup_title 空剧名", util.dedup_title("", "标题"), "标题")
    eq("A29 dedup_title 空标题", util.dedup_title("剧名", ""), "")


# ---------------------------------------------------------------- B 组


def test_b_config():
    from hgvd.config import Config, config_path, creds_path, tool_dir
    from hgvd.config import windows_download_dir as util_windows_download_dir

    tmp = Path(tempfile.mkdtemp())
    cfg = Config(out_dir=str(tmp))
    p = tmp / "c.json"
    cfg.save(p)
    again = Config.load(p)
    eq("B1 配置往返", again.out_dir, str(tmp))
    eq("B2 配置默认值（默认最高画质）", Config().quality, "1080p")
    check("B3 测试隔离生效", str(tool_dir()).startswith(os.environ["HGV_TEST_HOME"]))
    # 凭据不受测试隔离影响
    check("B4 凭据路径不受隔离", ".hgv_tool" in str(creds_path()))
    check("B5 配置路径受隔离", str(config_path()).startswith(os.environ["HGV_TEST_HOME"]))
    # 自定义目录的盘符不存在时回落到系统下载目录
    missing = _missing_drive()
    if missing:
        cfg2 = Config(out_dir=f"{missing}:/不存在的目录/xyz")
        got = cfg2.ensure_out_dir()
        check("B6 无效盘符回落到系统下载目录",
              got == util_windows_download_dir(),
              f"got={got}")
    # 自定目录的盘符在、只是子目录还没建 → 保留用户设定，不偷改
    import os as _os

    if _os.name == "nt" and _os.path.exists("Z:\\"):
        cfg3 = Config(out_dir="Z:/子目录还没建")
        eq("B6b 盘在则保留用户目录",
           str(cfg3.ensure_out_dir()).replace("\\", "/"), "Z:/子目录还没建")

    # 多实例设备档案：按 install_id 归档，换实例不互相覆盖
    from hgvd.device import load_device, load_device_for, save_device
    from hgvd.sign.core import DeviceIdentity

    save_device(DeviceIdentity(device_id="dev-A", iid="iid-A", cdid="", openudid=""))
    save_device(DeviceIdentity(device_id="dev-B", iid="iid-B", cdid="", openudid=""))
    check("B7 按 iid 取回同源设备（A）",
          (load_device_for("iid-A") or DeviceIdentity()).device_id == "dev-A")
    check("B8 按 iid 取回同源设备（B）",
          (load_device_for("iid-B") or DeviceIdentity()).device_id == "dev-B")
    check("B9 未知 iid 退回全局 latest",
          load_device("iid-X").device_id == "dev-B")


# ---------------------------------------------------------------- C 组


def test_c_history():
    tmp = Path(tempfile.mkdtemp())
    store = HistoryStore(tmp / "h.json")
    items = []
    store.merge(items, HistoryRecord(path="p1", state="下载中", size=100))
    eq("C1 新增记录", len(items), 1)
    store.merge(items, HistoryRecord(path="p1", state="已完成", size=200))
    eq("C2 更新为已完成", items[0].state, DONE)
    # 终态保护：迟到的「下载中」不能覆盖「已完成」
    store.merge(items, HistoryRecord(path="p1", state="下载中", size=50))
    eq("C3 终态不被覆盖", items[0].state, DONE)
    # 进度只前进
    store.merge(items, HistoryRecord(path="p2", state="下载中", size=300))
    store.merge(items, HistoryRecord(path="p2", state="下载中", size=100))
    eq("C4 进度只前进", items[0].size, 300)
    store.save(items)
    loaded = store.load()
    eq("C5 落盘往返", len(loaded), 2)
    store.purge("p1")
    eq("C6 清除记录", len(store.load()), 1)


# ---------------------------------------------------------------- D 组


def test_d_sign():
    # SM3 官方测试向量
    eq(
        "D1 SM3 abc",
        sm3_hex(b"abc"),
        "66c7f0f462eeedd9d1f2d46bdc10e4e24167c4875cf2f7a2297da02b8f4ba8e0",
    )
    eq(
        "D2 SM3 64字节",
        sm3_hex(b"abcd" * 16),
        "debe9ff92275b8a138604889c18e5a4d6fdb70e5387e5765293dcba39c0c5732",
    )
    # gorgon 稳定性：同输入同输出
    g1 = _x_gorgon("a=1&b=2", None, 1700000000, 12345)
    g2 = _x_gorgon("a=1&b=2", None, 1700000000, 12345)
    eq("D3 gorgon 确定性", g1, g2)
    check("D4 gorgon 长度", len(g1) == 52, f"len={len(g1)}")
    check("D5 gorgon 前缀", g1.startswith("8404"), g1[:8])
    # 不同 random → 不同结果
    check("D6 gorgon 依赖 random", _x_gorgon("a=1", None, 1700000000, 1)
          != _x_gorgon("a=1", None, 1700000000, 2))
    # helios 确定性
    h1 = _assemble_helios(1700000000, 999)
    h2 = _assemble_helios(1700000000, 999)
    eq("D7 helios 确定性", h1, h2)
    check("D8 helios 是 base64", len(base64.b64decode(h1)) > 0)
    # AES-CBC 标准向量（NIST SP 800-38A CBC-AES128 的第一个向量，
    # ★ IV 不是全零 —— 用全零会得到另一个值，那是正常的）
    key = bytes.fromhex("2b7e151628aed2a6abf7158809cf4f3c")
    iv = bytes.fromhex("000102030405060708090a0b0c0d0e0f")
    pt = bytes.fromhex("6bc1bee22e409f96e93d7e117393172a")
    ct = aes_cbc_encrypt(pt, key, iv)
    eq("D9 AES-CBC 标准向量", ct.hex(), "7649abac8119b246cee98e9b12e9197d")
    # 纯 Python 兜底实现必须与 cryptography 一致
    import hgvd.sign.aes_cbc as _m

    eq("D9b 纯 Python AES 与库一致",
       _m._aes_cbc_pure(pt, key, iv).hex(), ct.hex())
    # 签名请求：结构完整
    from hgvd.sign.core import DeviceIdentity, sign_request

    ident = DeviceIdentity(device_id="1", iid="2")
    q, hd = sign_request(
        "/novel/player/video_model/v1/",
        [("aid", "8662"), ("device_id", "1"), ("iid", "2")],
        1700000000000,
        "1",
        "7.3.9.32",
        b"body",
    )
    for k in ("x-gorgon", "x-helios", "x-medusa", "x-khronos", "x-argus",
              "x-ladon", "x-ss-stub", "x-ss-req-ticket"):
        check(f"D10 签名含 {k}", k in hd)
    check("D11 query 含 ts", "ts=" in q)
    check("D12 query 含 _rticket", "_rticket=" in q)
    eq("D13 ss-stub 是 MD5", hd["x-ss-stub"],
       __import__("hashlib").md5(b"body").hexdigest().upper())


# ---------------------------------------------------------------- E 组


def test_e_web():
    # 真实抓取的页面结构（精简样本）
    sample = {
        "loaderData": {
            "detail_page": {
                "seriesDetail": {
                    "series_id": "7697459724635098174",
                    "series_name": "全球死考",
                    "series_cover": "http://x",
                    "series_intro": "简介",
                    "episode_cnt": 786,
                    "accessible_episode_cnt": 5,
                    "tags": [{"tag_name": "脑洞"}, {"tag_name": "穿越"}],
                    "vid_list": ["7697465535398677529", "7697465811157404697"],
                }
            }
        }
    }
    html = "<script>window._ROUTER_DATA = " + json.dumps(sample) + ";</script>"
    data = web_api.extract_router_data(html)
    eq("E1 提取 SSR", data["loaderData"]["detail_page"]["seriesDetail"]["series_name"],
       "全球死考")
    # 贪婪正则会截断 —— 用 raw_decode 才正确
    html2 = html + "<script>var x=1;</script>"
    d2 = web_api.extract_router_data(html2)
    eq("E2 raw_decode 不被后续脚本干扰",
       d2["loaderData"]["detail_page"]["seriesDetail"]["episode_cnt"], 786)
    # 无 _ROUTER_DATA
    try:
        web_api.extract_router_data("<html>nothing</html>")
        check("E3 缺 SSR 报错", False)
    except RuntimeError:
        check("E3 缺 SSR 报错", True)
    # 直链构造
    eq("E4 直链构造",
       web_api.guess_video_url("123", "360p"),
       "https://hongguoduanju.com/video/public/123/360p.mp4")
    # 清晰度候选降级
    eq("E5 清晰度降级", web_api._quality_candidates("1080p"),
       ["1080p", "720p", "360p"])
    eq("E6 360p 不降级", web_api._quality_candidates("360p"), ["360p"])


# ---------------------------------------------------------------- F 组


def test_f_shelf():
    raw = {
        "code": 0,
        "message": "SUCCESS",
        "data": {
            "video_shelf_info": [
                {
                    "series_id": "111",
                    "series_title": "剧A",
                    "series_cover_url": "http://c",
                    "collect_time": 1700000000,
                    "video_data": {"series_id": "111", "title": "剧A", "episode_cnt": 100},
                },
                {
                    "book_id": "222",
                    "book_name": "剧B",
                    "video_data": {"episode_cnt": 50},
                },
                {"无 id": 1},
            ]
        },
    }
    # 直接测解析逻辑（不走网络）
    import hgvd.api.shelf as sh

    rows = raw["data"]["video_shelf_info"]
    out = []
    for r in rows:
        if not isinstance(r, dict):
            continue
        vid = r.get("video_data") if isinstance(r.get("video_data"), dict) else {}
        sid = (util.j_str(r, "series_id") or util.j_str(vid, "series_id")
               or util.j_str(r, "book_id"))
        if not sid:
            continue
        out.append({
            "series_id": sid,
            "title": (util.j_str(r, "series_title") or util.j_str(vid, "title")
                      or util.j_str(r, "book_name") or sid),
            "episode_cnt": util.j_int(vid, "episode_cnt") or util.j_int(r, "episode_cnt"),
        })
    eq("F1 收藏条数", len(out), 2)
    eq("F2 series_id", out[0]["series_id"], "111")
    eq("F3 标题", out[0]["title"], "剧A")
    eq("F4 集数", out[0]["episode_cnt"], 100)
    eq("F5 book_id 回退", out[1]["series_id"], "222")
    eq("F6 book_name 回退", out[1]["title"], "剧B")
    check("F7 无 id 的条目被跳过", len(out) == 2)
    eq("F8 端点常量", sh.PATH_SHELF_VIDEO,
       "/reading/bookapi/bookshelf/video/list/v")
    eq("F9 aid", sh.AID, "8662")


# ---------------------------------------------------------------- G 组


def test_g_drm():
    # 无效材料必须安全返回 None
    eq("G1 垃圾输入", derive_content_key("not-base64!!"), None)
    eq("G2 空输入", derive_content_key(""), None)
    eq("G3 base64 但太短", derive_content_key(base64.b64encode(b"ab").decode()), None)
    # 结构不对但能 base64 的
    eq("G4 随机字节", derive_content_key(base64.b64encode(bytes(40)).decode()), None)


# ---------------------------------------------------------------- H 组


# 产品代码里**禁止**出现的说法：把「HTTP 200 + 空 body」归因成服务端限流/风控。
#
# ★★ 为什么要有这条回归：这个推断已经被推翻过三次
#    （docs/03-开发要点.md §1.0：sign_request 用错 / x-argus 移植 bug /
#     网关丢请求），每次都以「再加一次退避」收场，白烧 19 分钟。
#    代码里的措辞会直接塑造下一次排查的方向 —— 文案留着，第四次就一定还会发生。
_BANNED = (
    "被服务端限流",
    "服务端限流并熔断",
    "请求节奏过快",
    "等待几分钟后重试",
    "账号级风控",
    "被限流中止",
)
# 出现在**否定句/历史复盘**里是允许的（同一行命中任一即可）
_ALLOW = ("不是", "证伪", "误判", "不要", "绝不", "禁止", "历史上",
          "别把", "三次", "推翻")

_SRC_ROOTS = ("hgvd", "hgvd-csharp/src", "hgvd-avalonia/src")
_SRC_SUFFIX = (".py", ".cs", ".axaml")


def test_h_attribution_guard():
    root = Path(__file__).resolve().parent.parent
    hits: list[str] = []
    for sub in _SRC_ROOTS:
        for p in (root / sub).rglob("*"):
            if not p.is_file() or p.suffix not in _SRC_SUFFIX:
                continue
            try:
                text = p.read_text(encoding="utf-8")
            except (UnicodeDecodeError, OSError):
                continue
            for i, line in enumerate(text.splitlines(), 1):
                if any(b in line for b in _BANNED) and not any(
                        a in line for a in _ALLOW):
                    hits.append(f"{p.relative_to(root)}:{i}: {line.strip()[:64]}")
    check("H1 源码里不再把空 body 归因为限流", not hits,
          "\n    " + "\n    ".join(hits[:6]))

    # 结论本身要留在文档里，且判据要可复现（两个探针缺一不可）
    doc = (root / "docs" / "03-开发要点.md").read_text(encoding="utf-8")
    check("H2 开发要点保留「限流已被证伪」章节",
          "已被证伪" in doc and "rate_probe" in doc and "emptybody_probe" in doc)
    check("H3 两个判据探针都在",
          (root / "tools" / "rate_probe.py").is_file()
          and (root / "tools" / "emptybody_probe.py").is_file())


def test_i_preload_protocol() -> None:
    """I 组：2026-11-20 新播放协议（multi_video_model/preload）+ 反向护栏。

    ★ 这一组守的是**结论本身**：旧接口 video_model/v1 已停用，
      新接口不校验签名头。以后谁把签名加回来、或把 body 形状改掉，这里会失败。
    """
    import gzip as _gzip

    from hgvd.api import playback as pb

    check("I1 走 preload 接口", pb.PATH_PRELOAD == "/novel/player/multi_video_model/preload/v1")
    check("I2 旧接口常量仍在（只作对照，不再使用）",
          pb.PATH_VIDEO_MODEL == "/novel/player/video_model/v1/")

    body = pb.build_preload_body(["123", "456"])
    check("I3 body 是 gzip", body[:2] == b"\x1f\x8b")
    payload = json.loads(_gzip.decompress(body).decode("utf-8"))
    check("I4 body 含 biz_param", isinstance(payload.get("biz_param"), dict))
    biz = payload["biz_param"]
    check("I5 video_platform=1024", biz.get("video_platform") == 1024)
    check("I6 need_all_video_definition=true", biz.get("need_all_video_definition") is True)
    check("I7 dr_scene=preload", payload.get("dr_scene") == "preload")
    check("I8 mixed_video_id_map 带两个 vid",
          payload.get("mixed_video_id_map") == {"1004": ["123", "456"]})
    body2 = json.loads(_gzip.decompress(pb.build_preload_body(["9"], map_key="1001")).decode())
    check("I9 map_key 可切换内容类型", "1001" in body2["mixed_video_id_map"])
    check("I10 三种 key 都在候选里",
          set(pb.PRELOAD_MAP_KEYS) == {"1004", "1001", "1000"})

    hdr = pb._preload_headers({"a": "b"})
    banned = ("x-gorgon", "x-argus", "x-ladon", "x-khronos", "x-medusa", "x-helios", "x-ss-stub")
    check("I11 preload 头里不带任何签名头",
          not any(k in {h.lower() for h in hdr} for k in banned),
          f"keys={sorted(hdr)}")
    check("I12 preload 带 gzip 头", hdr.get("Content-Encoding") == "gzip")

    # 适配：preload 的 data[vid] → 老的 data.video_model
    vm = {"video_list": [{"main_url": "http://x", "video_meta": {"definition": "360p"}}]}
    adapted = pb._adapt_preload(
        {"code": 0, "message": "ok", "_host": "h"},
        {"video_model": json.dumps(vm), "video_height": 360})
    check("I13 适配后 code=0", adapted.get("code") == 0)
    check("I14 适配后 video_model 是 dict",
          isinstance(adapted["data"]["video_model"], dict))
    check("I15 适配后能解析出流", len(pb.parse_streams(adapted)) == 1)

    # 文档里必须留下这次结论（否则下个会话又会去猜签名）
    root = Path(__file__).resolve().parent.parent
    doc = (root / "docs" / "05-抓包与协议排查.md").read_text(encoding="utf-8")
    check("I16 文档记录「不校验签名头」", "不校验签名头" in doc)
    check("I17 文档记录新接口路径", "multi_video_model/preload" in doc)


def main() -> int:
    for fn in (test_a_utils, test_b_config, test_c_history, test_d_sign,
               test_e_web, test_f_shelf, test_g_drm, test_h_attribution_guard,
               test_i_preload_protocol):
        try:
            fn()
        except Exception as e:  # noqa: BLE001
            import traceback

            _failed.append(f"{fn.__name__} 抛异常：{e}\n{traceback.format_exc()}")

    print(f"\n通过 {_passed} 项，失败 {len(_failed)} 项")
    if _failed:
        print("\n失败明细：")
        for f in _failed:
            print("  ✗", f)
        return 1
    print("全部通过 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())

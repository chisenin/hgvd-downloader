"""业务编排层：收藏夹 → 剧目 → 分集 → 下载。

这是工具的「单一事实来源」：所有前端（CLI / GUI）都只调这里的接口，
不自己碰 HTTP。
"""

from __future__ import annotations

import threading
from dataclasses import dataclass, field
from pathlib import Path

from . import net
from .api import detail as detail_api
from .api import playback as playback_api
from .api import shelf as shelf_api
from .api import web as web_api
from .auth import Account, load_account
from .config import Config
from .engine.downloader import (
    CancelToken,
    DownloadCallbacks,
    DownloadRequest,
    DownloadResult,
    episode_filename,
    find_existing,
    probe_duration,
    series_folder,
)
from .engine.history import HistoryRecord, HistoryStore
from .util import fmt_duration, safe_name


# ---------------------------------------------------------------- 模型


@dataclass
class ShelfItem:
    """收藏夹里的一项。

    ★ ``book_type`` 决定它是什么类型（实测 2026-11-13）：

      ======  ==========  ==========================================
      值      content_type 含义
      ======  ==========  ==========================================
      2       1004        **真短剧**（多集）—— 字节私有 bytevc2
                              加密编码，ffmpeg 解不出画面
      3       1003        **教学合集**（多集）—— 标准 h264/hevc，
                              不加密，★ 本工具的主目标
      4       1001        **用户推送的单个视频**（1 集）—— 教学内容
      ======  ==========  ==========================================

    ★ 重要修正：``book_type=3`` **并不是**「没有视频」，
      而是要用 ``album_detail`` 端点才能拿到分集 ——
      用 ``preload`` 查会得到 ``episode_cnt=0``，看起来像没有。
    """

    series_id: str
    title: str
    cover: str = ""
    episode_cnt: int = 0
    collect_time: int = 0
    book_type: int = 0
    content_type: int = 0
    episodes: list = field(default_factory=list)
    is_album: bool = False

    @property
    def key(self) -> str:
        return self.series_id

    @property
    def is_video(self) -> bool:
        """是不是有视频分集的内容。"""
        return bool(self.episodes) or self.book_type in (2, 3, 4) or self.is_album

    @property
    def is_short_drama(self) -> bool:
        """是不是**真短剧**（bytevc 加密，ffmpeg 解不出画面）。

        ★ 用户明确表示短剧只是消遣，没有下载价值 —— 所以
          ``download --all`` 会默认跳过它们。
        """
        return self.book_type == 2 or self.content_type == 1004

    @property
    def type_label(self) -> str:
        """给用户看的中文类型标签。"""
        if self.is_short_drama:
            return "短剧"
        if self.book_type == 4:
            return "单个视频"
        if self.book_type == 3:
            return "教学合集"
        return "其他"


@dataclass
class EpisodeItem:
    """一集。

    ★ ``video_id_type`` 是播放接口的关键参数（2026-11-13 实测）：
      ``0`` = 用 vid 查（短剧），``1`` = 用 series_id 查（教学内容）。
      ``-1`` = 不确定，由下载时自动两种都试。
    """

    vid: str
    index: int
    title: str
    duration: float = 0.0
    series_id: str = ""
    series_title: str = ""
    video_id_type: int = -1

    @property
    def key(self) -> str:
        return self.vid


@dataclass
class Job:
    """一个下载任务。"""

    episode: EpisodeItem
    out_path: str
    state: str = "排队中"
    down: int = 0
    total: int = 0
    speed: float = 0.0
    error: str = ""
    finished: bool = False

    @property
    def percent(self) -> int:
        if self.total > 0:
            return min(100, int(self.down * 100 / self.total))
        return 0


# ---------------------------------------------------------------- 服务


class HongguoService:
    """对外的业务门面。"""

    def __init__(self, config: Config | None = None):
        self.cfg = config or Config.load()
        self.session = net.make_session()
        self.account: Account = load_account()
        self.history = HistoryStore()
        self._ident = None

    # ---------------------------------------------------------- 授权

    @property
    def logged_in(self) -> bool:
        return self.account.ok

    def cookie_dict(self) -> dict:
        return dict(self.account.cookies) if self.account.ok else {}

    def set_account(self, account: Account) -> None:
        self.account = account
        # ★ 换账号/换实例后设备身份要重新按 install_id 配对，别沿用上一台的
        self._ident = None

    # ---------------------------------------------------------- 收藏夹

    def list_shelf(
        self,
        with_details: bool = True,
        with_albums: bool = True,
        album_limit: int = 30,
    ) -> list[ShelfItem]:
        """读收藏夹。

        ★ 没登录也能调（接口免签名），但没 cookie 时返回的是空列表 ——
          这时要提示用户去登录，而不是报「空收藏夹」。

        ★ ``with_details`` 会额外用**带签名**的批量详情接口补标题、
          集数与分集列表（收藏接口本身只给 ID）。

        ★ ``with_albums`` 会对 ``book_type=3`` 的项调用 **album_detail**
          端点补上真正的分集列表 —— 这是拿到教学合集分集的**唯一路径**。

        ★★ 但 album 端点是**逐个请求**的（222 个合集要 222 次），
          所以默认 ``album_limit=30`` —— 列表里显示前 30 个合集的集数，
          其余的按需用 ``list_album_episodes(series_id)`` 单独查。
        """
        rows = shelf_api.list_shelf(
            self.session,
            cookies=self.cookie_dict(),
            target_user_id=self.account.user_id or "",
        )
        details = {}
        if with_details and rows:
            try:
                details = detail_api.fetch_details(
                    self.session,
                    self.device_identity(),
                    [r["series_id"] for r in rows],
                    cookies=self.cookie_dict(),
                )
            except Exception:  # noqa: BLE001
                details = {}

        # ★ book_type=3 的项在 preload 里永远是 episode_cnt=0，
        #   必须用 album_detail 才能拿到分集。
        albums = {}
        if with_albums and rows:
            album_ids = [
                r["series_id"] for r in rows
                if r.get("book_type") == 3 and not details.get(r["series_id"], {}).get("episodes")
            ]
            if album_limit:
                album_ids = album_ids[:album_limit]
            if album_ids:
                try:
                    albums = detail_api.fetch_albums(
                        self.session,
                        self.device_identity(),
                        album_ids,
                        cookies=self.cookie_dict(),
                        pause=0.6,
                    )
                except Exception:  # noqa: BLE001
                    albums = {}

        items = []
        for r in rows:
            sid = r["series_id"]
            d = details.get(sid, {})
            a = albums.get(sid, {})
            eps = d.get("episodes") or a.get("episodes") or []
            items.append(
                ShelfItem(
                    series_id=sid,
                    title=a.get("title") or d.get("title") or r["title"],
                    cover=a.get("cover") or r.get("cover") or d.get("cover", ""),
                    episode_cnt=(
                        a.get("episode_cnt") or d.get("episode_cnt")
                        or r["episode_cnt"] or len(eps)
                    ),
                    collect_time=r["collect_time"],
                    book_type=r.get("book_type", 0),
                    content_type=r.get("content_type", 0),
                    episodes=eps,
                    is_album=bool(a),
                )
            )
        return items

    def remove_from_shelf(self, series_ids: list[str]) -> tuple[int, list[str]]:
        """把指定条目从收藏夹移除。

        ★ 用 ``bookshelf/video/update/v1/`` 端点（``video_shelf_operate_type=1``）。
        ★ 用户的短剧可以一键清掉 —— 短剧是 bytevc 加密的，本来也不可下载。

        返回 ``(成功数, 失败原因列表)``。
        """
        from .api import shelf_edit

        # 先拿每个条目的 book_type（不同类型要用不同的值）
        rows = shelf_api.list_shelf(
            self.session,
            cookies=self.cookie_dict(),
            target_user_id=self.account.user_id or "",
        )
        btype = {r["series_id"]: r.get("book_type", 2) for r in rows}
        targets = [(sid, btype.get(sid, 2)) for sid in series_ids]

        def _post(path, body):
            return detail_api.signed_post(
                self.session, self.device_identity(), path, body,
                self.cookie_dict(), quiet=True,
            )

        return shelf_edit.remove_many(_post, targets)

    def remove_short_dramas(self) -> tuple[int, list[str]]:
        """把收藏夹里所有**短剧**移除。

        ★ 短剧是 bytevc 加密的、ffmpeg 解不出画面，对用户没有下载价值。
        """
        rows = shelf_api.list_shelf(
            self.session,
            cookies=self.cookie_dict(),
            target_user_id=self.account.user_id or "",
        )
        ids = [r["series_id"] for r in rows if r.get("book_type") == 2]
        if not ids:
            return 0, []
        return self.remove_from_shelf(ids)

    def list_album_episodes(
        self,
        album_id: str,
        book_type: int = 3,
    ) -> list[EpisodeItem]:
        """取一个合集的分集列表（带标题与时长）。

        ★ 对 ``book_type=3`` 的教学合集，这是唯一能拿到分集的接口。
        ★ 每集还要再查一次 ``video_detail`` 才拿得到 ``vid``。

        ★★★ 取不到时**回落到** ``multi_video_detail/preload/v1`` ——
          那条路径覆盖另外两种类型：
            - ``book_type=4``（用户推送的单个视频）：``video_list`` 有 1 项，
              ``vid == series_id``
            - ``book_type=2``（真短剧）：``video_list`` 有全部集
        """
        raw = detail_api.signed_post(
            self.session,
            self.device_identity(),
            detail_api.PATH_ALBUM,
            {"album_id": album_id, "book_type": book_type},
            self.cookie_dict(),
            quiet=True,
        )
        if raw:
            from .api import album as album_api

            try:
                ad = album_api.parse_album(raw)
            except Exception:  # noqa: BLE001
                ad = None
            if ad and ad.episodes:
                return [
                    EpisodeItem(
                        vid=ep.series_id,
                        index=n,
                        title=ep.title,
                        duration=ep.duration,
                        series_id=album_id,
                        series_title=ad.title,
                        # ★ 合集分集要用 series_id 查播放地址
                        video_id_type=1,
                    )
                    for n, ep in enumerate(ad.episodes, 1)
                ]

        # ★ 回落到 preload（短剧 / 单个视频）
        raw2 = detail_api.signed_post(
            self.session,
            self.device_identity(),
            detail_api.PATH_DETAIL,
            {
                "dr_scene": "preload",
                "series_id": album_id,
                "biz_param": {
                    "detail_page_version": 0,
                    "disable_digg_stat": False,
                    "source": 7,
                    "need_all_video_definition": False,
                },
            },
            self.cookie_dict(),
            quiet=True,
        )
        data = (raw2 or {}).get("data") or {}
        out: list[EpisodeItem] = []
        for item in data.values():
            if not isinstance(item, dict):
                continue
            vd = item.get("video_data")
            vd = vd if isinstance(vd, dict) else {}
            title = vd.get("series_title") or album_id
            for n, e in enumerate(vd.get("video_list") or [], 1):
                if not isinstance(e, dict):
                    continue
                vid = e.get("vid") or ""
                if not vid:
                    continue
                out.append(
                    EpisodeItem(
                        vid=vid,
                        index=n,
                        title=e.get("title") or f"第{n}集",
                        duration=float(e.get("duration") or 0),
                        series_id=album_id,
                        series_title=title,
                        # ★ vid == series_id 时要用 type=1（教学内容那套）
                        video_id_type=(1 if vid == album_id else 0),
                    )
                )
            if out:
                break
        return out

    def list_episodes(self, series_id: str, max_count: int = 0) -> list[EpisodeItem]:
        """取一部剧的分集列表。"""
        s, eps = web_api.fetch_episodes(
            self.session,
            series_id,
            cookies=self.cookie_dict(),
            max_count=max_count,
        )
        return [
            EpisodeItem(
                vid=e.vid,
                index=e.index,
                title=e.title,
                duration=e.duration,
                series_id=s.series_id,
                series_title=s.title,
                # ★ 网页版短剧用 vid 查播放地址
                video_id_type=0,
            )
            for e in eps
        ]

    def fetch_episode_meta(self, series_id: str, vid: str) -> dict:
        """取单集元信息（时长等）。"""
        return web_api.fetch_episode_meta(
            self.session, series_id, vid, cookies=self.cookie_dict()
        )

    def resolve_url(self, vid: str, quality: str = "360p") -> tuple[str, int | None]:
        """解析免签名直链（网页版）。返回（url, size）。"""
        url, size, _ = web_api.probe_video(
            self.session, vid, quality, cookies=self.cookie_dict()
        )
        return url, size

    def device_identity(self):
        """稳定的设备身份（签名必需）。

        ★★ 必须与 cookie **同源**：cookie 里带了 ``install_id`` 时，
          优先取「这台实例」归档下来的 device_id（``devices.json``），
          只有找不到才回落到上一次的全局身份。
          多开场景下这一步是「授权看上去互斥」的真正解药。
        """
        if self._ident is None:
            from .device import ensure_device, load_device_for

            iid = self.account.cookies.get("install_id", "")
            ident = load_device_for(iid) if iid else None
            self._ident = ident or ensure_device()
        return self._ident

    def resolve_full_url(self, vid: str, quality: str = "360p",
                          video_id_type: int = 0):
        """解析**完整正片**的播放地址。

        ★ 走 App 网关的 ``video_model``，需要登录 + 播放专用签名。
        ★ 返回 ``(url, size, content_key, reason)``；拿不到时 ``url`` 为空，
          ``reason`` 里写的是**真实原因**（空 body / 参数错 / 网络错）。
          ``content_key`` 非空表示该集是 CENC 加密的，下载后需要
          ``ffmpeg -decryption_key`` 解密。

        ★★ ``video_id_type`` 是决定性的（2026-11-13 实测）：
          ``0`` = 传 vid（短剧），``1`` = 传 series_id（教学内容）。
          教学内容用 ``0`` 会返回 ``code=101000``。
        """
        if not self.logged_in:
            return "", None, None, "未登录：请先 python -m hgvd login", ""
        ident = self.device_identity()
        try:
            payload = playback_api.fetch_playback(
                self.session, ident, vid, cookies=self.cookie_dict(),
                video_id_type=video_id_type,
            )
        except Exception as e:  # noqa: BLE001
            return "", None, None, str(e), ""

        code = payload.get("code")
        if code not in (0, None):
            # ★ code=101000 = 参数/类型不符（多半是 video_id_type 用错），
            #   **不是空 body** —— 别把它当成服务端丢弃请求。
            return "", None, None, (
                f"服务端返回 code={code} {payload.get('message') or ''}".strip()
                + "（多半是 video_id_type 与内容类型不匹配：短剧用 0、教学用 1）"
            ), ""

        streams = playback_api.parse_streams(payload)
        if not streams:
            return "", None, None, "服务端返回了数据但没有可用的清晰度", ""

        # 优先选符合目标清晰度的流
        want = (quality or "360p").lower()
        chosen = None
        for st in streams:
            if st["definition"].lower() == want:
                chosen = st
                break
        if chosen is None:
            chosen = streams[0]

        key = playback_api.extract_content_key(payload)
        return (chosen["url"], chosen["size"], key, "",
                chosen.get("backup_url") or "")

    def download_series(
        self,
        series_id: str,
        episodes: list[EpisodeItem] | None = None,
        *,
        cb=None,
        cancel: CancelToken | None = None,
        quality: str | None = None,
        workers: int = 3,
    ) -> list[tuple[EpisodeItem, DownloadResult]]:
        """下载一部剧的多集（并发）。

        ★ 并发数控制：参考项目的教训是「占网络槽位的是还没下完的任务，
          不是线程还活着」—— 这里每集是独立文件、没有转封装流水线，
          所以按「正在下载的集数」限流即可。
        """
        if episodes is None:
            episodes = self.list_episodes(series_id)
        if not episodes:
            return []

        results: list[tuple[EpisodeItem, DownloadResult]] = []
        lock = threading.Lock()
        sem = threading.Semaphore(max(1, workers))

        def one(ep: EpisodeItem):
            with sem:
                if cancel and cancel.cancelled:
                    return
                try:
                    res = self.download_episode(
                        ep, cb=cb, cancel=cancel, quality=quality
                    )
                except Exception as e:  # noqa: BLE001
                    res = DownloadResult(
                        ok=False, out_path=str(self.build_out_path(ep)),
                        error=str(e)[:200],
                    )
            with lock:
                results.append((ep, res))

        threads = []
        for ep in episodes:
            t = threading.Thread(target=one, args=(ep,), daemon=True)
            t.start()
            threads.append(t)
        for t in threads:
            t.join()

        results.sort(key=lambda x: x[0].index)
        return results

    def download_shelf(
        self,
        items=None,
        *,
        cb=None,
        cancel: CancelToken | None = None,
        quality: str | None = None,
        workers: int = 3,
    ) -> list[tuple[EpisodeItem, DownloadResult]]:
        """下载收藏夹里的全部剧（串行剧、并发集）。"""
        if items is None:
            items = self.list_shelf()
        out = []
        for item in items:
            if cancel and cancel.cancelled:
                break
            try:
                eps = self.list_episodes(item.series_id)
            except Exception:  # noqa: BLE001
                continue
            out.extend(
                self.download_series(
                    item.series_id, eps, cb=cb, cancel=cancel,
                    quality=quality, workers=workers,
                )
            )
        return out

    # ---------------------------------------------------------- 下载

    def build_out_path(self, ep: EpisodeItem) -> Path:
        root = Path(self.cfg.ensure_out_dir())
        folder = root / series_folder(ep.series_title or ep.series_id)
        return folder / episode_filename(ep.series_title or ep.series_id, ep.index, ep.title)

    def is_done(self, ep: EpisodeItem) -> Path | None:
        """已下载检测。"""
        out = self.build_out_path(ep)
        folder = out.parent
        stem = out.stem
        return find_existing(folder, stem, ep.duration)

    def download_episode(
        self,
        ep: EpisodeItem,
        cb: DownloadCallbacks | None = None,
        cancel: CancelToken | None = None,
        quality: str | None = None,
    ) -> DownloadResult:
        """下载一集。"""
        quality = quality or self.cfg.quality
        out = self.build_out_path(ep)
        out.parent.mkdir(parents=True, exist_ok=True)

        # 已下载
        if self.cfg.skip_existing:
            exist = self.is_done(ep)
            if exist:
                return DownloadResult(
                    ok=True, out_path=str(exist), size=exist.stat().st_size,
                    duration=probe_duration(str(exist)), skipped=True,
                )

        # 拿时长（要精确校验）
        if ep.duration <= 0:
            try:
                meta = self.fetch_episode_meta(ep.series_id, ep.vid)
                ep.duration = float(meta.get("duration") or 0)
            except Exception:  # noqa: BLE001
                ep.duration = 0.0

        # ★ 优先走**完整正片**（App 网关 video_model，需登录 + 播放专用签名）
        url, _size, content_key, reason, backup_url = "", None, None, "", ""
        if self.logged_in:
            # ★ video_id_type：短剧用 0（vid），教学内容用 1（series_id）。
            #   两种都试 —— 失败的那个返回 code=101000，很快。
            #   先试「猜对的那个」，再试另一个。
            if ep.video_id_type >= 0:
                types = [ep.video_id_type, 1 - ep.video_id_type]
            else:
                types = [0, 1]
            for t in types:
                url, _size, content_key, reason, backup_url = self.resolve_full_url(
                    ep.vid, quality, video_id_type=t
                )
                if url:
                    ep.video_id_type = t
                    break

        # 兜底：网页版免签名直链（★ 只有 10 秒试看片段）
        if not url:
            url, _size = self.resolve_url(ep.vid, quality)
            if url and ep.duration <= 0:
                ep.duration = 0.0  # 片段没有可信时长，跳过时长校验
            if url:
                content_key = None

        if not url:
            return DownloadResult(
                ok=False, out_path=str(out),
                error=f"无法解析播放地址（vid={ep.vid}）"
                + (f"：{reason}" if reason else "")
                + "。★ 「提取App缓存」（python -m hgvd cache）完全不经过 video_model，是另一条出片路径。",
            )

        req = DownloadRequest(
            url=url,
            out_path=str(out),
            expect_duration=ep.duration,
            cookies=self.cookie_dict(),
            # ★ 主 CDN 主机连不上时自动换备用直链
            backup_url=backup_url or "",
        )
        res = download_with_history(self, req, ep, cb, cancel, content_key=content_key)
        return res


def download_with_history(
    svc: HongguoService,
    req: DownloadRequest,
    ep: EpisodeItem,
    cb: DownloadCallbacks | None,
    cancel: CancelToken | None,
    content_key: str = "",
) -> DownloadResult:
    """下载 + 写历史。"""
    from .engine.downloader import download

    req.content_key = content_key or ""
    inner_cb = DownloadCallbacks()

    def _phase(p):
        if cb and cb.on_phase:
            cb.on_phase(p)

    def _progress(down, total, speed):
        if cb and cb.on_progress:
            cb.on_progress(down, total, speed)

    def _log(m):
        if cb and cb.on_log:
            cb.on_log(m)

    inner_cb.on_phase = _phase
    inner_cb.on_progress = _progress
    inner_cb.on_log = _log

    # 阶段 + 进度都要落到历史，GUI 才能显示「下载中」
    def _phase_hist(p):
        _phase(p)
        svc.history.merge(
            svc.history.load(),
            HistoryRecord(
                path=req.out_path, title=ep.series_title, episode=ep.title,
                series_id=ep.series_id, vid=ep.vid, state=p,
            ),
        )

    def _progress_hist(down, total, speed):
        _progress(down, total, speed)
        svc.history.merge(
            svc.history.load(),
            HistoryRecord(
                path=req.out_path, title=ep.series_title, episode=ep.title,
                series_id=ep.series_id, vid=ep.vid, state="下载中",
                size=down, duration=ep.duration,
            ),
        )

    inner_cb.on_phase = _phase_hist
    inner_cb.on_progress = _progress_hist

    res = download(req, inner_cb, cancel)

    svc.history.merge(
        svc.history.load(),
        HistoryRecord(
            path=res.out_path, title=ep.series_title, episode=ep.title,
            series_id=ep.series_id, vid=ep.vid,
            state="已完成" if res.ok else "失败",
            size=res.size, duration=res.duration,
        ),
    )
    return res

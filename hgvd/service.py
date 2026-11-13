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

    ★ ``book_type`` 决定它是**短剧视频**（2，服务端有视频分集）
      还是番茄小说的**「书」**（3，图文/听书，服务端没有视频分集）。
    """

    series_id: str
    title: str
    cover: str = ""
    episode_cnt: int = 0
    collect_time: int = 0
    book_type: int = 0
    content_type: int = 0
    episodes: list = field(default_factory=list)

    @property
    def key(self) -> str:
        return self.series_id

    @property
    def is_video(self) -> bool:
        """是不是有视频分集的内容。"""
        return bool(self.episodes) or self.book_type == 2


@dataclass
class EpisodeItem:
    """一集。"""

    vid: str
    index: int
    title: str
    duration: float = 0.0
    series_id: str = ""
    series_title: str = ""

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

    # ---------------------------------------------------------- 收藏夹

    def list_shelf(self, with_details: bool = True) -> list[ShelfItem]:
        """读收藏夹。

        ★ 没登录也能调（接口免签名），但没 cookie 时返回的是空列表 ——
          这时要提示用户去登录，而不是报「空收藏夹」。

        ★ ``with_details`` 会额外用**带签名**的批量详情接口补标题、
          集数与分集列表（收藏接口本身只给 ID）。
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

        items = []
        for r in rows:
            d = details.get(r["series_id"], {})
            eps = d.get("episodes") or []
            items.append(
                ShelfItem(
                    series_id=r["series_id"],
                    title=d.get("title") or r["title"],
                    cover=r.get("cover") or d.get("cover", ""),
                    episode_cnt=d.get("episode_cnt") or r["episode_cnt"] or len(eps),
                    collect_time=r["collect_time"],
                    book_type=r.get("book_type", 0),
                    content_type=r.get("content_type", 0),
                    episodes=eps,
                )
            )
        return items

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
        """稳定的设备身份（签名必需）。"""
        if self._ident is None:
            from .device import ensure_device

            self._ident = ensure_device()
        return self._ident

    def resolve_full_url(self, vid: str, quality: str = "360p"):
        """解析**完整正片**的播放地址。

        ★ 走 App 网关的 ``video_model``，需要登录 + 完整签名。
        ★ 返回 ``(url, size, content_key)``；拿不到时 ``url`` 为空。
          ``content_key`` 非空表示该集是 CENC 加密的，下载后需要
          ``ffmpeg -decryption_key`` 解密。
        """
        if not self.logged_in:
            return "", None, None
        ident = self.device_identity()
        try:
            payload = playback_api.fetch_playback(
                self.session, ident, vid, cookies=self.cookie_dict()
            )
        except Exception:  # noqa: BLE001
            return "", None, None

        streams = playback_api.parse_streams(payload)
        if not streams:
            return "", None, None

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
        return chosen["url"], chosen["size"], key

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

        # ★ 优先走**完整正片**（App 网关 video_model，需登录 + 完整签名）
        url, _size, content_key = "", None, None
        if self.logged_in:
            url, _size, content_key = self.resolve_full_url(ep.vid, quality)

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
                + ("（未登录：请先 python -m hgvd login）" if not self.logged_in else ""),
            )

        req = DownloadRequest(
            url=url,
            out_path=str(out),
            expect_duration=ep.duration,
            cookies=self.cookie_dict(),
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

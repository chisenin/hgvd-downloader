"""下载历史。

★ 参考项目的关键教训：
  - 「已完成 / 失败」是终态，**不能被**「排队中 / 下载中」覆盖
  - 「失败 / 未完成」的分片必须**保留**（那是续传的本钱）
  - 只有「已完成」的记录才允许清理缓存
  - 进度只前进不后退
"""

from __future__ import annotations

import json
import time
from dataclasses import dataclass, asdict

from ..config import history_path
from ..util import md5_short

MAX_HISTORY = 500

# 终态词
DONE = "已完成"
FAILED = "失败"
RUNNING = ("排队中", "下载中", "解析中", "校验中")


@dataclass
class HistoryRecord:
    path: str = ""
    title: str = ""          # 剧名
    episode: str = ""        # 集名
    series_id: str = ""
    vid: str = ""
    state: str = ""
    size: int = 0
    duration: float = 0.0
    ts: float = 0.0

    @property
    def key(self) -> str:
        return self.path


class HistoryStore:
    """下载历史（JSON 文件）。"""

    def __init__(self, path=None):
        self.path = path or history_path()

    def load(self) -> list[HistoryRecord]:
        if not self.path.exists():
            return []
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except Exception:  # noqa: BLE001
            return []
        out = []
        for r in raw if isinstance(raw, list) else []:
            if not isinstance(r, dict):
                continue
            out.append(
                HistoryRecord(
                    path=str(r.get("path") or ""),
                    title=str(r.get("title") or ""),
                    episode=str(r.get("episode") or ""),
                    series_id=str(r.get("series_id") or ""),
                    vid=str(r.get("vid") or ""),
                    state=str(r.get("state") or ""),
                    size=int(r.get("size") or 0),
                    duration=float(r.get("duration") or 0),
                    ts=float(r.get("ts") or 0),
                )
            )
        return out

    def save(self, items: list[HistoryRecord]) -> bool:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        data = [asdict(r) for r in items[:MAX_HISTORY]]
        tmp = self.path.with_suffix(self.path.suffix + ".tmp")
        tmp.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
        tmp.replace(self.path)
        return True

    def merge(self, items: list[HistoryRecord], rec: HistoryRecord) -> bool:
        """按成品路径合并一条记录。

        ★ 终态保护：旧的「已完成 / 失败」不被「排队中 / 下载中」覆盖。
        """
        rec.ts = time.time()
        for i, old in enumerate(items):
            if old.path != rec.path:
                continue
            if old.state in (DONE, FAILED) and rec.state in RUNNING:
                return False
            if old.size > rec.size and rec.state in RUNNING:
                # 进度只前进不后退
                rec.size = old.size
            items[i] = rec
            return True
        items.insert(0, rec)
        if len(items) > MAX_HISTORY:
            del items[MAX_HISTORY:]
        return True

    def sort(self, items: list[HistoryRecord]) -> list[HistoryRecord]:
        return sorted(items, key=lambda r: r.ts, reverse=True)

    def purge(self, out_path: str) -> None:
        """删掉某成品的记录（用于右键删除成品）。"""
        items = self.load()
        rest = [r for r in items if r.path != out_path]
        if len(rest) != len(items):
            self.save(rest)

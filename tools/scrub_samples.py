#!/usr/bin/env python
"""抹掉 docs/samples/*.json 里的**签名 CDN 授权 URL**，只保留响应结构。

为什么要抹：
  `video_model` 里的 main_url / backup_url / fallback_api 都带
  `auth_key` / `auth_policy` 之类的**内容授权令牌**（虽然会过期，
  但本质上等同于把访问凭据公开发布）。
  公开仓库里只应保留**响应结构**（字段名、状态码、时长、分辨率），
  这些才是排查时真正要对照的东西。

用法：python tools/scrub_samples.py [--inplace]
"""
import json
import pathlib
import re
import sys

SAMPLES = pathlib.Path(__file__).resolve().parent.parent / "docs" / "samples"

# 需要打码的字段（键名）
URL_KEYS = {"main_url", "backup_url", "fallback_api", "url"}


def scrub_obj(o):
    """递归替换带令牌的 URL 字段。"""
    if isinstance(o, dict):
        out = {}
        for k, v in o.items():
            if k in URL_KEYS and isinstance(v, str):
                out[k] = redact(v)
            elif k == "fallback_api" and isinstance(v, dict):
                out[k] = {kk: (redact(vv) if isinstance(vv, str) else scrub_obj(vv))
                          for kk, vv in v.items()}
            else:
                out[k] = scrub_obj(v)
        return out
    if isinstance(o, list):
        return [scrub_obj(x) for x in o]
    if isinstance(o, str):
        # video_model / video_list 是 **JSON 字符串**，先解再处理
        s = o.strip()
        if s.startswith("{") or s.startswith("["):
            try:
                return json.dumps(scrub_obj(json.loads(s)), ensure_ascii=False)
            except Exception:
                pass
        return o
    return o


def redact(url: str) -> str:
    """只保留 host（CDN 域名是有用的证据），路径与查询串全部打码。

    ★ 路径里的那一长串（tos-cn-v-xxxx/<32位哈希>/…）本身就是签名的
      一部分，属于授权信息 ⇒ 一并抹掉。
    """
    m = re.match(r"(https?://[^/?#]+)(/[^?#]*)?(\?[^#]*)?", url)
    if not m:
        return "<redacted>"
    host = m.group(1)
    return f"{host}/<path:含对象令牌，已打码>?<redacted: auth_key/auth_policy 等授权参数>"


def main():
    inplace = "--inplace" in sys.argv
    for f in sorted(SAMPLES.glob("*.json")):
        raw = f.read_text(encoding="utf-8")
        d = json.loads(raw)
        new = scrub_obj(d)
        out = json.dumps(new, ensure_ascii=False, indent=2)
        if inplace:
            f.write_text(out, encoding="utf-8")
            print("已抹除:", f.name)
        else:
            print("=" * 20, f.name, "=" * 20)
            print(out[:1200])


if __name__ == "__main__":
    main()

# 红果视频下载器

把**你自己红果账号收藏夹**里的剧集，成批下载成通用 MP4。

> 面向收藏里是**冷门、教学类**内容的人——这些内容在聚合站基本找不到，
> 只能从自己账号的收藏夹里一条条捞。工具只处理**你自己账号下有权访问与保存**的内容。

2026-11-21 起把**兴趣岛课程下载器**的下载内核合并了进来：同一个窗口左上角切到
「兴趣岛课程」，粘课程链接（HLS / 直链）就能批量导出 MP4，不需要授权、不用开模拟器。
见[兴趣岛课程](#兴趣岛课程第二个内容源)。

C# + Avalonia 的桌面发行版（单文件 exe），外加一套共用同一核心的 Python 实现（改逻辑、做验证用）。

## 下载即用

从 [Releases](https://github.com/chisenin/hgvd-downloader/releases) 取 `hgvd-downloader-v*.exe`
（自包含单文件，约 81 MB —— .NET 运行时和 ffmpeg/ffprobe 都打在 exe 里，目标机什么都不用装；
首次启动会把 ffmpeg 自解压到 `%LOCALAPPDATA%\hgvd-downloader\runtime\`，之后再开秒起）。

1. 打开程序 → 点「重新授权」。工具会自己找到 MuMu 并读 App 登录态；
   万一定位不到安装位置，会弹框让你选一次 `MuMuManager.exe`。
2. 还没读到登录态时，会弹一个置顶小窗：开哪台安卓机 → 打开红果 → 确认登录，
   每 2 秒自动检测，读到就继续（最多等 180 秒）。
3. 左栏**勾选**想下的剧 → 分集追加进右侧「下载清单」（再勾一部继续追加，不替换）；
   勾错了取消勾选，只把它那一组移出 → 点「下载」。

清单里每部剧自成一组（有「剧名 · 共 N 集」标题行），**集号各归各的**，
多部一起下不会串号。收藏夹进来默认一个都不勾，勾哪些由你定。

## 能下什么

| 类型 | 分集来源 | 状态 |
|---|---|---|
| **教学合集**（`book_type=3`） | `album_detail` → `multi_video_model/preload`（key=1001/1000），标准 HEVC + AAC | ✅ 完全可用 |
| **短剧**（`book_type=2`） | `multi_video_detail/preload` → bytevc2，加密且 SPS/PPS 被抹 | ⚠️ 能下载能解密，ffmpeg 解不出画面 |
| 图书 / 听书 | — | ❌ 服务端没有视频 |

教学合集是本项目的主战场。下载的是**完整正片**（不是试看片段），加密分集用服务端下发的内容密钥解密；
支持断点续传、已下载跳过、成品时长校验、下载历史。

## 兴趣岛课程（第二个内容源）

顶栏左上角切到「兴趣岛课程」。这一侧**不需要授权，也不用开模拟器** —— 只要课程播放链接：

| 链接类型 | 处理方式 |
|---|---|
| 多码率主清单（`EXT-X-STREAM-INF`） | 按**画面短边**选流（这批课多为竖屏，看高度会误选最差一路） |
| 普通 HLS 清单 | 分片并发下载（默认 12 路）→ 按字节拼接 → `ffmpeg -c copy` 转封装 |
| 加密清单（`EXT-X-KEY`） | 找同目录**明文孪生** `v.f<编号>.m3u8`，**必须时长校验**（同目录常有别的视频） |
| 直链 `mp3` / `m4a` / `mp4` | 单请求流式下载，扩展名按 ffprobe 探到的真实容器定 |

左侧输入框一行一条，三种写法都认（`#` 开头是注释，`- ` / `1. ` 前缀会被忽略）：

```
先导课：解谜自然之道 | https://…/xxx.m3u8
五行导引功第一课	https://…/xxx.m3u8          ← 从 Excel 复制过来就是 Tab 分隔
https://…/xxx.m3u8                            ← 只有链接时用文件名当标题
```

- 行尾可以标时长：`[48:12]` 或 `时长=2892`（**有时长才会做成品校验**）
- 「导入清单…」支持 `.txt` / `.m3u8` / `.json`。同时选兴趣岛工具留下的
  `links.json` 与 `names.json`，会按 `fileId` 自动补全「营名 / 课名」
- 双击某一行 = 只下这一节；右键可「重新下载（清缓存）/ 复制链接 / 打开文件夹」
- 失败或中止的**分片会保留**，再点「下载」自动续传；「停止」不会删缓存

产物：`<下载目录>\兴趣岛\<课名>.mp4`（直链音频保持 `.mp3` / `.m4a`）。
清单存 `%USERPROFILE%\.hgv_tool\xqd\jobs.json`，分片缓存在同目录 `parts\`。

> 这一侧只合并了**下载内核**：不做模拟器自动化取课表，课表由你自己粘贴或导入。

## Python 版（改逻辑/做验证用）

```bash
python -m hgvd login                 # 默认从 MuMu 读登录态，免扫码
python -m hgvd login --watch         # 边等边取：开安卓机并登录，检测到就继续
python -m hgvd shelf                 # 收藏夹（自动展开合集，标出集数）
python -m hgvd list <series_id>      # 分集
python -m hgvd download <series_id> [--episodes 1-20] [--quality 720p]
python -m hgvd web                   # 本地网页版 http://127.0.0.1:8765/
```

登录还支持 `--phone`（手机号验证码，可能触发滑块）与 `--qr`（扫码，二维码约 60 秒有效）。
默认走模拟器，是因为你本来就在 App 里收藏剧，App 一定是登录的。

**MuMu 多开**：多台安卓机各自可能登录过红果，工具不按时间瞎猜，而是逐台用
`passport/account/info` 在线验证、取服务端真正认可的那台，设备身份按 `install_id` 归档
（`~/.hgv_tool/devices.json`），保证 cookie 与设备参数同源（`--list-mumu` 可看各台状态）。

## 技术要点

| 环节 | 接口 | 签名 | 登录 |
|---|---|---|---|
| 收藏夹 | `GET /reading/bookapi/bookshelf/video/list/v` | 否 | 是 |
| 剧目 + 全集 / 单集时长 | 网页版 `/detail`、`/player` 的 SSR | 否 | 否 |
| **完整正片播放地址** | `POST /novel/player/multi_video_model/preload/v1` | **否** | 可选 |

网页版 `/player` 里的免签名直链**只有 10 秒**，且只有前 5 集有 SEO 元信息，
所以完整正片必须走 App 网关的 `preload` 接口。

这条路的门槛不是签名，是 **body 形状**：`gzip({"biz_param":{…,"video_platform":1024},
"dr_scene":"preload","mixed_video_id_map":{"<内容类型>":["<vid>"]}})`。
`1004` = 视频流/短剧，`1000/1001` = 教学合集分集；客户端按 `1004 → 1001 → 1000`
依次试，取第一个能解析出 `video_list` 的。

签名链（`hgvd/sign/`、`Hgvd.Core/Sign/`）仍留在仓库里，但**播放请求不再发送任何签名头** ——
实测真机自己的 `X-Argus`/`X-Ladon` 只有 3 字节，宿主机上去掉全部签名头重放仍正常返回。

**「HTTP 200 + 空 body」是症状不是根因**：`code=101000` 是内容类型 key 用错；
`code=100001` 是 body 缺字段；零字节通常是**端点已停用/路径变了**，不是限流
（见 `docs/03-开发要点.md` §1.0 的四次误判记录与可复现探针）。

细节：[`docs/01-接口与请求.md`](docs/01-接口与请求.md)、
[`docs/05-抓包与协议排查.md`](docs/05-抓包与协议排查.md)、
[`docs/03-开发要点.md`](docs/03-开发要点.md)。

## 输出

```
红果：  <下载目录>\<剧名>\<序号>.<集名>.mp4
例：全球死考一季\001.第 1 集.mp4

兴趣岛：<下载目录>\兴趣岛\<课名>.mp4
例：兴趣岛\先导课：解谜自然之道.mp4
```

序号从 1 起、与 App 列表顺序一致；Windows 保留名自动加前缀；下载中的临时文件是
`*.mp4.part`，成功后才改名，半截文件不会被当成成品。

## 已知限制

- **建议登录**：播放接口匿名也能通，但带登录态更接近真实客户端，也更稳。
- 网页版只对前 5 集提供元信息，第 6 集起的时长要靠播放接口的 `video_duration`。
- 默认 `360p`，更高清晰度取决于内容本身授权。
- 短剧能下能解密，但 bytevc2 抹掉 SPS/PPS，ffmpeg 解不出画面。
- **下载链路目前仅 Windows**（读模拟器登录态那条路径是平台专用），界面层跨平台。
- 接口形状会随服务端改版失效：照 `docs/05` 抓一次真机请求，十分钟能定位差异。

## 合规

- 只处理你自己账号下有权访问与保存的内容，不用于未购内容的批量获取。
- 内容密钥按官方播放器同一套规则在本地解包，**不做破解**；下载后内容权利不因本软件改变。
- 请看完及时删除，不要长期留存。第三方项目对照关系见 [`NOTICE.md`](NOTICE.md)。

## 依赖与数据

Python 版要 Python 3.10+、`requests`、`cryptography`（或 `pycryptodome`）、`ffmpeg`/`ffprobe`（成品时长校验）；
构建 Avalonia 版要 .NET 8 SDK，运行时不需要，**ffmpeg/ffprobe 也已内置**（不再检测、不再提示安装）。
配置、历史、凭据都在 `%USERPROFILE%\.hgv_tool\`
（`config.json` / `history.json` / `cookie.json`，权限 0600），`python -m hgvd logout` 清除登录态。
内置的 ffmpeg 落在 `%LOCALAPPDATA%\hgvd-downloader\runtime\ffmpeg-<版本>\`，删掉无害，下次启动自动重建。

## 开发

```bash
cd hgvd-avalonia && ./build.cmd          # 或 build.sh，产物在 dist/
dotnet build hgvd-csharp/src/Hgvd.Tests/Hgvd.Tests.csproj -c Debug && hgvd-csharp/src/Hgvd.Tests/bin/Debug/net8.0-windows/Hgvd.Tests.exe   # 118 项
python -m tests.run                      # 离线
```

```bash
./dist/hgvd-downloader-v*.exe --uitest      # 界面交互自检（需真实登录态）
./dist/hgvd-downloader-v*.exe --selftest    # 收藏夹 → 分集 → 播放地址
./dist/hgvd-downloader-v*.exe --batchtest 2 # 真下 2 部剧
./dist/hgvd-downloader-v*.exe --xqdtest <m3u8 或 mp4 直链> [课名]   # 兴趣岛链路真下一节
```

目录：`hgvd/` Python 实现，`hgvd-csharp/src/Hgvd.Core` 共享核心（签名/下载器/配置），
`hgvd-csharp/src/Hgvd.Xqd` 兴趣岛下载内核（HLS 清单 / 分片 / 合成），
`hgvd-avalonia/` 发行版界面（两个内容源），`docs/` 技术文档，`tools/` 探测脚本。
版本与维护范围见 [`docs/02-版本与维护范围.md`](docs/02-版本与维护范围.md)。

# 红果视频下载器

把**你自己红果账号收藏夹**里的剧集，成批下载成通用 MP4。

> 面向收藏里是**冷门、教学类**内容的人——这些内容在聚合站基本找不到，
> 只能从自己账号的收藏夹里一条条捞。工具只处理**你自己账号下有权访问与保存**的内容。

2026-11-21 起把**兴趣岛课程下载器**合并了进来：同一个窗口左上角切到「兴趣岛课程」，
授权一次就能**自动列出「我的课程」**（正式营 / 体验营 / 公开课），勾选营 → 课表并入
**同一张下载清单**。见[兴趣岛课程](#兴趣岛课程第二个内容源)。

C# + Avalonia 的桌面发行版（单文件 exe），外加一套共用同一核心的 Python 实现（改逻辑、做验证用）。

## 下载即用

从 [Releases](https://github.com/chisenin/hgvd-downloader/releases) 取 `hgvd-downloader-v*.exe`
（自包含单文件，约 81 MB —— .NET 运行时和 ffmpeg/ffprobe 都打在 exe 里，目标机什么都不用装；
首次启动会把 ffmpeg 自解压到 `%LOCALAPPDATA%\hgvd-downloader\runtime\`，之后再开秒起）。

1. 打开程序 → 点「获取授权」。工具会自己找到 MuMu 并读红果 App 的登录态（启动不会自动读）；
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

顶栏左上角切到「兴趣岛课程」。这一侧同样**不会在启动时自动取授权** ——
点「获取授权」，工具从 MuMu 里的**兴趣岛 App** 读一次登录态（读的是 WebView 的 Cookie 库，
不需要 adb / root），之后复用本机缓存。

授权成功后左栏就是「我的课程」：

- 营列表按 正式营 / 体验营 / 公开课 分组显示，可搜索、可全选
- **勾选一个营** → 自动拉它的课表 → 整营的课**追加到右栏那张共用下载清单**（带「营名 · 共 N 节」分组标题）
- 双击清单里某一行 = 只下这一节；「暂停/继续」只作用于兴趣岛任务，分片会保留
- 手动粘贴链接作为兜底：左栏底部「链接…」→ 一行一条（`课名 | https://…`、Tab 分隔、纯 URL 都认）

下载链路与上游一致：

| 环节 | 做法 |
|---|---|
| 课表 | `myCourseListV2`（营）→ `playListOfCourse` / `openCourse/playListOfCourse`（课表） |
| 播放地址 | `getCoursePlayUrl`（每次下载现场解析，签名不缓存） |
| 多码率 | 按**画面短边** `min(w,h)` 选流（这批课多为竖屏） |
| 加密清单 | 找同目录**明文孪生** `v.f<编号>.m3u8`，**必须时长校验**（同目录常有别的视频） |
| 分片 | 12 片等距抽样估总字节 → 并发下载（12 路）→ 按字节拼接 → `ffmpeg -c copy` |
| 直链音频 | 单请求流式下载，扩展名按 ffprobe 探到的真实容器定 |

产物：

```
课表任务：<下载目录>\兴趣岛\<营名>\<序号>.<课名>.<日期>.mp4
手动链接：<下载目录>\兴趣岛\<课名>.mp4
```

清单与分片缓存都在 `%USERPROFILE%\.hgv_tool\xqd\`（`jobs.json` + `parts\`），
重启后自动恢复；链接任务里的 `sign` / `t` 过期会 403，重新取一次链接即可（分片会接着续）。

> 两源共用**同一张下载清单**：左边勾剧 / 勾营，右边同一个表、同一条队列、同一个日志。
> 切源不会打断正在跑的下载，也不会把另一源的行清掉。

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

兴趣岛：<下载目录>\兴趣岛\<营名>\<序号>.<课名>.<日期>.mp4
例：兴趣岛\引气归源太极营\001.先导课：解谜自然之道.2026.04.28.mp4
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
dotnet build hgvd-csharp/src/Hgvd.Tests/Hgvd.Tests.csproj -c Debug && hgvd-csharp/src/Hgvd.Tests/bin/Debug/net8.0-windows/Hgvd.Tests.exe   # 133 项
python -m tests.run                      # 离线
```

```bash
./dist/hgvd-downloader-v*.exe --uitest      # 界面交互自检（需真实登录态）
./dist/hgvd-downloader-v*.exe --selftest    # 收藏夹 → 分集 → 播放地址
./dist/hgvd-downloader-v*.exe --batchtest 2 # 真下 2 部剧
./dist/hgvd-downloader-v*.exe --xqdtest <m3u8 或 mp4 直链> [课名]   # 兴趣岛链路真下一节
./dist/hgvd-downloader-v*.exe --xqdcatalog [营名关键字] [url]        # 课表诊断（列营/课表/解析地址）
```

目录：`hgvd/` Python 实现，`hgvd-csharp/src/Hgvd.Core` 共享核心（签名/下载器/配置），
`hgvd-csharp/src/Hgvd.Xqd` 兴趣岛下载内核（HLS 清单 / 分片 / 合成），
`hgvd-avalonia/` 发行版界面（两个内容源），`docs/` 技术文档，`tools/` 探测脚本。
版本与维护范围见 [`docs/02-版本与维护范围.md`](docs/02-版本与维护范围.md)。

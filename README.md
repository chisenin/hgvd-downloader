# 红果视频下载器

把**你自己红果账号收藏夹**里的剧集，成批下载成通用 MP4。

> 面向收藏里是**冷门、教学类**内容的人——这些内容在聚合站基本找不到，
> 只能从自己账号的收藏夹里一条条捞。工具只处理**你自己账号下有权访问与保存**的内容。

C# + Avalonia 的桌面发行版（单文件 exe），外加一套共用同一核心的 Python 实现（改逻辑、做验证用）。

## 下载即用

从 [Releases](https://github.com/chisenin/hgvd-downloader/releases) 取 `hgvd-downloader-v*.exe`
（自包含单文件，约 46 MB，目标机不用装 .NET）。

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
| **教学合集**（`book_type=3`） | `album_detail` → `video_model(video_id_type=1)`，标准 HEVC + AAC | ✅ 完全可用 |
| **短剧**（`book_type=2`） | `multi_video_detail/preload` → bytevc2，加密且 SPS/PPS 被抹 | ⚠️ 能下载能解密，ffmpeg 解不出画面 |
| 图书 / 听书 | — | ❌ 服务端没有视频 |

教学合集是本项目的主战场。下载的是**完整正片**（不是试看片段），加密分集用服务端下发的内容密钥解密；
支持断点续传、已下载跳过、成品时长校验、下载历史。

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
（`~/.hgv_tool/devices.json`），保证 cookie 与签名同源（`--list-mumu` 可看各台状态）。

## 技术要点

红果接口分两类，**签名要求完全不同**：

| 环节 | 接口 | 签名 | 登录 |
|---|---|---|---|
| 收藏夹 | `GET /reading/bookapi/bookshelf/video/list/v` | 否 | 是 |
| 剧目 + 全集 / 单集时长 | 网页版 `/detail`、`/player` 的 SSR | 否 | 否 |
| **完整正片播放地址** | `POST /novel/player/video_model/v1/` | **是**（6 个签名头） | 是 |

网页版 `/player` 里的免签名直链**只有 10 秒**，且只有前 5 集有 SEO 元信息，
所以完整正片必须走 App 网关的 `video_model`，这条路要完整签名链
（`hgvd/sign/` 与 `hgvd-csharp/src/Hgvd.Core/Sign/`，SM3 / AES-V3 / gorgon / helios / medusa / argus / ladon）。
验证方式是拿开源参考实现的产出与本项目**逐字节比对**；C# 侧另有黄金向量
（`hgvd-csharp/tools/golden_argus.json`）做回归护栏，改签名链任何一行都会失败。

**「HTTP 200 + 空 body」是症状不是根因**：返回 JSON（哪怕 `code≠0`）说明签名是通的；
`code=101000` 是 `video_id_type` 用错；零字节才算服务端丢弃（限流/网关节点故障）。
客户端遇到空 body 立刻换网关（秒级，不退避），连续 2 次才熔断并提示改用备用路径。

细节：[`docs/01-接口与签名.md`](docs/01-接口与签名.md)、[`docs/03-开发要点.md`](docs/03-开发要点.md)。

## 输出

```
<下载目录>\<剧名>\<序号>.<集名>.mp4
例：全球死考一季\001.第 1 集.mp4
```

序号从 1 起、与 App 列表顺序一致；Windows 保留名自动加前缀；下载中的临时文件是
`*.mp4.part`，成功后才改名，半截文件不会被当成成品。

## 已知限制

- **必须登录**：匿名调 `video_model` 返回 200 + 空 body。
- 网页版只对前 5 集提供元信息，第 6 集起的时长要靠 `video_model`。
- 默认 `360p`，更高清晰度取决于内容本身授权。
- 短剧能下能解密，但 bytevc2 抹掉 SPS/PPS，ffmpeg 解不出画面。
- **下载链路目前仅 Windows**（读模拟器登录态那条路径是平台专用），界面层跨平台。
- 签名链会随服务端改版失效，按 `docs/01` 的方法重新比对即可。

## 合规

- 只处理你自己账号下有权访问与保存的内容，不用于未购内容的批量获取。
- 内容密钥按官方播放器同一套规则在本地解包，**不做破解**；下载后内容权利不因本软件改变。
- 请看完及时删除，不要长期留存。第三方项目对照关系见 [`NOTICE.md`](NOTICE.md)。

## 依赖与数据

Python 版要 Python 3.10+、`requests`、`cryptography`（或 `pycryptodome`）、`ffmpeg`/`ffprobe`（成品时长校验）；
构建 Avalonia 版要 .NET 8 SDK，运行时不需要。配置、历史、凭据都在 `%USERPROFILE%\.hgv_tool\`
（`config.json` / `history.json` / `cookie.json`，权限 0600），`python -m hgvd logout` 清除登录态。

## 开发

```bash
cd hgvd-avalonia && ./build.cmd          # 或 build.sh，产物在 dist/
dotnet build hgvd-csharp/src/Hgvd.Tests/Hgvd.Tests.csproj -c Debug && hgvd-csharp/src/Hgvd.Tests/bin/Debug/net8.0-windows/Hgvd.Tests.exe   # 72 项
python -m tests.run                      # 87 项（离线）
```

```bash
./dist/hgvd-downloader-v*.exe --uitest      # 界面交互自检（需真实登录态）
./dist/hgvd-downloader-v*.exe --selftest    # 收藏夹 → 分集 → 播放地址
./dist/hgvd-downloader-v*.exe --batchtest 2 # 真下 2 部剧
```

目录：`hgvd/` Python 实现，`hgvd-csharp/src/Hgvd.Core` 共享核心（签名/下载器/配置），
`hgvd-avalonia/` 发行版界面，`docs/` 技术文档，`tools/` 探测脚本。
版本与维护范围见 [`docs/02-版本与维护范围.md`](docs/02-版本与维护范围.md)。

# 红果视频下载器

从**你的红果账号收藏夹**里挑剧目，批量下载成通用 MP4。

> 目标用户：收藏里是**冷门、教学类**内容的人 —— 这些剧在聚合站基本找不到，
> 只能靠自己账号里的收藏夹一条条捞。
>
> 本工具只处理**你自己账号下有权访问与保存**的内容。

---

## 两套实现

| 角色 | 实现 | 位置 | 形态 |
|---|---|---|---|
| **发行版**（日常使用） | **C# + Avalonia 11** | [`hgvd-avalonia/`](hgvd-avalonia/) | 自包含单文件 exe（约 46 MB，目标机不用装 .NET） |
| **想法验证**（改逻辑用） | **Python** | [`hgvd/`](hgvd/) | 命令行 + 本地网页界面，依赖极少 |

两套实现**共用同一套核心**：签名链、下载器、配置目录、设备身份
（C# 侧在 `hgvd-csharp/src/Hgvd.Core`），登录态 / 设备档案 / 下载历史可以直接互换。

维护范围与发版流程见 [`docs/02-版本与维护范围.md`](docs/02-版本与维护范围.md)。

---

## 它做什么

- 登录你的红果/抖音账号，读到**云端收藏夹**（不是本地 App 数据库）。
- 列出收藏夹里的剧 → 展开每部的**全部分集** → 你勾选要下的 → 批量下载。
- 下载的是**完整正片**，不是试看片段；加密分集用服务端下发的内容密钥解密。
- 断点续传、已下载自动跳过、成品时长校验、下载历史。

---

## 接口链路（★ 技术核心）

红果的接口分两类，**签名要求完全不同**。

| 环节 | 接口 | 需要签名？ | 需要登录？ |
|---|---|---|---|
| **收藏夹** | `GET /reading/bookapi/bookshelf/video/list/v` | **否** | 是（否则返回空） |
| **剧目 + 全集列表** | 网页版 `/detail?series_id=<id>` 的 SSR | **否** | 否 |
| **单集时长/分辨率** | 网页版 `/player/<sid>/<vid>` 的 SSR | **否** | 否 |
| **完整正片播放地址** | `POST /novel/player/video_model/v1/` | **是**（全套 6 个签名头） | 是 |

### 为什么网页版不够用

网页版 `/detail` 页内嵌 `_ROUTER_DATA`，能免签名拿到**全部分集 ID** 和单集时长；
播放页里也直接写着免签名直链
`https://hongguoduanju.com/video/public/<vid>/360p.mp4` —— **能下载，但只有 10 秒**。
且 `accessible_episode_cnt` = 5，即只有前 5 集有 SEO 元信息。

所以：**完整正片必须走 App 网关的 `video_model`**，这条路要完整签名链。

### 签名链

签名链是本项目最核心的部分，两套实现里都有：

| 语言 | 位置 | 文件 |
|---|---|---|
| Python | `hgvd/sign/` | `sm3.py`（SM3，GB/T 32905-2016）、`aesv3.py`（AES-V3 变体）、`aes_cbc.py`（标准 AES-128-CBC）、`core.py`（gorgon / helios / medusa / argus / ladon 主流程） |
| C# | `hgvd-csharp/src/Hgvd.Core/Sign/` | `Sm3.cs` / `AesV3.cs` / `AesCbc.cs` / `Gorgon.cs` / `Medusa.cs` / `Argus.cs` / `F13.cs` / `Signer.cs` |

**验证方式**：用开源参考实现（`beilunyang/openhongguo` 的 WASM 包）拦截真实请求，
拿它产出的 `x-gorgon` / `x-helios` / `x-ss-stub` 与本项目实现**逐字节比对**，
请求 body 也逐字节一致（是 JSON，尽管 `Content-Type` 写着 protobuf）。

C# 侧另有**字节级黄金向量**（`hgvd-csharp/tools/golden_argus.json`）做护栏：
改动签名链任何一行，回归立刻失败。

### 「空 body」的判据

播放接口的「HTTP 200 + 空 body」是**症状**，不是根因 —— 至少有四种成因：

| 现象 | 含义 |
|---|---|
| 返回 **JSON**（哪怕 `code≠0`） | 签名是通的，问题在业务参数 |
| `code=101000` | **`video_id_type` 用错**（教学类用 1、短剧用 0） |
| 返回 **零字节 body** | 服务端把请求丢弃了（网关节点故障 / 限流） |

★ 所以**不要**把空 body 笼统写成「服务端限流」。
客户端内置**网关故障转移**：遇到空 body 立刻换一个播放网关（秒级，不退避），
单个网关抽风不影响下载。

详见 [`docs/01-接口与签名.md`](docs/01-接口与签名.md)。

---

## 登录

三种方式，按可用性排序：

```bash
python -m hgvd login              # 读 MuMu 模拟器里红果 App 的登录态（默认，免扫码）
python -m hgvd login --watch      # 边等边取：先开安卓机/登录红果，检测到就自动继续
python -m hgvd login --list-mumu  # 列出所有模拟器实例（含「✓有效 / ⚠无法验证 / ✗失效」）
python -m hgvd login --phone      # 手机号验证码（可能触发滑块）
python -m hgvd login --qr         # 扫码（二维码有效期约 60 秒）
```

默认走模拟器，是因为网页版二维码**有效期只有约 60 秒**，
而手机号登录又会触发滑块验证 —— 而你本来就在 App 里收藏剧，App 一定是登录的。

**工具会自动找 MuMu 装在哪**（进程 → 开始菜单快捷方式 → 卸载注册表 → 各盘目录 → PATH），
不需要你设环境变量；GUI 万一找不到，会直接弹文件选择框让你选一次 `MuMuManager.exe`。

**MuMu 多开 / 多台安卓机**：每台都可能装了红果并各自登录过，工具不会按时间瞎猜 ——
它会逐台用 `passport/account/info` 在线验证，挑**服务端真正认可的那台**，
并把设备身份按 `install_id` 归档（`~/.hgv_tool/devices.json`），保证 cookie 与签名同源。
所谓「同一账号在多台里授权互斥」，实测更常见的其实是
**多开复制镜像导致设备身份串台**（session 相同、`install_id`/`device_id` 不同）或
**后登录的那台把旧 session 顶掉**，不是账号本身互斥。

**GUI**：点「重新授权」若还没读到登录态，会弹一个置顶小窗，告诉你开哪台安卓机、
打开红果确认登录，并每 2 秒自动检测，读到就自动继续（最多等 180 秒）。

读的是这些文件（值都是**明文**）：

| 文件 | 内容 |
|---|---|
| `shared_prefs/cookieStore.xml` | `sessionid` / `sid_tt` / `install_id` / `odin_tt` |
| `shared_prefs/token_shared_preference.xml` | `X-Tt-Token` |
| `shared_prefs/applog_stats.xml` | `device_id` / `install_id` |
| `shared_prefs/CsrfTokenManager_sp.xml` | `csrf_token`（明文，放在 `passport_csrf_token` cookie 里） |

---

## 快速开始（Python）

```bash
# 1. 登录
python -m hgvd login

# 2. 看收藏夹（★ 会自动展开「合集」，标出每部有多少集）
python -m hgvd shelf

# 3. 看某部剧的分集（★ 合集与短剧都支持，自动识别）
python -m hgvd list <series_id>

# 4. 下载
python -m hgvd download <series_id>                    # 全部集
python -m hgvd download <series_id> --episodes 1-20    # 只要前 20 集
python -m hgvd download <series_id> --quality 720p     # 指定清晰度
python -m hgvd download --all                          # 收藏夹里全部剧

# 5. 历史
python -m hgvd history

# 6. 备用路径：把 App 播放过的视频从模拟器缓存里提取出来（完全不需要签名）
python -m hgvd cache --list
python -m hgvd cache --out ./videos
```

---

## ★ 三类内容，三条路径

收藏夹里的内容不是一种东西，工具会**自动分流**：

| 类型 | `book_type` | 分集来源 | 播放地址 | 编码 | 状态 |
|---|---|---|---|---|---|
| **教学合集** | 3 | `album_detail` | `video_model`（`video_id_type=1`） | **标准 HEVC + AAC，不加密** | ✅ **完全可用** |
| **短剧** | 2 | `multi_video_detail/preload` | `video_model`（`video_id_type=0`） | bytevc2，**加密 + SPS/PPS 被抹** | ⚠️ 能下载能解密，**但 ffmpeg 解不出画面** |
| 图书 / 听书 | 3（部分） | — | — | — | ❌ 服务端确实没有视频 |

★ 教学合集是本项目的主战场 —— 那正是「聚合站没有的冷门教学内容」。
★ `codec_type=bytevc1` 只是个**元数据标签**，实际文件就是标准 HEVC。

---

## 图形界面

### C# + Avalonia 11（发行版）

```bash
cd hgvd-avalonia
./build.sh            # 或 Windows 下 build.cmd
./dist/hgvd-downloader-v1.1.1.exe
```

**交互**：左栏收藏夹（勾选框 + 剧名 + 类型/集数）→ **勾选**想下的剧
→ 右栏「下载清单」里**依次追加**它的分集（再勾一部就再追加一组，不替换前面的）
→ 点「下载」下清单里所有待下载的（选中某几行则只下那几行）。

- 收藏夹进来**默认一个都不勾**：几十上百项时一次全下不现实，勾哪些由你定。
- 点列表行**不会**再加载分集（列表行只做展示），加不加只看勾选框。
- 每部剧在清单里自成一组（有「剧名 · 共 N 集」的标题行），
  **集号各归各的**，多部剧一起下也不会串号，文件名按各自的剧名 + 集号。
- 取消勾选 = 把这部剧整组从清单里移出，其它剧不受影响。

**批量下载**（`--batchtest` 用的内部流程）：按剧依次取分集 → 逐集下载，
不经过右栏清单，所以不会搅乱你已经勾好的东西。

**限流保护**：播放接口被服务端**丢弃**时返回「HTTP 200 + 空 body」。
此时连续 2 次空 body 就**熔断**，整批立刻停下并提示改用「提取 App 缓存」
—— 不会在退避里空转几十分钟。
★ 错误提示会给出**真实原因**（区分限流 / 参数错 / 网关故障），不笼统写成「风控」。

**自检**：

```bash
./dist/hgvd-downloader-v1.1.1.exe --selftest    # 收藏夹 → 分集 → 播放地址
./dist/hgvd-downloader-v1.1.1.exe --uitest      # ★ 界面交互：勾选追加/取消移出/多剧不串号/下载
./dist/hgvd-downloader-v1.1.1.exe --batchtest 2 # ★ 真下 2 部剧
```

> `--uitest` 在窗口 `Opacity=0` 下**走真实的事件绑定**跑一遍交互。

详见 [`hgvd-avalonia/README.md`](hgvd-avalonia/README.md)。

### Python 本地网页版

```bash
python -m hgvd.web            # 默认 http://127.0.0.1:8765/，会自动开浏览器
python -m hgvd.web --port 9000
```

左栏勾剧 → 右栏勾集 → 底部任务表实时显示状态/大小/速度/进度（**渐变进度条 + 居中百分比**）。

> ★ 为什么用网页而不是 Tkinter / PySide6：网页界面**零依赖**，
> 打包产物小得多，也不用操心 GUI 框架的版本兼容。

---

## 输出结构

```
<下载目录>\<剧名>\<序号>.<集名>.mp4
例：全球死考一季\001.第 1 集.mp4
```

- 序号从 1 起，与 App 列表顺序一致。
- 文件名过 Windows 保留名（`CON`/`NUL`…）时自动加前缀。
- 下载中的临时文件是 `*.mp4.part`，成功后才改名 —— 半截文件不会被当成成品。

---

## 依赖

| 组件 | 用途 | 备注 |
|---|---|---|
| Python 3.10+ | Python 版运行时 | |
| `requests` | HTTP | |
| `cryptography` 或 `pycryptodome` | Argus 的 AES-CBC | 都没有时会退到纯 Python 实现（慢，但够用） |
| ffmpeg / ffprobe | 成品时长校验 | 需要在 PATH 里 |
| .NET 8 SDK | 构建 Avalonia 发行版 | 运行时不需要，目标机不用装 .NET |

**不需要** Go / Rust / PySide6 / 模拟器（模拟器只影响「免扫码登录」这一条路径）。

---

## 目录结构

```
hgvd/                    Python 实现（想法验证）
  api/web.py             网页版 SSR：详情 / 分集 / 时长 / 免签名直链
  api/shelf.py           收藏夹（免签名）
  api/album.py           ★ 合集详情（book_type=3 的分集唯一来源）
  api/detail.py          签名 POST 封装（指数退避）+ 批量详情 + 合集批量
  api/playback.py        App 网关 video_model（播放专用签名）
  auth/login.py          扫码登录 → 明文 cookie
  auth/phone.py          手机号验证码登录
  auth/mumu.py           ★ 从 MuMu 读 App 登录态（免扫码，默认路径）
  auth/cache_extract.py  ★ 提取 App 视频缓存（绕过服务端）
  sign/                  签名链（SM3 / AES / gorgon / helios / medusa / argus / ladon）
  drm.py                 CENC 内容密钥解包
  device.py              设备身份（签名必需，稳定持久化）
  engine/                下载器 + 历史
  web/                   本地网页版前端（server.py + ui.html）
  service.py             业务编排（前端只调这一层）
  cli.py                 命令行
hgvd-avalonia/           C# + Avalonia 11（发行版，自包含单文件）
hgvd-csharp/
  src/Hgvd.Core/         ★ 共享核心（签名链 / 下载器 / 配置目录）
  src/Hgvd.Tests/        离线回归 72 项
tests/run.py             Python 离线回归 87 项（不发网络请求）
tools/                   探测与自检脚本
docs/                    技术文档
```

---

## 工具

```bash
python tools/selftest.py     # 逐环自检：网页版 / 收藏夹 / video_model
python tools/login_watch.py  # 扫码助手（二维码过期自动换新重弹）
```

---

## 状态与数据位置

| 内容 | 位置 |
|---|---|
| 配置 | `%USERPROFILE%\.hgv_tool\config.json` |
| 下载历史 | `%USERPROFILE%\.hgv_tool\history.json` |
| 登录凭据 | `%USERPROFILE%\.hgv_tool\cookie.json`（权限 0600） |

`python -m hgvd logout` 清除登录态。

---

## 已知限制

- **必须登录才能下载**。匿名调 `video_model` 会返回 HTTP 200 + 空 body。
- 收藏夹接口只读，不会往你账号里写任何东西。
- 网页版只对**前 5 集**提供 SEO 元信息；第 6 集起的时长要靠 `video_model` 返回。
- 清晰度默认 `360p`。更高清晰度是否可用取决于内容本身的授权。
- **短剧**能下载能解密，但 bytevc2 抹掉了 SPS/PPS，ffmpeg 解不出画面。
- **核心层仍是 Windows 专用**（读模拟器登录态那条路径），Avalonia 的界面层虽然跨平台，
  下载链路目前只能在 Windows 上跑。
- 签名链跟着服务端改版会失效。验证与比对方法见
  [`docs/01-接口与签名.md`](docs/01-接口与签名.md)，服务端改版后按那里的方法重新比对即可。

---

## 合规与使用边界

- 本工具**只处理你自己账号下有权访问与保存**的内容。
- 不提供也不鼓励用于未购内容的批量获取。
- 内容密钥是服务端下发的、按官方播放器同一套规则在本地解包，
  **不做破解**；下载后的内容权利不因本软件改变。
- 请看完及时删除，不要长期留存。

签名算法与第三方项目的对照关系见 [`NOTICE.md`](NOTICE.md)。

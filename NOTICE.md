# NOTICE

## 本仓库运行时主链路

全部为**独立编写**（Python 实现 + C# 独立实现，两套共用同一套协议理解），
不包含任何账号体系、加密服务或商业客户端代码，
也**不包含任何账号凭据**（凭据只存在用户本机 `%USERPROFILE%\.hgv_tool\cookie.json`）。

## 签名算法的来源

`hgvd/sign/` 下的签名实现（SM3 / AES-V3 / AES-CBC / gorgon / helios / medusa /
argus / ladon）是**按公开的线上协议独立复现**的等价实现，
其对照参考是 Apache-2.0 授权的开源项目 `beilunyang/openhongguo`：

- 项目：https://github.com/beilunyang/openhongguo
- 许可：Apache License 2.0
- 用到的部分：`internal/protocol/sign/`、`internal/drm/`

Apache-2.0 允许在保留声明的前提下使用与修改。本实现为独立的 Python 等价实现，
未直接复制其源码文件；算法本身是协议规格，不受版权保护。

## 调研参考材料

`_ref/` 目录（已在 .gitignore 中排除）存放了调研过程中下载的第三方项目源码片段，
仅供**分析与对照**，不在运行时链路中。其中包含：

- `aston314/hongguo-tv`（Go）与 `N3urda/hongguoTV`（Kotlin）的收藏夹/扫码登录实现
  —— GPL-3.0 / MIT 等，**不得**作为本项目源码直接分发
- `fysh1010/mcp-server-fanqie` 的番茄书架对照实现

如需在仓库中保留这些材料，必须单独提交、逐文件标注出处与原始许可。

## 内置的第三方二进制（ffmpeg）

发行版单文件 exe 里**内置了 ffmpeg / ffprobe**（用于成品时长校验与加密分集解密），
不是本项目源码的一部分，各自保留原作者的版权与许可：

| 组件 | 来源 | 许可 |
|---|---|---|
| `ffmpeg.exe` | 经 npm 包 `@ffmpeg-installer/win32-x64@4.1.0` 分发（构建时为 `N-92722-gf22fcd4483`） | ffmpeg 项目按其编译选项提供；本包为 GPL 变体，随附 COPYING（GPL-2.0-or-later） |
| `ffprobe.exe` | 经 npm 包 `@ffprobe-installer/win32-x64@4.1.0` 分发（构建时为 `N-89672-g41e51fbcd9`） | 同上 |

- 上游项目：https://ffmpeg.org/
- 许可证全文：https://ffmpeg.org/legal.html （GPL/LGPL 全文见 http://www.gnu.org/licenses/ ）
- 内置形态：构建时压缩为 `ffmpeg.exe.gz` / `ffprobe.exe.gz` 嵌入 exe，
  首次启动解压到用户本机 `%LOCALAPPDATA%\hgvd-downloader\runtime\ffmpeg-<版本>\`，
  以**独立可执行文件**形式被调用（未修改、未静态链接、未与本项目代码混编）。
- 本分发包按 GPL 要求提供对应源码获取方式：ffmpeg 源码可从上面的上游地址取得；
  构建配置参数见 `hgvd-avalonia/prepare-ffmpeg.ps1`（固定 `4.1.0` 版预编译包，未改动编译选项）。

## 内容权利

下载内容的权利不因本软件改变。请**只处理自己已购买、有权访问与保存的内容**，
仅供个人学习与本地教学使用，看完及时删除。

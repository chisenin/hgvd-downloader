# NOTICE · 版权、许可与第三方声明

> 适用范围：本仓库源码、`hgvd-downloader` 发行物（目录版 zip / 单文件 exe）及其内置资源。
> 最后更新：2026-11-21（兴趣岛内核合并后）。

---

## 一、本项目

**© 2026 chisenin。按 [PolyForm Noncommercial License 1.0.0](LICENSE) 授权。**

一句话：**源码公开，允许非商业地使用、修改与再分发；不允许商用。**

| 允许 | 不允许 |
|---|---|
| 个人学习、研究、实验、教学（学校 / 公益 / 公共研究机构） | 任何商业目的（含内部业务、外包交付、收费服务、随商品分发） |
| 阅读、修改源码，做自己的衍生版本 | 把本工具或其衍生版拿去卖、租、提供付费下载/代下服务 |
| 再分发（须随附本许可与 `Required Notice`） | 移除版权声明、许可文本或 `Required Notice` 后分发 |

> ★ 严格说，它**不是 OSI 定义的开源许可** —— OSI 不允许限制商业用途，
> 所以本仓库属于「**源码可用（source-available）**」。选择这条许可是刻意的：
> 把商用排除在外，只留给研究、教学与个人使用。需要商用请先联系作者单独授权。

```
Required Notice: Copyright 2026 chisenin (https://github.com/chisenin/hgvd-downloader)
```

运行主链路为**独立编写**：

- 不包含任何第三方账号体系、加密服务或商业客户端代码；
- **不包含任何账号凭据**（凭据只存在使用者本机 `%USERPROFILE%\.hgv_tool\`）；
- 不包含任何破解工具或绕过付费墙的实现。

---

## 二、代码来源

| 模块 | 位置 | 来源与许可 |
|---|---|---|
| 红果核心 | `hgvd-csharp/src/Hgvd.Core/` | 作者独立实现（C#）；`hgvd/` 为同一作者的 Python 实现 |
| 兴趣岛内核 | `hgvd-csharp/src/Hgvd.Xqd/` | 作者另一项目 **`xqd-downloader` v1.7.1（MIT）** 的内核并入；同一版权人，不涉及第三方授权变更 |
| 界面 | `hgvd-avalonia/` | 作者独立实现，使用 Avalonia 11（MIT） |
| 离线回归 | `hgvd-csharp/src/Hgvd.Tests/` | 作者独立实现 |

兴趣岛合并时**没有搬**上游的 ADB 界面自动化、模拟器自动启动、ffmpeg 自动安装，
本仓库改为「API + MuMu 只读 Cookie + 内置 ffmpeg」。

---

## 三、协议实现的参考（不在运行链路）

红果的签名链（`hgvd/sign/`、`Hgvd.Core/Sign/`）是**按公开的线上协议独立复现**的等价实现，
对照参考为 Apache-2.0 授权的开源项目：

- 项目：<https://github.com/beilunyang/openhongguo>
- 许可：Apache License 2.0
- 参考部分：`internal/protocol/sign/`、`internal/drm/`

Apache-2.0 允许在保留声明的前提下使用与修改。本实现**未直接复制其源码文件**；
算法与协议格式本身是规格，不是受版权保护的表达。

`_ref/` 目录（已在 `.gitignore` 中排除）存放调研期下载的第三方片段，**仅供分析与对照**，
不参与构建、不进入发行物。其中包含：

- `aston314/hongguo-tv`（Go）、`N3urda/hongguoTV`（Kotlin）的收藏夹 / 扫码登录实现
  —— GPL-3.0 / MIT 等，**不得**作为本项目源码直接分发；
- `fysh1010/mcp-server-fanqie` 的番茄书架对照实现。

如需在仓库中保留这些材料，必须单独提交、逐文件标注出处与原始许可。

---

## 四、内置的第三方二进制（ffmpeg / ffprobe）

发行物里**内置了 ffmpeg / ffprobe**，用于分片转封装、CENC 解密与成品时长校验。
它们**不是本项目源码的一部分**，各自保留原作者的版权与许可：

| 组件 | 来源 | 许可 |
|---|---|---|
| `ffmpeg.exe` | npm 包 `@ffmpeg-installer/win32-x64@4.1.0`（构建时为 `N-92722-gf22fcd4483`） | ffmpeg 项目按其编译选项提供；本包为 **GPL 变体**，随附 COPYING（GPL-2.0-or-later） |
| `ffprobe.exe` | npm 包 `@ffprobe-installer/win32-x64@4.1.0`（构建时为 `N-89672-g41e51fbcd9`） | 同上 |

- 上游项目：<https://ffmpeg.org/>
- 许可说明：<https://ffmpeg.org/legal.html>（GPL / LGPL 全文见 <http://www.gnu.org/licenses/>）
- 内置形态：构建时压缩为 `ffmpeg.exe.gz` / `ffprobe.exe.gz` 作为 `EmbeddedResource` 嵌入主程序集，
  首次启动解压到 `%LOCALAPPDATA%\hgvd-downloader\runtime\ffmpeg-<版本>\`，
  以**独立可执行文件**形式调用（未修改、未静态链接、未与本项目代码混编）。
- GPL 合规：对应源码可从上面的上游地址取得；构建固定使用 `4.1.0` 预编译包，
  **未改动编译选项**，脚本见 `hgvd-avalonia/prepare-ffmpeg.ps1`。

## 五、随产物分发的托管运行时与界面框架

| 组件 | 许可 | 形态 |
|---|---|---|
| Avalonia 11（含 Fluent 主题） | MIT | 编译进产物 |
| .NET 8 运行时 | MIT | 自包含发布时打进产物 |

这两项的版权归 .NET Foundation / Avalonia 项目及其贡献者；许可全文随对应 NuGet 包分发，
也可从各自项目主页取得。

> ★ PolyForm Noncommercial **只覆盖本项目自己的代码**。第三方组件仍按各自许可：
> 本项目的「非商用」限制不改变、也不替代 GPL / MIT 的义务；再分发时两者都要遵守。

---

## 六、内容权利与使用边界

- **下载内容的权利不因本软件改变**：版权仍归原作者与平台所有。
- 只处理使用者**自己已购买、已开通、有权访问与保存**的内容
  （已购课程、体验营、公开课、自己账号的收藏夹）。
- **下载得到的内容**仅供个人学习、备份与离线观看；不得传播、转售、公开发布或用于商业用途
  （软件本身的非商业再分发见 §一，两者不是一回事）。
- 不做 DRM 破解：加密内容使用服务端下发给本机播放器的密钥，按官方播放器同一套规则解包。
- 请看完及时删除。因使用本软件产生的账号风险、版权纠纷或其他后果，由使用者自行承担。

---

## 七、商标与平台名称

文中出现的「红果」「兴趣岛」「MuMu」等名称与标识，版权及商标权归各自权利人所有。
提及这些名称**仅为说明互操作对象**，不表示与任何平台存在合作、授权或背书关系。

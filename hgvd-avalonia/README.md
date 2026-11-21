# 红果视频下载器 —— C# / Avalonia 界面版（发行版）

用 **C# + Avalonia 11** 写的界面层。**一个窗口装两个内容源**：红果收藏夹、兴趣岛课程，
右栏共用一张下载清单、一条队列、一个日志。

发行形态是**自包含目录版 + zip（主推）**，另附单文件 exe：

| 形态 | 产物 | 体积 |
|---|---|---|
| **目录版（推荐）** | `hgvd-downloader-v1.4.0-win-x64.zip` | 约 54 MB |
| 单文件（备用） | `hgvd-downloader-v1.4.0.exe` | 约 58 MB |

两种都是自包含的：.NET 运行时与 ffmpeg/ffprobe 全都打包在内，目标机什么都不用装，
首次启动把 ffmpeg 解压到 `%LOCALAPPDATA%\hgvd-downloader\runtime\`。

> ★ 为什么主推 zip 而不是单文件：单文件把 ffmpeg 嵌进去之后是"假单文件"——
> 运行时仍要解到 `%LOCALAPPDATA%`（磁盘反而翻倍），而且"exe 解出 exe"
> 是杀软启发式的典型特征。目录版启动更快、ffmpeg 可替换、出问题能直接看目录。

> 体积是 `PublishTrimmed`（`TrimMode=partial`）裁出来的：不裁时体积约翻倍。
> 当前实测：目录版 77 MB / zip 54 MB / 单文件 58 MB（都含内置 ffmpeg）。
> 裁剪有两条必须守住的约束（Avalonia 反射绑定要整程序集保根、`System.Text.Json`
> 反射序列化要显式打开），改了发布配置先看
> [`../docs/03-开发要点.md`](../docs/03-开发要点.md) §3.6。

> 与 Python 版（[`../hgvd/`](../hgvd/)）**共用同一套协议理解**；
> 红果核心在 `../hgvd-csharp/src/Hgvd.Core`（签名链、下载器、配置目录），
> 兴趣岛内核在 `../hgvd-csharp/src/Hgvd.Xqd`（课表发现、HLS、分片、合成）。
> 登录态 / 设备档案 / 下载历史可以直接互换；Python 版负责快速验证，本版负责日常使用。

![界面截图](docs/界面截图.png)

---

## 一、两个内容源

| | 红果收藏夹 | 兴趣岛课程 |
|---|---|---|
| 左栏 | 收藏夹（勾选剧目） | 我的课程（正式营 / 体验营 / 公开课） |
| 授权 | MuMu 里红果 App 的登录态 | MuMu 里兴趣岛 App 的 WebView Cookie |
| 条目进清单 | 勾剧 → 全部分集 | 勾营 → 整营课表 |
| 兜底 | 提取 App 缓存 | 手动粘贴课程链接 |
| 专属按钮 | 提取App缓存 | 暂停 / 全选 / 链接… |

两源**共用**右栏清单、下载队列、日志、下载目录与底栏统计。
切源只换左栏，**不打断**正在跑的下载，也不清另一源的行。

清单本身的能力（两源一致）：

- **5 档视图**：全部 / 待下载 / 下载中 / 已完成 / 失败
- **每行标明来源**（红果 / 兴趣岛）与**剩余时间**（按剩余字节 ÷ 当前速度）
- **右键**：下载这一条 / 重新下载（清缓存）/ 复制链接 / 复制标题 / 打开文件夹 / 移出清单；
  `Delete` 键同效
- **历史**：两源共用的 `history.json`（最近 500 条，同一成品只留一条）
- **重启恢复**：红果写 `queue.json`，兴趣岛写 `xqd\jobs.json`，两边都会恢复上次的清单

---

## 二、构建

```bat
build.cmd                :: 发布 Release 目录版（输出到 pkg\）
build.cmd verify         :: ★ 发版用：构建 + 界面自检 + 端到端真下载
build.cmd selftest       :: 无头跑一遍收藏夹 / 分集 / 播放地址
```

Git Bash / MSYS 里用等价脚本：

```bash
./build.sh
./build.sh verify
./build.sh selftest
```

> ★ **本地只出目录版**（2026-11-21 起）：产物在 `pkg\hgvd-downloader-vX.Y.Z\`，
>   直接双击里面的 exe 即可。**本地不再打 zip、不再出单文件 exe**。
>   GitHub Release 上的 zip + 单文件 exe 照旧提供（CI 构建，与本地无关）。

> ★★ **发版前必须跑 `verify`。** 发行版开了 IL 裁剪（`PublishTrimmed`），
>   裁剪的破坏是静默的 —— 编译、启动、开窗、读收藏夹全都正常，
>   只有点到某个功能才炸。所以"能构建"不算通过，必须界面自检全绿
>   **并且**真下一集。`tools/publish_release.sh` 会检查这两份日志是否
>   在 24 小时内且通过，不过就拒绝打标签。
>   细节见 [`../docs/03-开发要点.md`](../docs/03-开发要点.md) §3.6 与 §6.1。

**依赖**：.NET 8 SDK + NuGet 源（`NuGet.config` 已配好镜像）+ 联网。

> ★ 构建第一步跑 `prepare-ffmpeg.ps1`，把 ffmpeg / ffprobe 从 npmmirror 拉下来、
> 解出 exe、压成 `..\.cache\ffmpeg\*.exe.gz`，再作为 `EmbeddedResource` 打进 exe；
> 资源已在（`.cache` 里有）就秒过，不用重复下载。`-Force` 强制重来。
> `.cache/` 不进版本库（见 `.gitignore`），CI 上由同一步骤现拉。

> ★ `NuGet.config` 放在本目录，但被引用的 `Hgvd.Core` / `Hgvd.Xqd` 在 `../hgvd-csharp/` 下 ——
>   NuGet 配置是**按项目目录向上查找**的，所以发布前必须显式
>   `dotnet restore --configfile NuGet.config -r win-x64`（脚本已经带上了）。
>   少了这一步会报 `NU1100：无法解析 Microsoft.NETCore.App.Runtime.win-x64`。

### 成品命名与版本号

产物固定叫 **`hgvd-downloader-vX.Y.Z.exe`** / `…-win-x64.zip`。
版本号唯一来源 = `build.cmd` / `build.sh` 里的 `VER`
（等价于 csproj 的 `PkgVersion`，同时决定**程序集版本**，
界面右下角与「关于」里的版本号读的就是它）。

在 CI 里由**标签**决定：`git tag v1.4.1` ⇒ 产物 `hgvd-downloader-v1.4.1.exe`，
界面显示 `v1.4.1`。工作流会核对 `FileVersion` 与标签，不一致直接失败。

> ★ **升版本只走一条命令**：`python tools/bump_version.py 1.4.1`
> （同时改 `build.cmd` / `build.sh` / csproj，并保留 build.cmd 的 CRLF）。
> `publish_release.sh` 与 `release.yml` 都会用 `tools/check_version.py` 核对
> 标签与仓库版本，**不同步就拒绝发布**。
> 历史：`v1.3.0` 指向合并前的提交（只有红果），所以兴趣岛合并版是 `v1.4.0`。

---

## 三、命令行自检

```bash
hgvd-downloader-v1.4.0.exe --selftest [series_id]    # 收藏夹 → 分集 → 播放地址
hgvd-downloader-v1.4.0.exe --uitest                  # 界面交互自检
hgvd-downloader-v1.4.0.exe --batchtest [部数]         # 批量下载冒烟
hgvd-downloader-v1.4.0.exe --demo <series_id> [skip]  # 加载一部剧并真开始下载（截图用）
hgvd-downloader-v1.4.0.exe --xqdcatalog [营名] [url]  # 兴趣岛课表诊断（只读）
hgvd-downloader-v1.4.0.exe --xqdtest <直链> [课名]    # 兴趣岛链路真下一节
```

> ★ 程序是 `WinExe`（没有控制台），启动时会 `AttachConsole` 挂上父进程的控制台，
>   所以从 cmd 里跑能看到输出；控制台编码强制 UTF-8，中文不乱码。

`--uitest` 覆盖的交互点（节选）：

```
[✓] 收藏夹已加载 / 剧名已补齐 / 默认未勾选
[✓] 勾选剧目 → 分集进清单（追加不替换）/ 有分组标题行
[✓] 各剧分集序号独立（都从 1 开始），不串号
[✓] 取消勾选 → 只把该剧移出清单
[✓] 下载跑遍清单每一集 / 状态都标为已完成
[✓] 双击只下载这一集（不是整个清单）
[✓] 状态栏统计与清单一致 / 右侧显示版本号
[✓] MinWidth 能容纳工具栏（按实际宽度实测）
[✓] 授权按钮写的是「获取授权」（启动不自动取）
[✓] 切到兴趣岛 → 左栏换成营列表、隐藏「提取App缓存」
[✓] 兴趣岛条目加入同一张清单 / 带分组标题
[✓] 来源列标「红果 / 兴趣岛」
[✓] 暂停按钮标明「只对兴趣岛生效」
[✓] 5 档筛选：已完成 / 待下载 / 全部 各自的行数正确
[✓] 移出清单：行被删掉、分组计数同步、兴趣岛队列也删掉
[✓] 切回红果 → 收藏夹恢复、兴趣岛行仍在、红果行未受影响
[✓] logo 区单击打开「关于」
```

---

## 四、Avalonia 踩坑要点

下面是实际踩到的坑，代码里都有对应注释。

### 4.1 XAML 层

| 常见写法 | Avalonia 里的写法 | 说明 |
|---|---|---|
| `ToolTip="..."` | `ToolTip.Tip="..."` | ToolTip 是附加属性，写成属性直接编译失败 |
| `DataTrigger` | 换成 VM 上直接暴露 `bool` / `IBrush` | Avalonia 没有 DataTrigger |
| `<Window>` 下两个子元素 | 必须**只有一个**根子元素 | 否则报 `AVLN3000: Content 无法多次赋值` |
| `GridView` + `GridViewColumn` | 手写 `Grid`（列定义 `58,Auto,54,126,74,136`） | 不引 DataGrid 包，列宽全部算死，标题列由代码算 |
| `CollectionViewSource` 过滤 | 手工维护一份 `EpisodeView` 集合 | Avalonia 的 `ICollectionView` 不好用 |
| `MessageBox.Show` | 自写 `MsgBox`（纯代码搭窗口） | Avalonia 没有内置 MessageBox |
| WinForms `FolderBrowserDialog` | `StorageProvider.OpenFolderPickerAsync` | 跨平台版；`IStorageFolder.Path.LocalPath` 是本地路径 |
| `TextBox.AppendText` | `TxtLog.Text += ...` + `CaretIndex = 长度` | Avalonia 没有 AppendText |
| `Mouse.Capture` | `e.Pointer.Capture(this)` | |
| `StaticResource` | 改 `DynamicResource` | 窗口在运行时创建，App 级资源用 StaticResource 可能找不到 |
| `TextBox` 无 `CornerRadius` | 保持 Fluent 默认模板，只改配色 | 自写 TextBox 模板容易踩模板部件的坑 |

### 4.2 C# 层

- **`Avalonia 11 没有 `ButtonBase`** —— `Button : ContentControl`，`ClickEvent` 在 `Button` 上。
  勾选框的 Click 事件要 `new RoutedEventArgs(Button.ClickEvent)`。
- **`Window.BeginResizeDrag(WindowEdge, PointerPressedEventArgs)`** —— 枚举叫 `WindowEdge`
  （`NorthWest/North/NorthEast/West/East/SouthWest/South/SouthEast`），而且**要传事件参数**。
- **`Application` 上没有 `MainWindow` / `Shutdown`** —— 在
  `IClassicDesktopStyleApplicationLifetime` 上（`life.MainWindow = w` / `life.TryShutdown(0)`）。
- **`Application.TryFindResource` 不存在** —— 用 `Application.Current.Resources.TryGetValue(key, out var v)`。
- **后台线程不能调 `Window.Show()`** —— 抛 `InvalidOperationException: Call from invalid thread`。
  自检模式要用 `Dispatcher.UIThread.Post(async () => ...)`。
- **配色统一放 `Application.Resources`**，`DynamicResource` + `Palette.Brush(key)` 取用，
  避免同一份配色在 XAML 和 C# 里各写一遍。

### 4.3 自定义控件：容器型子控件别用 Left/Right 对齐

`ProgressCell`（进度条单元格）踩过一次很隐蔽的坑：

> `Border` 没有子内容 ⇒ `DesiredSize` 恒为 `(0,0)`。
> 填充层设了 `HorizontalAlignment = Left` 且**从未被 Measure** ⇒
> Avalonia 排版时把宽度收缩到 `DesiredSize.Width` = **0**
> ⇒ **整块渐变填充被压成 0 宽**，界面上只剩一条空轨道，看起来像"渐变没实现"。

★ **教训**：自定义控件里，容器型子控件**要么显式 Measure，要么用 `Stretch`**。
另外，界面效果"没出来"时，先**证明它到底画出来没有**（抓像素看颜色），
再证明状态对不对，最后才改 —— 只看代码容易把方向判错。

### 4.4 界面细节

- **列表项不会被拉伸**：Avalonia 的 `ListBoxItem` 内容默认按内容宽度排版，
  右边的标签会被挤出面板。
  ⇒ 剧名列的宽度由代码按 `LstShelf.Bounds.Width` 算好，统一写进每一行。
- **跑马灯**：用 `TextBlock + 位移` 实现，不自己 `DrawText`，前景色取灰蓝 `#8494A8`。
- **进度条**：不用 Avalonia 自带 `ProgressBar`（模板部件随版本变动），
  改成自写 `ProgressCell`（`Panel` + 两个 `Border` + 一个 `TextBlock`），
  渐变填充，百分比居中压在条上。
- **窗口按钮**：图标是**矢量自绘**（`WindowButton.Render` 覆写），
  不用字体字形 —— 字体缺字形会渲染成豆腐块。
- **一行里左右分组的工具栏禁止 `DockPanel`**：窗口一窄右侧按钮会被裁掉，
  用 `Grid ColumnDefinitions="Auto,*,Auto"`（清单工具栏就是这么改的）。

---

## 五、目录结构

```
hgvd-avalonia/
├── NuGet.config              # NuGet 镜像（发布时要用 --configfile 指定）
├── Directory.Build.props     # Release：win-x64 / 自包含 / 单文件 / 裁剪
├── build.cmd / build.sh      # 构建脚本（cmd 必须 CRLF，见 .gitattributes）
├── prepare-ffmpeg.ps1        # 取 ffmpeg/ffprobe 并压成 .gz
├── PACK-README.txt           # 打 zip 时放进包里的说明
└── src/Hgvd.Avalonia/
    ├── Program.cs            # 入口（挂父进程控制台 + UTF-8 输出）
    ├── App.axaml / .cs       # 全局配色与控件样式 + 命令行开关分发
    ├── Controls/
    │   ├── WindowButton.cs   # 矢量自绘窗口按钮
    │   ├── MarqueeControl.cs # 跑马灯
    │   ├── ProgressCell.cs   # 进度条单元格
    │   └── Palette.cs        # 资源取画刷的小工具
    ├── ViewModels/
    │   ├── NotifyBase.cs
    │   ├── ShelfRow.cs       # 左栏红果一行
    │   ├── XqdCampRow.cs     # 左栏兴趣岛一个营
    │   └── EpisodeRow.cs     # 右栏一行（红果分集 + 兴趣岛课共用）
    └── Views/
        ├── MainWindow.axaml / .cs
        ├── MainWindow.SelfTest.cs   # --selftest / --uitest / --batchtest / --demo
        ├── WaitAuthWindow.cs        # 等待模拟器登录（置顶小窗）
        ├── AboutWindow.axaml / .cs
        └── MsgBox.cs                # Avalonia 没有 MessageBox，自己搭
```

---

## 六、已知限制

- **核心层仍是 Windows 专用**：`MumuAuth`（读 MuMu 登录态）、`XqdAuth`（读兴趣岛
  WebView Cookie）、`CacheExtract` 都是 Windows 专属路径。界面层跨平台，
  **下载链路目前只能在 Windows 上跑**。
- **没有单独的「停止」按钮**：下载/暂停/继续共用一个按钮；要中止就暂停后关窗（分片与临时文件保留）。
- **历史窗口只读**：能看两源共用的 `history.json`（最近 500 条），不能从历史续传/重下。
- **「所属营」没有独立列**：靠分组标题表示；来源列、剩余时间列已有。
- **「空 body」是症状，不是根因** —— `code=101000` 是内容类型 key 用错
  （教学 1000/1001、视频流 1004），`code=100001` 是 body 缺字段，
  零字节 body 通常是**端点已停用/路径变了**。
  ★ **不要**把空 body 写成「服务端限流」：这条假设已被多次误判证伪
  （判据见 [`../docs/03-开发要点.md` §1.0](../docs/03-开发要点.md)）。
- 急用可走「提取 App 缓存」—— 那条路**完全不需要**播放接口。
- Avalonia 的 `ListBox` 选中态样式与 Fluent 默认略有差异，
  已用 `ListBox.shelf ListBoxItem` / `ListBox.ep ListBoxItem` 后代选择器覆盖。

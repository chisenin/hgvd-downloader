# libmetasec_ml.so 逆向笔记

> 目标：`/data/app/.../com.phoenix.read/lib/arm64/libmetasec_ml.so`（红果 7.4.1.32 自带）
> 起因：v04.10 之后本项目的 `x-argus` / `x-ladon` 用旧常量再也签不出能过网关的请求，
> 需要确认「算法/密钥是否变了」。

## 0. 样本与工具

| 项 | 值 |
|---|---|
| 文件 | `_ref/metasec/libmetasec_ml.so`（gitignore，不入库） |
| 大小 / MD5 | 4,501,136 / `d030a25597de95d550c4700460ea2ccc` |
| 架构 | ELF64 AArch64, `ET_DYN`，**已 strip**（无 `.symtab`，`.dynsym` 147 项，仅导出 `JNI_OnLoad`） |
| 依赖 | liblog / libdl / libandroid / libm / libc（**无 libc++_shared**，C++ 运行时静态链进去了） |
| 版本串 | `.rodata` 0x67806 `v04.10.03.04-bugfix`、0x670ca `v04.10.03` |
| 布局 | `.text` 0x172900 起 0x24dd54（2.4 MB）；`.rodata` 795 KB；`.data` 417 KB；`.bss` 85 KB |

工具（都在 `tools/metasec/`）：

```bash
python tools/metasec/analyze.py info            # ELF 概览
python tools/metasec/analyze.py strings argus   # 字符串
python tools/metasec/analyze.py xref 0x3ecd28   # adrp+add / 指针槽交叉引用
python tools/metasec/analyze.py func  0x2976e4 60

python tools/metasec/emu.py --only init         # 只跑 .init_array
python tools/metasec/emu.py --only onload -v    # 跑 JNI_OnLoad（打印 JNI 调用）
python tools/metasec/emu.py --call 1 -v         # 再调 MSManager.a(cmd=1,…)
```

依赖：`python -m pip install pyelftools capstone unicorn`。

## 1. 已确认的事实

### 1.1 旧常量**不在**库里（明文搜不到）

把现有实现里的 8 个常量（`ARGUS_SIGN_KEY` / `ARGUS_AES_KEY` / `ARGUS_AES_IV` /
`ARGUS_MASK` / `ARGUS_TAIL` / `MEDUSA_*`）拿去整文件搜，**一处都没有**。
⇒ 密钥不是「换个字符串就能对上」的那种改法，要么运行时派生，要么由服务端下发，
  要么就根本不在这个库里（另见 1.4）。

### 1.2 字符串是加密的

全文件只有 1742 条 ≥6 字符的可打印串，绝大多数是 libc++/libunwind 的 RTTI 文本。
`x-argus` / `x-ladon` / `com/bytedance/...` 之类**静态都搜不到**，运行时才解出来
（实测：`JNI_OnLoad` 期间 `.bss` 0x462680 被写成
`com/bytedance/mobsec/metasec/ml/MS`）。

### 1.3 入口只有一个原生方法

用 Unicorn 把库真正跑起来（见 §2），在 `RegisterNatives` 处抓到的方法表：

```
class  com/bytedance/mobsec/metasec/ml/MSManager      (vdex 里的真名)
[0]  a  (IIJLjava/lang/String;Ljava/lang/Object;)Ljava/lang/Object;  fn=0x297890
```

即整个 SDK 对 Java 只暴露**一个 RPC 式入口** `a(int cmd, int, long, String, Object) → Object`，
所有能力（初始化、取签名、上报…）都靠 `cmd` 分派。

⚠ 直接调 0x297890 现在**什么都不做**就返回：它是「跳板 + 参数重排」
（`mov x1,x0; mov w0,w2; …` 然后 `bl` 读 LR，`br LR+0x38` 落到自己的收尾块），
真正的实现要靠运行期改写/间接跳转才能到，静态看到的地址不可直接当函数用。

### 1.4 混淆特征（决定了"能不能静态反编译"）

- **控制流平坦化 + 跳板**：`JNI_OnLoad` 本身就是
  `stp x0,x1,[sp] … bl <读LR> … br x1` 的花指令；`0x297890`/`0x2978d0` 是同一套路。
- **JNI 函数表偏移被打散**：间接调用前先 `orr/movk/eor` 拼出表内偏移（如
  `0x297584 ldr x11,[x8,#0x6f8]`），所以按固定 `#0x6b8` 扫 `RegisterNatives` 只能捞到
  20 处候选，还得逐个人工确认。
- **`svc #0` + `brk #3` 混在代码里**：既是反调试，也让线性反汇编错位。
- **16 个 `.init_array` 构造器**（0x188a90…0x3bb494），其中 8 个是
  `adrp/add` 取一个 `.data` 常量再 `bl 0x392a0c` 的「注册」模式（模块自注册表）。
- 导入里有 `prctl` / `fork` / `kill` / `popen` / `system` / `dlopen` / `dlsym` /
  `__system_property_get` / `dl_iterate_phdr` ⇒ 有环境检测与反调试，纯静态跑不通。

## 2. 能跑起来：Unicorn 最小 Android 运行时

`tools/metasec/emu.py` 做了这些事，把 arm64 Android 库在 x86 上跑通：

1. 按 `PT_LOAD` 把 ELF 映射到 0 基址；**手动应用 `R_AARCH64_RELATIVE`**
   （`.init_array` 在文件里全是 0，目标地址只存在于重定位表 —— 这是第一个坑）。
2. 147 个 import 全部改写到自己的 stub 页，hook 命中后用 Python 实现
   （malloc 用 bump 分配器、`__system_property_get` 给假 prop、pthread_* 直接成功…）。
3. 伪造 `JNIEnv` / `JavaVM`：240 + 8 个表项各指向一个 stub，**表项下标就是 JNI 函数号**，
   hook 里按号分派并打印参数。
4. `TPIDR_EL0` 指向自建 TLS（`[+0x28]` 放栈保护哨兵），否则 `mrs` 一读就崩。

跑通的结果：

```
init_array 16 项  → 39 次 import 调用，无异常
JNI_OnLoad       → 1581 次调用，返回 0x10006（JNI_VERSION_1_6）
   FindClass("com/bytedance/mobsec/metasec/ml/MS…")
   GetSuperclass ×2 → RegisterNatives(1 个方法) → DeleteLocalRef
   pthread_create ×3（0x39e4e8、0x39db54×2，本 harness 不执行线程体）
```

## 3. 两个"看似有引用其实扫不到"的坑（方法论）

- **capstone 的 `Cs.disasm()` 遇到解不了的字节会直接停**，2.4 MB 的 `.text`
  在 0x188… 附近就断了 ⇒ 必须先 `md.skipdata = True`，否则任何交叉引用都扫不出来
  （本次第一版 `xref` 全 0 就是这个原因，重扫后 60 万条指令正常）。
- **Unicorn 不允许在 hook 里再 `emu_start`**（嵌套调用会以
  `access violation writing 0x24` 之类的方式炸掉）。要"调用库里的函数"只能
  `tail_call`：把 LR 指到临时 stub、PC 跳过去，等它 `ret` 回来再恢复现场。

## 4. Java 层：命令表与调用链（本轮新增）

样本：`hgvd_new.apk`（7.4.1.32，共享目录里已有），25 个 dex。
`MSManager` 定义在 `classes22.dex`，实现类 `Lms/bd/c/f3;` 在 `classes15.dex`。

### 4.1 结构

```
MSManager              门面（frameSign/getToken/getFeatureHash/getReportRaw/report/…）
   └─ 字段 a: Lms/bd/c/h3;(接口)
        └─ Lms/bd/c/f3;(实现, 持 long 句柄 a + Context)
             └─ Lms/bd/c/k3;.a(IIJLjava/lang/String;Ljava/lang/Object;)Ljava/lang/Object;  ← 与 native 同形
```

### 4.2 命令号（从 `f3` 的字节码里直接读出来的常量）

| cmd | 方法 | 实参 | 返回 | 说明 |
|---|---|---|---|---|
| `0x02000006` | `getFeatureHash(str, byte[])` | `(0, handle, str, bytes)` | `String[]` | 键值对 → Map |
| `0x02000007` | `getToken()` | `(0, handle)` | `String` | |
| `0x02000008` | `getReportRaw(str, int, Map)` | `(flags, handle, str, json)` | `Object[]` | |
| `0x0200000A` | **`frameSign(scene, int)`** | `(flags, handle, scene)` | `String[]` | 键值对 → Map，**最像签名头** |
| `0x01000001` | 通用服务调用 | `(0, 0, <6 位 hex key>, obj)` | 任意 | 配置/开关类，key 是明文字符串 |

`0x0200000A` 的字节码（`f3.frameSign`）：

```
iget-wide v0, f3.a          ; 句柄，-1 直接返回空 Map
const v1, #0x0200000a
move v2, <int 参数>          ; flags
move-object v5, <scene 字符串>
invoke-static/range {v1..v6}, Lms/bd/c/k3;.a(...)   ; obj = null
→ 返回值强转 String[]，两两 put 进 HashMap
```

### 4.3 三个反直觉的结论

1. **App 自己从不调 `frameSign`**。25 个 dex 里 `MSManager` 的方法引用只有
   `getToken` / `report` / `setDeviceID` / `setInstallID` / `getFeatureHash` /
   `setCollectMode`（`c83.n` 用 getToken 填用户信息、`SwitchManager` 用 0x01000001 取配置）。
2. **`x-argus` / `x-ladon` / `x-khronos` / `x-gorgon` 这四个串只出现在 `libsscronet.so`**
   （TTNet 网络栈），且旁边就是 `x-metasec-bypass-*` / `x-metasec-process-mssdk-*` 这一组
   TTNet 特性开关 ⇒ **签名头是网络层加的**，不是 App Java 层拼的。
3. 所以 Java 层的 cmd 表只能说明「能力清单」，**说明不了红果实际用哪条**。
   要在 `libsscronet` 里找它调 metasec 的位置（或直接动态看）。

### 4.4 附带发现：0x297890 是**诱饵**

`MSManager.a` 注册的 `fn=0x297890` 实测是「参数重排 + 读 LR + `br LR+0x38`」的跳岛，
直接调用会立刻返回第 3 个 Java 参数。20 个 `ldr [xN,#0x6b8]` 站点里，
0x18ed84 / 0x2080e0 / 0x229d68 附近还有 `.text` 常量（0x18eca0 / 0x207bd0 / 0x229d50…）
可能是真正的注册点，尚未逐个验证。

## 5. ★★★ 转折：MITM 抓包证明「签名根本没被校验」（2026-11-20 22:2x）

### 6.1 让模拟器信任自制 CA（这一步最容易卡住）

MuMu 的 Android 15 上，`/system/etc/security/cacerts` 有 145 个证书但**不生效** ——
运行时读的是 **Conscrypt APEX**：

```
/apex/com.android.conscrypt/cacerts        # 145 个，ro（loop 挂载）
/system/etc/security/cacerts               # 我们加的证书在这里，被忽略
```

正确做法（root + SELinux Permissive 即可）：

```bash
# 1) 准备合并目录（145 + 自己的）
cp /proc/<zygote64_pid>/root/apex/com.android.conscrypt/cacerts/* /data/local/tmp/cacerts_new/
cp /system/etc/security/cacerts/96fd57fc.0 /data/local/tmp/cacerts_new/
# 2) 在 zygote 的 mount namespace 里用 tmpfs 盖掉 APEX 目录（新起的 App 进程都继承）
nsenter -t <zygote64_pid> -m -- sh -c \
  'mount -t tmpfs tmpfs /apex/com.android.conscrypt/cacerts && \
   cp /data/local/tmp/cacerts_new/* /apex/com.android.conscrypt/cacerts/'
# 3) 杀 App 重启
```

`<hash>.0` = `openssl x509 -subject_hash_old -noout`（本例 `96fd57fc.0`）。
验证：`ls /proc/<app_pid>/root/apex/com.android.conscrypt/cacerts | wc -l` 应为 146。

代理用 `tools/metasec/mitm.py`（python + cryptography，自己签叶证书，
对客户端只宣告 http/1.1，按请求落盘到 `_ref/metasec/bodies/`）。

### 6.2 抓到的真实头：argus/ladon 是 **3 字节桩**

真机（7.4.1.32）对 `/novel/player/video_model/v1/`、`multi_video_model/preload/v1`：

```
X-Argus: hVgAaw==        ← 3 字节
X-Ladon: AT3DcA==        ← 3 字节
X-Gorgon: 8404d0804480dd271e5fe8d09ed19dbdd0710c2105858b8ef031   (46 hex，真的)
X-Helios / X-Medusa / X-Khronos / X-SS-STUB 也都在
```

而服务端照样 `code=0` + 返回播放地址 ⇒ **这个接口不校验任何签名头**
（argus/ladon/gorgon/khronos/medusa/helios/ss-stub 全都不校验，至少在 7.4.1.32 上如此）。

### 6.3 重放实验（在宿主机上，不经模拟器）

```
原样重放（连旧的 x-gorgon/x-khronos 一起带上）→ HTTP 200 code=0 每个视频 5 条直链
去掉全部签名头（gorgon/argus/ladon/khronos/medusa/helios/ss-stub）→ 仍然 200 code=0
不带 Cookie（匿名）→ 仍然 200 code=0
query 只留 9 个参数 → 仍然 200 code=0
body 只留 dr_scene + mixed_video_id_map（去掉 biz_param）→ code=100001 参数错误
```

⇒ **签名不是根因**。之前「v04.10 改了 native 算法」的判断被推翻；
真正的变化是**接口换了**，且 body 从 protobuf 变成了 **gzip JSON**。

### 6.4 新接口（唯一可用的播放模型）

```
POST https://api5-normal-sinfonlineb.fqnovel.com/novel/player/multi_video_model/preload/v1?<query>
Content-Type: application/json; charset=utf-8
Content-Encoding: gzip
body(gzip) = {"biz_param":{"detail_page_version":0,"device_level":3,
                           "disable_digg_stat":false,"disable_video_relate_book":false,
                           "need_all_video_definition":true,"need_mp4_align":false,
                           "use_os_player":false,"use_server_dns":false,
                           "video_platform":1024},
              "dr_scene":"preload",
              "mixed_video_id_map":{"1004":["<vid>", …]}}
```

响应：`{"code":0,"data":{"<vid>":{"video_model":"<JSON 字符串>", "video_height":…}}}`，
`video_model.video_list[].main_url / backup_url` 就是直链（实测 206 + `ftyp` 正常）。

★★ `mixed_video_id_map` 的**键 = 内容类型**（实测）：

| key | 适用 | 备注 |
|---|---|---|
| `1004` | 视频流 / 短剧 | 教学集用它会 `code=101000` |
| `1001` / `1000` | 教学合集分集 | 用 1004 会 101000 |

所以实现里按 `("1004","1001","1000")` 依次试，取第一个能解析出 `video_list` 的。

### 5.5 落地结果（Python + C# 双双跑通）

**Python**（`hgvd/api/playback.py`）
- 新增 `build_preload_body` / `fetch_preload` / `preload_query` / `_adapt_preload`
- `fetch_playback` 改走新接口并按 `("1004","1001","1000")` 依次试 map key，
  适配回老的 `data.video_model` 形状 ⇒ 调用方零改动
- `python -m hgvd download 7709054295315844121 --episodes 1`
  → **11.3 MB / 174.8s / h264 640x360 + aac**，ffprobe 校验通过
- 回归新增 I 组（17 项）：谁把签名头加回来、或改掉 body 形状，测试会红

**C# 发行版**（`Hgvd.Core/Api/SignedClient.cs`）
- 新增 `PathPreload` / `BuildPreloadBody` / `PlaybackPreloadAsync`；
  `PlaybackAsync` 先走 preload，拿不到流再回落旧接口（旧接口已死，仅保底）
- `dotnet build` 0 error；C# 离线回归全过
- GUI `--demo 7709054295315844121` 实跑：**3 集全部下载成功**
  （11.8 / 10.7 / 10.6 MB，ffprobe 正常）

## 6. 复盘：这条线为什么走偏了

1. **把「症状」当成了「根因」**：`video_model` 回 0 字节，最省事的解释是
   「签名被服务端拒了」，于是去猜 native 算法/密钥。实际上服务端根本
   **不校验** argus/ladon —— 真正的变化是**接口换成了 preload**。
2. **只看自己的请求，没看 App 的**：整条链上只要抓一次真机请求就能看到
   `X-Argus: hVgAaw==`（3 字节），立刻能推翻「要真签名」的假设。
   ⇒ **判据纪律再补一条：先拿到「对方的真实样本」，再谈算法。**
3. 反过来说，前面的逆向工作并不是白做：它证明了
   「旧常量一个都不剩、入口只有一个 `a()`」，从而**排除了**改常量的幻想，
   逼着去找真正的差异 —— 只是最后发现差异不在那一层。

### 还没做但值得做

- **C# 发行版同步**：`Hgvd.Core` 的播放请求要照 §5.4 改（否则 GUI 仍下载失败）。
- 把 `tools/metasec/mitm.py` 保留成常备工具：以后再遇到「接口悄悄变了」，
  十分钟就能抓到真机样本，比猜快得多。

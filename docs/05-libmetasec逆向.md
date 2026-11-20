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

## 5. 下一步（2026-11-20 22:xx 更新：第 1 项已完成）

1. ~~拿 Java 层 cmd 表~~ ✅ 见 §4。结论是它只给了「能力清单」，
   因为 App 的 Java 层根本不调 `frameSign`。
2. **动态观测（现在最该做的）**：在模拟器里 hook `ms.bd.c.k3.a`（ART 层）。
   ★ 关键：ART 的方法 hook 对**从 native 发起的 JNI 调用同样有效**，
   所以即使签名是 `libsscronet` 发的，也能看到「cmd / scene / 返回值」。
   需要 frida-server（x86_64 版，进程是 x86_64；arm64 的 so 由翻译层执行）。
3. **抓真实请求头**（可与 2 并行）：模拟器有 root，可把自制 CA 装进
   `/system/etc/security/cacerts` 再挂代理；拿到真 `x-argus` 值后
   与本地生成的做**结构对照**（长度/前缀/尾 9 字节），比猜常量有效得多。
4. 让那 3 个 `pthread_create` 线程真跑（context 轮转），看后台线程是否承担 init/上报。
5. 只有 2~4 都拿不到算法时，才硬啃 `.text`（2.4 MB 平坦化代码，成本极高）。

> 结论先行：**"密钥变了"这个假设目前既没被证实也没被证伪** ——
> 能确定的是旧常量在 v04.10 里一个都不剩、对外只留一个 `a()` 入口、
> 且签名头是 TTNet 网络层加的。继续照旧常量猜没有意义。

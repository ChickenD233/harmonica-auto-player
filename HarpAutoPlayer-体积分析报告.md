# HarpAutoPlayer 体积分析报告

对象：https://github.com/ChickenD233/harmonica-auto-player（本地工作副本，v1.0.10 / 线上 v1.0.9）
方法：在本地用 `.tools/dotnet`（.NET 8.0.424）对同一份源码跑了 8 组真实发布，逐一称重。
所有数字都是实测，不是估算。

---

## 结论速览

| 问题 | 答案 |
|---|---|
| 为什么 exe 有 94 MB？ | **应用自己的代码只占 1.3 MB（约 1%）。** 其余 99% 是「自包含 .NET 8 运行时 + Avalonia UI 框架 + Skia 图形库」的固定开销 |
| 最大的浪费在哪？ | 没开 **裁剪**：一堆用不到的 BCL 被整包带进来（`System.Private.Xml` 7.6 MB、`System.Linq.Expressions` 3.5 MB、`System.Data.Common` 2.7 MB…）；另外还有 ~6 MB 纯调试用的原生库 |
| 能优化到多少？ | 只改 **2 个发布参数**：exe **94 MB → 约 21 MB**，下载 zip **39.6 MB → 约 15.5 MB（−61%）**，功能不减、不要求用户装 .NET |
| 顺带发现 | 本地 `.git` 有 **1.2 GB** 垃圾对象；线上 tag `v1.0.0` 里塞了一个 **43 MB** 的成品包 |

---

## 已落地（实测复核）

- `HarpAutoPlayer/build-win.sh` 已加入 `EnableCompressionInSingleFile`、`PublishTrimmed` + `TrimMode=partial`、`DebugType=none`。
- 同文件补上 `AVALONIA_TELEMETRY_OPTOUT=1`。该开关缺失时，Avalonia 构建遥测会写 `~/Library/Application Support/AvaloniaUI/`，在受限环境（沙箱、只读 HOME）下直接让 MSBuild 报 `MSB4018` 失败。
- 用新脚本完整重跑一次发布，数值与预测一致：

| | 改造前 | 改造后 | 降幅 |
|---|---:|---:|---:|
| exe 磁盘占用 | 90.12 MiB | **20.87 MiB** | −76.8% |
| zip 下载体积 | 37.85 MiB | **15.51 MiB** | −59.0% |

- 本地 `.git` 已执行 `git reflog expire --expire=now --all && git gc --prune=now`：**1.2 GB → 42 MB**。执行后复核：可达提交 31 个、标签 10 个、HEAD 不变、`git fsck` 无错误。
- 尚未做：Windows 上的功能回归（第六节清单）与 tag `v1.0.0` 的清理（需要动远端）。

---

## 一、体积构成（实测拆解）

### 1. 最终交付物

```
release/HarpAutoPlayer-win-x64.zip   39.6 MB   ← 用户下载这个
  └─ HarpAutoPlayer.exe              94.5 MB   ← 解压后双击这个
     └─ 更新说明.txt                  3.6 KB
```

### 2. 94 MB 里到底装了什么（按发布目录 105.7 MB 拆解）

| 成分 | 大小 | 占比 | 说明 |
|---|---:|---:|---|
| BCL 托管程序集 `System.*` / `Microsoft.*` | 59.3 MB | 56% | .NET 基础类库。**其中一多半本项目根本用不到** |
| .NET 运行时原生（coreclr / clrjit / hostfxr） | 17.9 MB | 17% | 跑 .NET 必须有的引擎 |
| 原生图形栈（SkiaSharp + ANGLE + HarfBuzz） | 16.3 MB | 15% | Skia 9.2 / ANGLE 5.3 / HarfBuzz 1.8 |
| Avalonia UI 框架 | 6.8 MB | 6% | 界面框架本体 |
| **调试 / 诊断原生库** | **6.1 MB** | **6%** | `mscordaccore`×2、`mscordbi`、`DiaSymReader`、`createdump` —— 只有崩溃转储和带符号堆栈才用得到 |
| DryWetMidi（MIDI 解析） | 0.9 MB | 1% | |
| **`HarpAutoPlayer.dll`（你自己的代码）** | **1.3 MB** | **1%** | 全部业务逻辑 |

> 一句话：**这不是一个"臃肿的程序"，而是"一个正常程序 + 一整个 .NET 运行时"。**

---

## 二、为什么会这么大（4 个根因）

`build-win.sh` 里的这条命令决定了产物大小：

```bash
"$DOTNET" publish HarpAutoPlayer.csproj -c Release \
    -r win-x64 --self-contained true \        # ① 把整个 .NET 运行时打进 exe
    -p:PublishSingleFile=true \               # ② 单文件，但没开压缩
    -p:IncludeNativeLibrariesForSelfExtract=true
```

1. **`--self-contained true`（约 70 MB）**
   为了让用户"双击即用、不用装 .NET"。这是有意的取舍，不是 bug。想省掉这 70 MB 就只有一个办法：改成框架依赖（要求用户装 .NET 8 桌面运行时）——见第四节方案 F。

2. **没有 `PublishTrimmed`（约 30 MB 可省）**
   .NET 是"整套类库一起发布"的，裁剪器会根据实际调用图删掉没用到的代码。当前发布连 XML、数据表、并行 LINQ、VisualBasic 兼容层这些完全无关的东西都带上了。

3. **单文件没有开压缩（exe 体积减半，但下载不变）**
   没开 `EnableCompressionInSingleFile` 时，exe 就是 219 个文件原样拼在一起的容器。开了之后 exe 直接小一半多。

4. **`DebugType=embedded` + 调试原生库**
   PDB 内嵌只占约 0.03 MB（可忽略）；但发布时无条件带上的 `mscordaccore` / `mscordbi` / `DiaSymReader` 是 **6 MB**，正常跑游戏时一次都不会用到。

---

## 三、三个容易踩的认知误区

这一节很重要，网上流传的很多".NET 体积优化"经验对**这个项目无效**：

### ❌ 误区 1："开单文件压缩能把下载体积减半"

**不成立。** 你交付的是 zip，而 zip 本身就是 deflate 压缩过的：

| | exe 磁盘占用 | zip 下载体积 |
|---|---:|---:|
| 现状 | 90.1 MB | 37.9 MB |
| 只开 `EnableCompressionInSingleFile` | **43.4 MB** | 37.9 MB（**几乎不变**） |

exe 内部压过之后，zip 再压不动了。**开压缩只改善用户解压后的磁盘占用，不改善下载。**
真正能降下载体积的只有**裁剪**。

### ❌ 误区 2："`InvariantGlobalization=true` 能省 30 MB"

**对本项目完全无效。** 我确认过：win-x64 自包含发布目录里**没有任何 ICU 数据文件（`icudt*.dat`）**——Windows 用的是系统自带的 ICU。实测开关它只带来 **0.03 MB** 差异，却会让日期/大小写/排序等文化相关行为发生变化。

### ❌ 误区 3："`TrimMode=full` 比 `partial` 省很多"

**只省 0.5 MB，风险却高得多。** 完全裁剪会连带裁剪你自己的程序集，而这个项目有 13 处反射式 `{Binding}` 和反射式 JSON 序列化——为 0.5 MB 冒功能损坏的风险不划算。

---

## 四、优化方案与实测收益（全部实跑过）

| # | 配置 | exe 磁盘 | zip 下载 | 相对现状 | 风险 | 建议 |
|---|---|---:|---:|---:|:--:|:--:|
| A | 现状（`build-win.sh` 原样） | 90.1 MB | 37.9 MB | — | — | — |
| B | A + 单文件压缩 | 43.4 MB | 37.9 MB | exe −52% | 无 | ✅ |
| C | B + 精简运行时开关 | 43.4 MB | 37.9 MB | 同 B | 低 | ⚠️ 收益≈0，不必 |
| **G** | **B + `PublishTrimmed`(partial)** | **20.9 MB** | **15.5 MB** | **exe −77%，下载 −59%** | **低** | **✅ 推荐** |
| D | G + 精简运行时开关 | 20.5 MB | 15.1 MB | 再多 0.4 MB | 中 | ⚠️ 不值得 |
| E2 | G + `TrimMode=full` | 19.9 MB | 14.6 MB | 再多 0.5 MB | 高 | ❌ |
| H | 裁剪但不压缩单文件 | 38.7 MB | 15.6 MB | 下载同 G | 低 | 🔸 备选 |
| F | 不打包运行时（框架依赖） | 9.9 MB | 9.7 MB | 下载 −74% | 中 | 🔸 备选 |

**读表要点**

- **C 和 D 里的"精简运行时开关"**（`UseSystemResourceKeys` / `StackTraceSupport=false` / `EventSourceSupport=false` / `InvariantGlobalization=true`）**总共只值 0.4 MB**，代价是异常信息变成 `Arg_FileNotFound` 这类代号——你的程序会把异常写进日志让用户回传，这个代价不值得。**别开。**
- **H（裁剪但不压缩）** 下载体积和 G 几乎一样（15.6 vs 15.5 MB），好处是启动时不需要自解压、某些杀软也不容易误报；代价是用户解压后看到的是 38.7 MB 的 exe。**如果你更在意启动速度和杀软兼容，选 H。**
- **F（框架依赖）** 能压到 9.9 MB，但要求用户先装 .NET 8 桌面运行时，与你"双击即用"的定位冲突。可以作为**并行的"精简版"**发布，不适合替换主包。

---

## 五、推荐落地改动（只动 `build-win.sh`，不改 csproj）

实测确认：`-p:` 传的命令行属性会覆盖 `csproj` 里的同名属性，所以**不需要改 `HarpAutoPlayer.csproj`**，改动全部收敛在发布脚本里。

在 `HarpAutoPlayer/build-win.sh` 的 publish 命令中加两行：

```diff
 "$DOTNET" publish HarpAutoPlayer.csproj -c Release \
     -r win-x64 --self-contained true \
     -p:PublishSingleFile=true \
     -p:IncludeNativeLibrariesForSelfExtract=true \
     -p:ExcludeNativeLibrariesFromSingleFile=false \
+    -p:EnableCompressionInSingleFile=true \
+    -p:PublishTrimmed=true -p:TrimMode=partial \
     -o "$OUT"
```

就这两行，预期效果：

| | 现在 | 改后 |
|---|---:|---:|
| zip 下载体积 | 39.6 MB | **约 15.5 MB（−61%）** |
| 解压后 exe | 94.5 MB | **约 21 MB（−78%）** |
| 用户是否需要装 .NET | 否 | 否（不变） |
| 功能 | — | 不变（需按第六节回归验证） |

**副作用（可接受，但要知道）**

- 单文件压缩后，**首次启动**多约 0.5–1 秒（要把原生库解到临时目录）。
- 压缩 + 自解压的单文件 exe 被部分杀软重点关照，建议发布前用 Windows Defender / 火绒扫一遍。
- 启动后 `%TEMP%` 里会有一个解压目录（`IncludeNativeLibrariesForSelfExtract` 的固有行为，现在也有，只是压缩后更明显）。

**还有一处可选收益：干掉 6 MB 的调试原生库**

这 6 MB 在文件夹版里是 `mscordaccore.dll`×2 + `mscordbi.dll` + `Microsoft.DiaSymReader.Native.amd64.dll`，裁剪器不会删它们。单文件模式下它们被塞进 exe，压缩后大约值 2 MB。想拿掉需要在 `csproj` 里加一个 target，在打包前从发布清单里剔除：

```xml
<Target Name="StripDiagnosticNatives" AfterTargets="ComputeFilesToPublish">
  <ItemGroup>
    <ResolvedFileToPublish Remove="@(ResolvedFileToPublish)"
      Condition="$([System.String]::Copy('%(Filename)').StartsWith('mscordaccore'))
              Or '%(Filename)' == 'mscordbi'
              Or '%(Filename)' == 'Microsoft.DiaSymReader.Native.amd64'
              Or '%(Filename)' == 'createdump'" />
  </ItemGroup>
</Target>
```

代价：崩溃时不再生成 dump，堆栈里没有行号。属于进阶优化，**优先级低于上面两行**，且必须实测验证。

---

## 六、裁剪的风险与前置改动

裁剪不是零风险，这是唯一需要认真对待的地方。实测 `TrimMode=partial` 产生 **26 条警告，3 类**：

| 警告 | 来源 | 影响评估 |
|---|---|---|
| `IL2026` ×23 | `Persist/AppConfig.cs` 用反射式 `System.Text.Json`；`MainWindow.axaml` 有 13 处 `{Binding}`；Avalonia 内部 Bson | **partial 模式下你的程序集不参与裁剪**，`AppConfig` 和 `TrackRowVM` 的成员都会保留，实际风险低 |
| `IL2104` ×2 | `Melanchall.DryWetMidi` + `Avalonia.DesignerSupport` 自身有裁剪警告 | **需要重点回归**：MIDI 解析是核心功能 |
| `IL2008` ×1 | `System.Diagnostics.FileVersionInfo` 找不到 `System.SR` | .NET 8 已知无害警告，可忽略 |

**上线前必须做的回归（Windows 上）**

1. 跑一遍仓库里已有的 `HarpAutoPlayerSmoke` 自检项目。
2. 手工载入几首**结构复杂**的 MIDI：多音轨、含调号/拍号元事件、含 `scale=255` 这类脏数据（v1.0.5 修过的那类）、打击乐轨、超长曲目。
3. 走一遍完整链路：导入 → 选轨 → 移调 → 试播 → 导出 Lua/CSV → 检查更新（这条会走 `HttpClient` + JSON，裁剪后要确认）。
4. 确认配置文件 `AppConfig` 的读写正常（`%LocalAppData%\HarpAutoPlayer`）。

**建议顺手做的两处加固**（做完之后裁剪就从"低风险"变成"基本无风险"）

- 把 `csproj` 里的 `<AvaloniaUseCompiledBindingsByDefault>` 改为 `true`，并给 `TrackRowVM` 加 `x:DataType`，把 13 处反射绑定改成编译绑定。
- 用 `JsonSerializerContext` 源生成替代 `AppConfig` 里的反射式 `JsonSerializer`。

这两项本身也是性能与启动速度的小优化，与体积无关但顺手。

---

## 七、软件之外：仓库本身的体积问题

"这么大"如果指的是 clone 仓库，还有两个独立的坑：

### 1. 本地 `.git` = 1.2 GB，其中约 1.2 GB 是垃圾

实测：

```
对象库全部 blob     225 个   1244.6 MB
refs 可达 blob      129 个     43.2 MB
refs 可达提交        31 个
对象库提交总数       206 个
```

也就是说约 **1.2 GB 的对象只被 reflog 引用**（历史上有 206 个提交，但任何分支/标签都到不了其中的 175 个）。
原因很清楚：开发过程中反复把构建出来的 exe / zip 提进 git，之后又改写/重置了历史，旧提交被 reflog 保住了。

`git fsck --unreachable` 和 `git prune` 都报 0 —— 因为 reflog 让它们"可达"，默认的 2 周宽限期也拦着。要清必须先让 reflog 过期：

```bash
# 预期 .git 从 1.2 GB 降到 ~50 MB
git reflog expire --expire=now --all
git gc --prune=now --aggressive
```

### 2. 线上仓库：tag `v1.0.0` 里带着一个 43.3 MB 的成品包

通过 GitHub API 查 `v1.0.0` 的完整树，确认里面确实有：

```
release/HarpAutoPlayer-win-x64.zip   size: 43275353 (43.3 MB)
```

后果：任何 `git clone` 都会多拉 43 MB（tag 默认会被 fetch）。
（注意 GitHub API 里仓库的 `size` 字段报的是 350 KB，**不准**，别被它骗了；实际 tag 树里有这个 blob。）

当前 `.gitignore` 已经正确排除了 `release/`，但历史里还在。处理方式二选一：

- **推荐**：删掉或重建 `v1.0.0` 这个 tag（v1.0.0 的成品本来就在 Releases 里，tag 没实际用途）。
- 或者不管它 —— 仓库主要靠 Releases 分发，clone 的人不多，43 MB 只是难看。

---

## 八、其他发现

1. **`build-win.sh` 清理 `.dylib` 的逻辑是对的**，但工作区里残留了旧产物：
   `release/win-x64-single/Melanchall_DryWetMidi_Native64.dylib`（106 KB，macOS 库，Windows 用不到，9/10 的旧文件）。
   我确认过**线上 zip 是干净的**（只有 `HarpAutoPlayer.exe` + `更新说明.txt` 两个文件），只是本地这个旧目录建议删掉，免得哪天误发。

2. **`Avalonia.Desktop` 这个元包会把 X11 / macOS / Linux 后端一起带进来**（`Avalonia.X11` 0.35 MB、`Avalonia.Native` 0.32 MB、`Avalonia.FreeDesktop` 0.19 MB、`Tmds.DBus.Protocol` 0.20 MB…）。
   裁剪会自动去掉它们（实测已全部消失），所以**不需要**为了体积换成 `Avalonia.Win32` + `Avalonia.Skia`。但如果你只想发布 Windows 版，显式换掉依赖会让构建更快、警告更少。

3. **`av_libglesv2.dll`（ANGLE，5.2 MB）** 是 Avalonia 在 Windows 上默认的 GL 渲染后端。压掉它可以再省 5 MB，但会退回软件渲染、UI 明显变卡。**不建议**。

4. **`.gitattributes` 缺失**：仓库里的 `*.mid`、`*.zip`、`*.png` 没有二进制标记，长期看容易出问题。建议加一行 `*.mid binary`、`*.zip binary`。属于卫生问题，不影响体积。

---

## 九、行动清单（按性价比排序）

| 优先级 | 动作 | 收益 | 成本/风险 |
|:--:|---|---|---|
| ✅ 1 | `build-win.sh` 加 `-p:EnableCompressionInSingleFile=true` | exe 90→43 MiB | 零风险，1 行。**已完成** |
| ✅ 2 | 同处加 `-p:PublishTrimmed=true -p:TrimMode=partial -p:DebugType=none` | exe 43→20.9 MiB，下载 37.9→15.5 MiB | 低风险。**已完成**，待 Windows 回归验证 |
| ✅ 3 | `git reflog expire --expire=now --all && git gc --prune=now` | 本地 `.git` 1.2 GB→42 MB | 零风险。**已完成** |
| 🟡 4 | 删掉 tag `v1.0.0`（含 `push --delete`） | 新 clone 少拉 43 MB | 低，但动远端 |
| 🟢 5 | 开编译绑定 + JSON 源生成 | 消除全部裁剪警告 | 开发工作量 |
| 🟢 6 | 剔除调试原生库 target | 再省约 2 MB | 中，需验证 |
| ⚪ 7 | 不做 | — | 别开 `InvariantGlobalization` / `UseSystemResourceKeys` / `TrimMode=full`；别为了 39 MB 的框架依赖包牺牲"双击即用" |

---

## 十、还能更小吗？软件本体的实测下限

先明确一件事：**15.5 MiB 的下载包里，你自己的代码只占 1.23 MiB（3.9%）。** 其余全是 .NET 运行时与 Avalonia 的图形栈。

### 15.5 MiB 由什么组成（裁剪后文件夹版 45.07 MiB）

| 类别 | 未压缩 | 占比 |
|---|---:|---:|
| 图形/文本原生栈（Skia + ANGLE + HarfBuzz） | 16.01 MiB | 35.5% |
| .NET 基础类库 BCL | 7.55 MiB | 16.7% |
| .NET 运行时原生（coreclr / clrjit / hostfxr） | 7.20 MiB | 16.0% |
| 调试/诊断原生库（**可删**） | 5.91 MiB | 13.1% |
| Avalonia UI 框架 | 4.15 MiB | 9.2% |
| 你自己的代码 | 1.75 MiB | 3.9% |
| 其他 | 1.59 MiB | 3.5% |
| DryWetMidi | 0.91 MiB | 2.0% |

单个文件看：`libSkiaSharp.dll` **8.98 MiB** + `av_libglesv2.dll` **5.17 MiB** + `libHarfBuzzSharp.dll` **1.72 MiB** = **15.87 MiB**。这三个是 Avalonia 的 GPU 渲染与文本排版栈，与业务逻辑无关，但比整个下载包压缩后还大。

### 三条路的下限（全部实跑）

| 路线 | 下载体积 | 用户需装 .NET | 说明 |
|---|---:|---|---|
| **A 现状**：自包含 + 裁剪 | **15.51 MiB** | 否 | 已落地 |
| **A+** 再删调试/诊断原生库 | 约 13.5 MiB（估算） | 否 | 文件夹版实测省 **2.58 MiB**（18.64 → 16.06）。单文件包未实测：`CustomAfterMicrosoftCommonTargets` 注入未生效，需正式加 csproj target 后重测 |
| **B 框架依赖**：不打包运行时 | **9.63 MiB** | 是（.NET 8 桌面运行时，约 55 MB） | .NET 8 **不允许**对框架依赖发布做裁剪（实测报 `NETSDK1102`）。发布目录 25.94 MiB → zip 9.63 MiB |
| **C 换技术栈** | 约 3–6 MiB | 否 | Tauri / WebView2（Win10/11 自带）或原生 Win32。这是重写，不是优化 |

### 关键结论

1. **框架依赖模式（9.63 MiB）里，最大的文件仍是 `libSkiaSharp.dll` 8.98 MiB 与 `av_libglesv2.dll` 5.17 MiB。** 即使把 .NET 运行时整个拿掉，图形栈仍占下载体积的大部分。这是 Avalonia 的固定成本，不是你的代码造成的。
2. **15.5 MiB 已经接近这个技术栈的地板。** 再往下只有三条路：删 2.58 MiB 调试库、要求用户装 .NET（省约 6 MiB）、或换栈重写（省 10 MiB 以上）。
3. 参照：同类 Electron 桌面工具普遍 80–150 MB。当前 15.5 MB 在「自带运行时、双击即用」这一档里属于偏小。
4. **不建议**为了 5 MiB 删掉 `av_libglesv2.dll`（ANGLE）：Avalonia 会退回软件渲染，界面明显变卡。收益不值。

---

## 附录：实验方法

全部在本地 macOS 上用仓库自带的 `.tools/dotnet`（.NET 8.0.424）交叉发布 win-x64 实测，脚本与原始日志保留在 `_sizecheck/`：

- `size-matrix.sh` / `size-extra.sh` / `folder-compare.sh` —— 8 组发布配置
- `size-floor.sh` / `size-floor2.sh` / `categorize.py` —— 第十节的成分拆解与下限测试
- `out/*.log` —— 每组发布的完整 MSBuild 日志（含裁剪警告原文）
- `out/results.txt` —— 原始字节数

注：实验中发现 `dotnet publish` 有增量复用行为——属性不同的两次发布可能被 MSBuild 判定为"已最新"而直接拷贝旧产物（第一次跑 `TrimMode=full` 就是这样，结果与 `partial` 逐字节相同）。E2 已清空 `obj/` 强制重建后重测，表中数据均为真实重建结果。

任何采用裁剪的方案，**上线前必须在 Windows 上跑通第六节的回归清单**——我在 macOS 上只能验证"体积"和"裁剪警告"，无法验证运行时行为。

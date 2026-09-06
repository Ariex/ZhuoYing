# Linux 移植记录

分支 `feature/ubuntu-migration`。目标：Linux 下 REQUIREMENTS v1 的功能全部可用。

## 开发环境（本机实测）

| 项 | 值 |
|----|-----|
| 发行版 | Ubuntu 26.04 LTS (Resolute Raccoon) |
| 桌面 | GNOME on **Wayland**（Xwayland 在，`DISPLAY=:0`） |
| 显示 | 单屏 1920×1080，96 DPI，**无缩放**（经 NoMachine 远程桌面） |
| .NET | SDK 10.0.111（snap，`DOTNET_ROOT=/var/snap/dotnet/common/dotnet`） |
| portal | Screenshot v2、ScreenCast v5、GlobalShortcuts v1、RemoteDesktop v2 全部可用 |

与 Windows 开发机的关键差异：那边是双 4K @200%、混合 DPI 是一级场景；
这边是单屏无缩放。**混合 DPI 的回归只能在 Windows 侧做**，Linux 侧的多屏/缩放
需要另造环境（Xvfb 可造任意多屏布局，见下）。

两个必须先解决的环境坑见 `TROUBLESHOOTING.md`：
§13（IPv6 黑洞导致 `dotnet restore` 挂死）、§14（libSkiaSharp native 版本冲突）。

### 构建与运行

```bash
./build-linux.sh build      # 等价 dotnet build（已带必需的环境变量）
./build-linux.sh run -- --api-monitors
./build-linux.sh publish    # 自包含 linux-x64 → publish/linux-x64
```

直接跑产物需要 `export DOTNET_ROOT=/var/snap/dotnet/common/dotnet`（snap 版
dotnet 没往标准位置装 host）。

### 自动化验证环境：Xvfb

真实桌面是 Wayland，X11 抓屏只能看到 X 客户端（合成器内容全黑，实测
1920×1080 里 2073458 像素纯黑），**没法用来验证 X11 抓屏路径**。
所以 X11 路径一律在 Xvfb 里验证——完全受控、可复现、不干扰用户桌面，
还能造出 Windows 开发机上难造的多屏布局：

```bash
Xvfb :99 -screen 0 1920x1080x24 +extension RANDR +extension XFIXES +extension SHAPE &
DISPLAY=:99 xlogo -geometry 400x300+200+150 -bg blue -fg white &   # 常驻测试内容
DISPLAY=:99 ./src/Zhuoying/bin/Debug/net10.0/Zhuoying --test-copy "200,150,400,300"
```

测试内容**必须用常驻窗口**，不能用 `xsetroot`/`display -window root` 铺背景
（原因见 TROUBLESHOOTING §15）。对照基准用
`ffmpeg -f x11grab -video_size WxH -i :99.0+X,Y -frames:v 1 -update 1 -y out.png`。

## 架构：平台边界收敛

移植第一步是把散在上层的 `Win32.*` 调用收回接口（CLAUDE.md 列的"泄漏点"表）。
现在**上层一律经 `Platform/PlatformServices` 取服务**，不再直接引用
`Platform.Windows` / `Platform.Linux` 下的具体类型。

```
Platform/
  PlatformServices.cs      平台实现的唯一取用口（编译期按 ZY_WINDOWS/ZY_LINUX 选定）
  IScreenCapture.cs        抓屏 + 显示器 + 窗口枚举（新增 CaptureInto / GetVisibleWindowsWithTitles）
  IHotkeyService.cs        全局热键
  IClipboardImage.cs       剪贴板图片
  IStartupManager.cs       开机自启                      ← 新增
  IWindowEffects.cs        录制期窗口特效（排除入镜/点击穿透）  ← 新增
  IFrameSource.cs          录屏帧源                      ← 新增
  IVideoEncoder.cs         MP4 编码器                    ← 新增
  PngDpiWriter.cs          PNG pHYs 写入（纯托管，从 Windows 目录移出）
  Windows/                 ZY_WINDOWS 时编译
  Linux/                   ZY_LINUX 时编译
```

csproj 里两个目录**互斥编译**（`ZyWindows` 属性按 `RuntimeIdentifier` 或构建机
OS 判定），所以 Linux 构建里根本不存在 `Microsoft.Win32.Registry`、
Media Foundation、Desktop Duplication 这些类型，不需要运行时判断。

上层随之改动（Windows 行为逐字不变）：

| 文件 | 改动 |
|------|------|
| `App.axaml.cs` | 服务改经 PlatformServices；`--test-dda` 整段 `#if ZY_WINDOWS`（DDA 与 BitBlt 双路对比是 Windows 专属后端，Linux 只有 XGetImage 一条路） |
| `Capture/RecordingController.cs` | 私有 `ExcludeFromCapture` → `PlatformServices.WindowEffects` |
| `Capture/RegionFrameSource.cs` | 拆成 `Platform/*/『*FrameSource』` + `Capture/IScreenRecorder.cs` |
| `Capture/Mp4Recorder.cs` | 变纯托管驱动循环，编码交 `IVideoEncoder` |
| `Agent/AgentCli.cs` | `AttachConsole` 进 `#if ZY_WINDOWS`（Linux 是 Exe，stdout 直连终端）；抓屏改经 `CaptureInto` |
| `Agent/McpServer.cs`、`Settings/SettingsWindow.cs` | 改经 PlatformServices |

## Linux 实现现状

### X11 会话（已全面验证）

在 Xvfb 1920×1080 上逐项实测，除特别注明外全部通过：

| 能力 | 实现 | 验证方式与结果 |
|------|------|----------------|
| 抓屏 | `X11ScreenCapture`（XGetImage） | 与 `ffmpeg -f x11grab` 抓同区域**逐字节一致**（120000 像素零差异） |
| 显示器枚举 | XRandR CRTC + primary + `_NET_WORKAREA` | `--api-monitors` 报告 1920×1080 scaling=1 primary ✅ |
| 全局热键 | `X11HotkeyService`（XGrabKey 专用线程） | 托盘驻留下注入 `Ctrl+2`，遮罩窗口如期出现 ✅ |
| 剪贴板 | `LinuxClipboardImage`（xclip / wl-copy，PNG + pHYs） | 复制出的 PNG 与直抓**逐字节一致**，pHYs=3780ppm（96 DPI）✅ |
| 保存 | 复用纯托管路径 | 落盘 `~/Pictures/捉影/捉影_20260906_104638.png`，中文目录与文件名正常 ✅ |
| 钉屏 | `PinWindow` | 窗口精确出现在 `400x300+200+150` ✅ |
| 标注：形状 | `ShapeElement` | 输出合成红系像素 4000（基线 0）✅ |
| 标注：线/箭头 | `LineElement` | 红系 737 ✅ |
| 标注：文字 | `TextElement` | 红系 924、唯一色 580，**中文渲染正常** ✅ |
| 标注：编号 | `NumberElement` | 红系 1694 ✅ |
| 标注：画笔 | `PenElement` | 红系 1211 ✅ |
| 标注：橡皮 | `EraserElement` | 叠加橡皮后红系由 1211 降到 673，擦除生效 ✅ |
| 标注：区域模糊 | `MosaicElement` | 模糊区内 34% 像素改变、**区外 0 差异**（边界精确）✅ |
| 标注：图章 | `StampRasterizer`（Svg.Skia） | 1162 像素、包围盒精确，**SVG 光栅化正常** ✅ |
| GIF 录屏 | 纯托管 `GifWriter` + `X11FrameSource` | 31 帧 / 3075ms，帧差分生效；**修掉一个 LZW 码宽 bug 后** ffmpeg 零错误、Pillow 读全帧 ✅ |
| MP4 录屏 | `FfmpegVideoEncoder`（rawvideo → libx264） | 63 帧写入，解码亮区精确落在动画窗位置，**颜色序列红→绿→蓝→黄** ✅ |
| MP4 解码回读 | `FfmpegVideoProbe` | 报告格式与 Windows 侧一致 ✅ |
| 完整录制流程 | `RecordingController` | `--test-record-ui` 走完红框/控制条/落盘，GIF 进 `~/Pictures/捉影/` ✅ |
| 设置窗口 | `SettingsWindow` | 400×600 正常渲染，中文/控件/取色器全对（见下）✅ |
| 开机自启 | `LinuxStartupManager`（XDG autostart） | 界面勾选并保存后正确生成 `~/.config/autostart/zhuoying.desktop` ✅ |
| Agent API | `--api-monitors` / `--api-capture` | 输出正确 ✅ |
| MCP 内置 HTTP | `McpHttpServer` | 监听 127.0.0.1:8990；`take_screenshot` 返回的图像与直抓**逐字节一致**；恶意 Origin → **403** ✅ |
| 窗口枚举 | EWMH `_NET_CLIENT_LIST_STACKING` | ⚠ Xvfb 无窗管，`_NET_CLIENT_LIST_STACKING` 不存在，`list_windows` 返回空。**代码路径未被真正覆盖**，需带窗管的环境复验 |
| 点击穿透 | `X11WindowEffects`（XShape 空输入区） | ⏳ 未单独验证 |
| 录屏边框/控制条 | 不支持排除捕获时改用**四条实心边条** + 控制条只停区域外 | 录制产物中红框/控制条/按钮均 **0 像素** ✅ |

设置窗口的鼠标点击测试同时覆盖了**输入命中**——CLAUDE.md 强调"自动化验证渲染 ≠ 验证输入"，
这里点复选框、点保存都按预期生效，说明 Linux 下渲染缩放与命中缩放没有分裂
（Windows 侧 §7/§8/§10 那类问题未在 X11 复现）。

### 录制期控件不入镜：四条边条取代覆盖式红框

X11 与 Wayland 都**没有** `WDA_EXCLUDEFROMCAPTURE` 的等价物，
`IWindowEffects.SupportsCaptureExclusion` 在 Linux 上为 false。按用户拍板的
「录制期临时隐藏」思路，改成让控件根本不出现在录制区里：

- **红框**：不支持排除捕获时，拆成上下左右**四条实心边条**窗口，贴录制区外沿，
  一个像素也不覆盖录制区；支持排除捕获的平台（Windows）保持原来的覆盖式透明窗口。
- **控制条**：区域下方外侧优先 → 上方外侧 → 都放不下时，能排除捕获就翻进区域内
  （反正不入镜），不能排除捕获则**隐藏**并提示从托盘结束录制。

实测录制产物里红框色 `E53E3E`、控制条底 `282828`、按钮蓝 `2D8CF0` 均为 **0 像素**。

顺带解决了一个更隐蔽的问题：原覆盖式红框靠
`TransparencyLevelHint = Transparent` 让中间透出底下画面，**这依赖合成器**。
无合成器的 X11（Xvfb、轻量 WM）下透明失效，窗口渲染成**白色实心块把整个录制区盖死**——
录出来是一片白，而红框边线本身位置是对的，极易误判成"帧源抓错了区域"。
四条边条不依赖任何透明支持。

### 坐标模型：与 Windows 语义一致（已验证）

一度怀疑 Avalonia 在 Linux 上把 `Window.Position` 当 DIP 处理（GIF 帧差分区落在
区域内 (160,120)，而按设定算应是 (80,60)，正好差 2 倍）。实测证伪：

- `xwininfo` 报告窗口几何 `60x40+380+260`，与设定的物理像素**精确一致**；
- 录制区内实测亮区 x 80..139 / y 60..99，尺寸 60×40，与期望完全吻合。

那个"2 倍"是误判——`--test-record` 与 `--test-mp4` 两个测试钩子本来就用了**不同的
窗口位置公式**（`+460+320` vs `+380+260`），不是坐标语义问题。
**X11 的 root window 坐标即物理像素**，项目"虚拟屏物理像素为唯一真源"的约定原样成立。

### Wayland 会话（GNOME 50 / Ubuntu 26.04 实测）

| 能力 | 实现 | 验证结果 |
|------|------|----------|
| 抓屏 | `WaylandScreenCapture` = portal ScreenCast + PipeWire | 首次 22s（含人工点授权），**二次 2.1s 静默无框**；40 万种颜色、纯黑仅 112 像素（0%），两次抓屏差异 12% = 活画面 ✅ |
| 授权持久化 | `persist_mode=2` + restore_token | token 落盘 `~/.config/Zhuoying/wayland-restore-token`，二次静默恢复 ✅ |
| GIF 录屏 | `WaylandFrameSource`（复用同一 portal 会话，不再授权） | 28 帧 / 3023ms，Pillow 零错误，首末帧差异 38720 像素 ✅ |
| MP4 录屏 | 同上 + `FfmpegVideoEncoder` | 31 帧写入 / 32 帧解码，颜色序列红→绿→蓝→黄 ✅ |
| 剪贴板 | `LinuxClipboardImage` → wl-copy | `wl-paste --list-types` 报 image/png，取出 320×240 PNG ✅ |
| 显示器信息 | X11/XRandR 回退 | 1920×1080，workY=32 workHeight=1048（GNOME 顶栏被正确排除）✅ |
| 全局热键 | `PortalHotkeyService`（GlobalShortcuts v1） | 绑定成功并写入 dconf，按 Ctrl+2 触发正常 ✅ |
| 窗口枚举 | **Wayland 无此能力** | ⚠ 合成器不暴露全局窗口列表，窗口吸附自然降级（用户已确认接受） |

#### X11 路径在 Wayland 下会**崩溃**，不是抓到黑屏

对 Xwayland 的 root window 调 `XGetImage` 返回 `BadMatch`，而 **Xlib 的默认错误
处理器直接 `exit()`**——整个进程没了，连异常都抛不出来。虽然已按会话类型分流，
仍在 `X11Display` 里装了进程级的错误处理器兜底（错误处理器是进程级而非每
Display 一个，装一次即可）。修复后同样的调用优雅返回
`{"error":"XGetImage 抓取 … 失败"}`。

#### Wayland 全局热键：portal 要求 app id

`GlobalShortcuts.CreateSession` 会直接返回
`NotAllowed: An app id is required`。xdg-desktop-portal 从进程的 **systemd scope 名**
（`app-<appid>-<pid>.scope`）反推 app id，所以：

- **从终端直接运行的进程拿不到 app id，全局热键必然不可用**；
- 经 .desktop 启动（应用菜单、XDG autostart）或
  `systemd-run --user --scope --unit=app-zhuoying-N` 才有。

实测用 systemd scope 启动后 CreateSession 立即成功。为此加了 `install-linux.sh`
安装 `~/.local/share/applications/zhuoying.desktop`。

`BindShortcuts` **不需要任何用户交互**——GNOME 直接绑定，成功后写进 dconf：

```
[org/gnome/settings-daemon/global-shortcuts/zhuoying]
shortcuts=[('capture', {'shortcuts': <['<Control>2']>, 'description': <'截屏'>})]
```

但**首次调用会超时**：portal-gnome 把请求转给
`org.gnome.Settings.GlobalShortcutsProvider`，这是个 D-Bus 激活的服务
（要拉起 gnome-control-center 那侧），首次激活慢到超时；服务常驻后同一个调用
**0 秒返回**。`PortalHotkeyService` 因此在超时后自动重试一次。

排查时一度以为在等用户点确认框，白等了两轮——真正的判据是
`dbus-monitor` 看 portal-gnome 转发给谁，以及 dconf 里有没有落键。

注意 portal 的语义：`preferred_trigger` 只是**建议**，用户实际按哪个键由合成器
决定并在系统设置里管理。设置界面显示的键位因此可能与实际不符——这是平台差异不是 bug。

#### 抓屏耗时：2.4s → 0.30s

原本单次抓屏 2.1～2.4 秒，一度以为瓶颈是 portal 会话协商，打算靠"托盘启动即预热
会话"来省。**加计时一测，前提根本不成立**：

```
EnsureSession=33ms   gst启动=12ms   首帧=15728ms～57661ms(!)
```

会话协商只占 33ms，预热省不下任何东西。真正的开销全在等首帧，而且**会挂死**——
屏幕静止时首帧要等 15 秒起步，最坏 57 秒。三处修复：

1. **`fdsink sync=false`**（决定性，15700ms → 50ms）。`GstBaseSink` 默认
   `sync=true`，按 buffer 时间戳等到"该播放的时刻"才吐帧。抓屏要的是立刻拿到
   当前画面，这个同步语义纯属帮倒忙。
2. **不丢首帧**。原先照搬 Windows DDA 的教训（TROUBLESHOOTING §12：首帧可能
   未播种、须丢弃重取）丢一帧再读，但 PipeWire 语义正相反——合成器在流建立时
   就推一帧当前内容，之后**只在画面变化时才推新帧**。静止桌面上多要一帧就是
   无限期干等。
3. **`Kill()` 而非 `Kill(entireProcessTree: true)`**（Dispose 2000ms → 0ms）。
   后者在 Linux 上要扫 `/proc` 重建整棵进程树，耗时抖动到 2 秒；gst-launch
   不 fork 子进程，杀它自己就够。原来还先 `WaitForExit(2000)` 等它优雅退出，
   这条管道是一次性的，直接杀。

修复后稳定 **0.30～0.32 秒**（5 次测量无抖动），分解：

| 阶段 | 耗时 |
|------|------|
| 进程冷启动 | ~70ms |
| EnsureSession（已有 token） | 33ms |
| gst-launch 启动 | 11ms |
| 首帧 | 49ms |
| BlitRegion（1920×1080） | 10ms |
| PNG 编码 + 退出 | ~130ms |

这是 **CLI 单次进程**的数字。产品路径上托盘常驻、会话已缓存、无需 PNG 编码，
实际热键到画面 ≈ **70ms**（gst 启动 + 首帧 + blit）。

#### 为什么是 ScreenCast 而不是 Screenshot

`org.freedesktop.portal.Screenshot` 简单得多（一次调用返回 PNG 文件，无 PipeWire），
但**每次调用都弹授权框**——实测发起后 25 秒无人点击就一直挂着，portal 还会被这个
未决请求阻塞（后续任何 portal 调用一并超时，须
`systemctl --user restart xdg-desktop-portal-gnome.service` 才恢复）。
热键截屏每次弹框等于不可用。

`ScreenCast` 配 `persist_mode=2` 只在首次弹一次框，之后凭 `restore_token` 静默恢复，
且截屏与录屏能共用同一条会话。代价是要引入 PipeWire 帧读取。

#### 实现注记

- **先订阅信号再发起调用**。portal 的方法只返回一个 request 对象路径，真正的结果
  经该路径上的 `Response` 信号送达。顺序颠倒会漏掉快速返回的响应（有 restore_token
  时的免授权 Start 就是这种），表现为永远等不到回应——原型阶段实测踩过。
- **`restore_token` 存 `~/.config/Zhuoying/wayland-restore-token`**，是本机凭据不是
  用户配置，故不进 settings.json（同 Windows 侧"注册表即持久态"的取舍）。
- **`ObjectDisposedException` 不是 `IOException`**。`FfmpegVideoEncoder` 早期版本用
  一个 `_finished` 标志混用了"管道已断"与"已收尾"两种含义，`Dispose()→Finish()`
  第二次进入时对已 Dispose 的 stdin 调 `Flush()`，抛出的 `ObjectDisposedException`
  没被 `catch (IOException)` 接住，异常从 `Mp4Recorder.Stop()` 传出去打断了 UI 线程
  回调，整个录制流程挂死、MP4 文件明明已经写好却永不收尾。现已拆成
  `_inputClosed` 与 `_finished` 两个标志。

## NativeAOT（已验证）

```bash
sudo apt install clang zlib1g-dev
./build-linux.sh aot          # → publish/aot-linux
```

产物：`Zhuoying` 32MB + `libSkiaSharp.so` 11MB + `libHarfBuzzSharp.so` 2.8MB
≈ **46MB**（`Zhuoying.dbg` 77MB 是符号，不分发）。对比自包含发布 105MB。
唯一警告是已知无害的 `Svg.Model` IL2104 裁剪警告（与 Windows 侧一致）。

**15 项回归全绿**，重点覆盖 AOT 最容易出事的地方：

| 项 | 结果 |
|---|---|
| X11 抓屏 | 与 `ffmpeg x11grab` 逐字节一致 |
| Wayland 抓屏 | 0.22s，唯一色 228669 |
| **SVG 光栅化图章**（裁剪风险最高） | 差异 1162 像素，**与 JIT 版逐值一致** |
| 文字/中文字体 | 唯一色 483、红系 893 |
| 剪贴板（xclip / wl-copy） | X11 120000px、Wayland 76800px |
| JSON 源生成配置 | 读写正常 |
| 设置窗口 + ColorPicker 主题 | 唯一色 3763（JIT 版 3755） |
| GIF 录屏（手写 LZW） | Pillow 严格校验通过 |
| MP4 录屏 + 解码回读 | X11 21 帧 / Wayland 22 帧 |
| **X11 全局热键**（XGrabKey + UnmanagedCallersOnly） | 注入 Ctrl+2 遮罩弹出 |
| **Wayland 热键绑定**（GDBus + UnmanagedCallersOnly 信号订阅） | 绑定成功、dconf 落键 |

手写的三套互操作在 AOT 下都没问题：X11/GLib 的 `LibraryImport` P/Invoke、
`UnmanagedCallersOnly` 回调（X 错误处理器、portal Response 信号、热键 Activated）、
以及经子进程的 gst-launch / ffmpeg 管道。

## 已知平台差异与待决策

1. **Wayland 抓屏必须走 portal**。GNOME 下 X11 抓屏对合成器内容全黑，
   `org.gnome.Shell.Screenshot` 的 D-Bus 接口对普通客户端返回
   `AccessDenied`（GNOME 45+ 收紧），只剩 `xdg-desktop-portal`：
   - `org.freedesktop.portal.Screenshot` —— 每次弹授权框，交互式，不适合热键流；
   - `org.freedesktop.portal.ScreenCast` + PipeWire —— 一次授权可持久
     （`persist_mode`），适合截屏与录屏共用一条帧源。
   倾向 ScreenCast，需要引入 PipeWire 互操作（纯 P/Invoke，AOT 兼容）。
2. **Wayland 无全局坐标概念**。"虚拟屏物理像素为唯一真源"这条核心约定
   在 Wayland 下需要重新定义（多输出各自坐标系）。单输出场景可先按
   ScreenCast 给的 stream 尺寸建立等价坐标系。
3. **Wayland 全局热键**走 portal `GlobalShortcuts`（本机 v1 可用），
   与 X11 的 XGrabKey 是两条完全不同的路。
4. **录制排除自身**在 X11 上无解。控制条/红框会入镜，需要上层策略
   （录制期把控件挪出录制区，或临时隐藏）。Wayland 的 ScreenCast
   可以只共享指定窗口/区域，反而更好办。
5. **`Topmost` 在 Wayland 下不可靠**（钉屏贴图受影响，layer-shell 需扩展协议）。
6. **单实例 Mutex**：`Local\Zhuoying.SingleInstance` 这个名字在 Linux 上
   由 .NET 用文件系统模拟，实测未报错，但跨发行版行为需再确认。

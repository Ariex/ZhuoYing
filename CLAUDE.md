# CLAUDE.md

本文件为 Claude Code 在本仓库工作时的指导。**所有输出使用中文。**

## 项目概述

**捉影（Zhuoying）**：Windows 桌面截屏标注工具——全局热键呼出 → 冻结虚拟屏幕 → 框选区域 →
原位矢量标注（9 种工具）→ 输出（剪贴板 / PNG 文件 / 钉屏贴图 / GIF / MP4 录屏）。
交互体验对标 Snipaste / 微信截图。当前版本 **0.18**，REQUIREMENTS v1 主体功能已全部落地
（仅"聚光灯"暂缓），下一大方向是 **Linux 移植**。

- 技术栈：Avalonia 11.3 + .NET 10，NativeAOT 发布（这是硬约束，见下）
- 混合 DPI 多屏是**一级支持场景**（不是边缘情况），坐标一律以"虚拟屏幕物理像素"为唯一真源

## 文档地图（决策与需求的权威来源）

| 文件 | 内容 | 维护规则 |
|------|------|----------|
| `REQUIREMENTS.md` | 需求规格 v1（范围、明确不做的事） | 需求变更时更新 |
| `TOOLS-SPEC.md` | 每个标注工具的完整行为定义（含"替用户拍板的细节"清单） | 工具行为定稿后回写"已按 X.Y 实现定稿" |
| `DEVPLAN.md` | 阶段进度（阶段一~十七全勾）+ 每阶段实现要点与验证记录 | 每完成一个阶段追加一节 |
| `CHANGELOG.md` | 按里程碑的变更记录 | 每个里程碑一节 |
| `docs/TROUBLESHOOTING.md` | **疑难问题根因记录（§1–§12）**——最有价值的文档，改相关代码前必读 | 新踩的坑（有排查过程、有根因）追加一节 |

读代码前先读 DEVPLAN 对应阶段一节，实现细节和"为什么这样做"大多写在那里。

## 协作与工作流约定（用户明确要求）

- **git 提交 / 推送 / 打 tag 全部由用户自己操作，不要代劳**。用户的节奏是：本地多次构建
  测试，满意后才提交。目前本地 main 与 tag v0.9~v0.18 均未推送远端。
- 版本号单一来源 `Directory.Build.props`：`主.次`=里程碑（手动改），文件版本三四段=
  构建时间戳（距 2026-01-01 天数.当日分钟，自动）。**不用 git 提交数做版本号**（用户否决过）。
- 根目录 `.bat` 必须 **GBK 编码 + CRLF**：UTF-8（无论是否 CHCP 65001）或 LF 行尾都会让
  cmd 按字节错位解析（症状：`'otnet' 不是命令`）。用脚本写入时用
  `[IO.File]::WriteAllText` + Encoding 936 并把 `\n` 换成 `\r\n`。
- 界面仅中文；不做多语言（v1 范围外，见 REQUIREMENTS §12）。

## 构建 / 运行 / 发布

```powershell
dotnet build src\Zhuoying\Zhuoying.csproj
dotnet run --project src\Zhuoying
```

发布（根目录脚本，产物进 `publish/`）：

- `aot-win.bat` — NativeAOT，exe ≈19MB + 3 原生库。前置：VS「使用 C++ 的桌面开发」。
  ILCompiler 的 vcvarsall 探测在 VS 2026 上不可靠，脚本内用 vcvars64 +
  `-p:IlcUseEnvironmentalTools=true` 绕过；注意 vcvars 环境会让输出走 `bin\x64\Release`。
- `release-singlefile-win.bat` — 自包含单文件 ≈46MB。

发布后必须用 `--test-copy` / `--test-shape` 等参数对**产物**做 AOT 回归
（重点：剪贴板、文字/字体、ColorPicker 主题、JSON 配置、SVG 光栅化）。

### NativeAOT 是硬约束

全项目必须 AOT 兼容，新代码遵守：

- **禁用内置 COM interop**：DXGI/D3D11（`DesktopDuplication.cs`）与 Media Foundation
  （`MediaFoundation.cs`）的 COM 调用全部是手写 vtable 函数指针（`delegate* unmanaged`），
  新增 COM 调用照此办理；将来若做 OLE 拖拽需 `[GeneratedComInterface]`。
- **JSON 一律源生成**：`SettingsJsonContext`；新增配置文件类型要加一行 `[JsonSerializable]`。
  注意源生成对 `init` 属性**缺字段 = default(T) 而非初始化器值**——配置文件始终全字段写出，
  手改配置文件不要删字段。
- 无反射、无动态代码生成。已知无害警告：Svg.Model 有 IL2104 裁剪警告但 SVG 光栅化实测正常。

## 架构

```
src/Zhuoying/
  Program.cs             入口：--api-*/--test-* 分流在单实例 Mutex 之前；单实例 + 托盘驻留
  Platform/              平台抽象接口：IHotkeyService / IScreenCapture / IClipboardImage
  Platform/Windows/      Windows 实现：Win32.cs（P/Invoke 集中地）、WindowsScreenCapture
                         （DDA 为主 + BitBlt 按矩形回退）、DesktopDuplication（手写 DXGI vtable）、
                         MediaFoundation（手写 MF vtable）、WindowsClipboardImage（纯 Win32
                         CF_DIB）、WindowsHotkeyService（专用消息线程 + RegisterHotKey）、
                         StartupManager（HKCU Run 键）
  Capture/               截屏会话：CaptureController（会话生命周期）→ CaptureOverlayWindow
                         （单一遮罩窗口）→ 分层控件；SelectionController（选区状态机）、
                         EditorState（编辑器状态/工具/样式）、EditorToolbar、
                         RecordingController + RegionFrameSource + GifRecorder/Mp4Recorder（录屏）、
                         PinWindow（钉屏贴图）、StampLibrary/StampRasterizer、StyleMemory
  Annotations/           标注模型：元素类层级 + 命令式撤销/重做（AnnotationModel）
  Agent/                 Agent API：AgentCli（--api-* 无头 CLI）、McpServer（协议层，传输无关）、
                         McpHttpServer（托盘实例内置 HTTP）、MiniPng（零依赖 PNG 编码器）
  Settings/              AppSettings + SettingsService（settings.json）+ PresetsService
                         （presets.json）+ SettingsWindow
```

### 渲染分层（自下而上）

冻结帧 Image → AnnotationLayer（元素渲染，选区外随遮罩暗化）→ SelectionLayer（纯渲染：
暗化/选区框/手柄）→ EditorLayer（统一输入路由：元素手柄 → 元素本体 → 选区交互；放大镜）。
文字就地编辑 = 叠加真实 TextBox（TextEditController，IME 原生支持，旋转态 RenderTransform
同角度，编辑中屏蔽全局快捷键）。

### 标注模型类层级

```
AnnotationElement（Render / HitTest / CaptureState-RestoreState 通用快照）
├── BoxedElement（包围盒 + 绕中心旋转；移动/8 手柄缩放/旋转手柄共用逻辑）
│   ├── ShapeElement（矩形，圆角 0-100%，100%=精确椭圆）
│   ├── TextElement / MosaicElement / StampElement
├── LineElement（N 控制点折线；箭头=两点+末端头；11 种端头；不等粗=渐变带；Catmull-Rom 弧线）
├── PenElement（自由笔迹；荧光=采样冻结帧×色正片叠底+带状几何裁剪）
├── NumberElement（圆心+值+样式；无缩放旋转；每类型独立序列存 EditorState，不入撤销栈）
└── EraserElement（不渲染自身；BuildClip 反向裁剪只套在 PenElement 上）
```

撤销/重做：命令模式，创建/删除/移动/缩放/旋转/属性修改/层序全部入栈；滑条拖动合并单条命令。
样式跨会话记忆：StyleMemory 静态类（EditorState 构造读 / RaiseStyleChanged 写回），落盘 presets.json。

## 关键技术决策（含理由，改动前务必了解）

1. **单一遮罩窗口覆盖整个虚拟屏幕**（ShareX 方案）。曾走过"每屏一个窗口"弯路：Avalonia 11.3
   窗口创建后跨 DPI 移屏，渲染缩放与输入命中缩放**永久分裂**（按钮点不中），应用层无法修正
   （TROUBLESHOOTING §7/§8/§10）。物理↔DIP 一律手工换算：`DIP = (物理 − 显示器原点) ÷
   RenderScaling`；另有投递式几何收敛校验环（PostGeometryCheck）防御。
2. **剪贴板只写 CF_DIB，绝不同时写 "PNG" 自定义格式**——否则 Win11 画图粘贴按 DPI 减半
   （§1，排查最曲折的一个）。DIB 头写来源屏真实 ppm（200% → 7559）。
3. **UI 显示用的位图必须 96 DPI**（Avalonia Image 对非 96 DPI 位图渲染错误，只显示左上 1/4
   放大，§2；PinWindow 阶段复发过一次）。输出位图在裁剪时才标记真实 DPI。
4. **DDA 抓屏只接受 LastPresentTime≠0 的帧**：新会话首帧可能是"未播种"仅指针帧，S_OK 但
   纹理全黑，且时序相关（桌面有合成活动时恰好正常，极具迷惑性，§12）。静止桌面超时回退
   BitBlt 即正确。duplication 会话即建即弃；单输出失败仅该矩形回退 BitBlt。
5. **元素内部零重叠原则**（§11）：`PushOpacity` 按图元逐个混合，半透明元素内图元重叠会变深，
   Avalonia 无带边界的层重载。解法全部是几何消重叠（填充内缩半线宽、端头精确让位、渐变箭头
   头身融合单多边形、锐角折点 bevel）。**新元素类型渲染时必须遵守此原则。**
6. **GIF 手写流式 GIF89a 编码器**（GifWriter：帧差分+八叉树+LZW），弃 ImageSharp（动画 GIF
   需全帧驻留内存 + 许可条款）；相同帧合并时长，内存 O(1)。
7. **MP4 = 手写 Media Foundation 互操作**。坑：RGB32 输入必须正 stride 声明顶朝下否则整帧
   翻转；SourceReader 解码 RGB32 需 ENABLE_VIDEO_PROCESSING；同步 ReadSample 在 STA UI
   线程死锁（放 MTA 后台线程）。
8. **MCP 内置 HTTP 而非 stdio**（v0.18 用户拍板）：stdio 需客户端拉起独立进程，与托盘常驻
   单实例冲突。手写 TcpListener HTTP/1.1（HttpListener 走 http.sys 有非管理员 URL ACL 门槛），
   仅绑 127.0.0.1:8990（McpPort 可改），Origin 校验防 DNS rebinding。协议层 McpProtocol
   与传输解耦。**AgentApiEnabled 与 MCP 开关默认全关**；只"看"不提供输入注入。
9. **反色颜色 = InvertPaint.Sentinel**（alpha=1 黑哨兵色），仅形状+画笔支持，对底图逐像素取反。
10. **橡皮只擦画笔/荧光笔**（用户定稿，否决了早期"注释组遮罩"草案）——EraserElement 汇总
    橡皮带做反向 GeometryClip，只套在 PenElement 渲染上（AnnotationLayer 与输出合成两处）。
11. **区域模糊永远在最底层**（模型列表头部"模糊前缀组"），只采样冻结帧；抗逆向 = 跨块像素
    交换（块内乱序不改均值，必须跨块才能抗 Depix）+ 模糊后 ±2 确定性噪声（抗反卷积），
    种子存元素保证重绘稳定。
12. 交互约定（用户定）：Ctrl+角手柄 = 等比缩放；图章/编号/画笔/折线画完**不**回选择工具
    （连续创建），形状/箭头/文字/区域模糊画完回选择工具；右键 = 会话被改动时整体重置、
    初始态才退出（微信截图式）；Esc 逐级退出；Alt+点击下钻重叠元素。

## Avalonia 已知陷阱（跨平台通用，Linux 上同样适用）

- 窗口 `Opened` 时 Bounds 还是布局前默认尺寸——工具条定位一律用虚拟屏尺寸/RenderScaling
  换算，不读窗口 Bounds。
- 子孙控件 IsVisible 刚改完对祖先 `Measure(Infinity)` 会被"measure 仍有效"短路——行显隐后
  的重摆必须 `Dispatcher.Post(DispatcherPriority.Loaded)`。
- 未挂树的模板控件（Popup 未开时的 Button/ColorView）Measure≈0——弹层高度估算要传最小
  高度兜底（PopupPlacement.Adjust）；屏底翻转判定用 PointToScreen + Screens.ScreenFromPoint。
- 初始 IsVisible=false 的面板首次显示当帧 Bounds=0——跳过钳位或 OnSizeChanged 再刷新
  （NumberActionsPanel 两者都做）。
- 悬浮弹层不能用 Flyout + IsPointerOver 轮询（拖拽捕获时失真）：自管理 Popup + 300ms 看门狗，
  且**弹层内按钮不能挂 ToolTip**（气泡使 IsPointerOver 失真导致误关）；嵌套弹层用
  ColorPickButton.AnyOpen 防外层看门狗误关。
- Popup 承载 ColorView 会裁掉左列——已改为可拖拽置顶浮窗；其 Deactivated 关闭必须 gate 在
  "真正激活过之后"，否则浮窗开了就自杀。
- 命中测试基于实际绘制内容：全透明区域不可命中，需铺 `Brushes.Transparent` 矩形（§3）。
- UI 风格约定：滑条用自绘 MiniSlider（细轨道小圆钮，数值只显示）；选项类控件用横向
  子工具条式面板（PaletteButton），不用 ComboBox；虚线段圆头。

## 自测参数体系（免键鼠注入，开发验证的主要手段）

启动参数在 Program.cs 分流。完整语法见 README「构建与运行」一节，速查：

- 基础：`--test-capture`（1.5s 自动抓屏）、`--test-settings`、`--test-copy x,y,w,h`、
  `--test-select x,y,w,h`、`--test-save`、`--test-pin`（坐标一律虚拟屏物理像素）
- 标注（2.2s 注入，可与 --test-copy 组合验证输出合成）：`--test-shape` / `--test-line` /
  `--test-text` / `--test-number` / `--test-mosaic`（固定种子 12345 可复现）/ `--test-pen` /
  `--test-stamp` / `--test-eraser`（2.6s，晚于画笔）
- 后端：`--test-dda`（DDA 与 BitBlt 双路 diff）、`--test-record`（GIF 核心）、`--test-mp4`
  （MP4 + 解码回读）、`--test-record-ui`（完整录制流程）
- 时序约定：1.5s 抓屏、2.2s 加标注、3s 执行动作，脚本等 5–6s 再检查

验证脚本注意事项：

- **脚本自身必须 PMv2 感知**：`SetProcessDpiAwarenessContext(-4)`。用 `SetProcessDPIAware()`
  在混合 DPI 下坐标被虚拟化，测量结果全部失真（§9，曾因此误判应用有 bug）。
- pwsh + System.Drawing 截屏（.NET 10）：`Add-Type` 需
  `-ReferencedAssemblies System.Drawing.Common,System.Drawing.Primitives,System.Private.Windows.Core,System.Private.Windows.GdiPlus`。
- 剪贴板验证：读 CF_DIB 头取宽高/ppm 比截图肉眼比对可靠；`IsClipboardFormatAvailable` 偶发
  误报，以 `EnumClipboardFormats` 为准；连续多轮"启动→拷贝→读"时每轮读取前先
  EmptyClipboard（第二轮拷贝偶发失败会静默读到旧内容）。
- 像素采样验证：避开选中元素的 8 个白色手柄位置、偏离包围盒中线 2–3px（虚线框会混色）；
  50% 透明红在黑底 R≈134，别用 R>200 判"红"。
- **自动化验证渲染 ≠ 验证输入**：截图比对只覆盖渲染路径，输入命中必须注入点击单独验证
  （DPI 异常时两者会分裂，§10）。
- 单实例 Mutex：重测前先 `Stop-Process -Name Zhuoying`。

### 本机开发环境（用户机器特有）

- 双 4K 显示器，当前**均为 200%** 缩放（历史上副屏曾 150%，混合 DPI 场景靠改副屏缩放复现）；
  虚拟桌面 7680×2160 无缝拼接。编译机时区 UTC+10。
- 用户常驻 **PixPin**，占用 Ctrl+1（捉影本机热键已设 Ctrl+2）；**不允许 kill 它的进程**；
  其截屏会话的全局鼠标钩子会吞掉注入的 mouse_event，注入右键两次可取消其卡住的会话。
- 键鼠注入测试只能在用户明示"电脑未使用"时做（曾干扰用户操作）；--test-* 系列无注入、
  只有约 2 秒遮罩闪现，可随时用。
- 本机有一个外部输入法/语言栏式白色小悬浮窗（[≡][▭] 两图标，topmost、会自己移动）——
  截图验证时会混进画面，别误判成应用弹层。

## Linux 移植指引（下一大方向）

### 现状：平台边界在哪里

P/Invoke **声明**全部集中在 `Platform/Windows/`（Win32.cs / DesktopDuplication.cs /
MediaFoundation.cs），但 `Win32.*` 的**调用**有泄漏到上层的点，移植时需先把这些收敛回接口：

| 泄漏点 | Windows 依赖 | 说明 |
|--------|--------------|------|
| `Capture/RegionFrameSource.cs` | BitBlt + DrawIconEx 光标补绘 | 录屏帧源，GIF/MP4 共用；需抽 IFrameSource |
| `Capture/RecordingController.cs` | WDA_EXCLUDEFROMCAPTURE、WS_EX_TRANSPARENT | 录制红框/控制条不入镜 + 点击穿透 |
| `Capture/GifRecorder.cs` | （经 RegionFrameSource） | 编码器本身纯托管 |
| `Capture/Mp4Recorder.cs` | Media Foundation SinkWriter | Linux 需换编码方案 |
| `App.axaml.cs` | 少量 Win32 调用 | 移植时逐个核对 |
| `Agent/AgentCli.cs` | AttachConsole | WinExe 无控制台的补偿，Linux 不需要 |
| `Platform/Windows/StartupManager.cs` | HKCU Run 注册表键 | Linux → XDG autostart .desktop |

**纯托管、可直接复用**：`Annotations/` 全部（矢量模型与渲染，注意零重叠原则依赖的是
Avalonia 行为，跨平台一致）、`GifWriter`、`MiniPng`、`McpServer/McpHttpServer`（TcpListener
跨平台）、`Settings/`（除 StartupManager；路径 `%AppData%\Zhuoying` 需换
`XDG_CONFIG_HOME`）、`StampLibrary/StampRasterizer`（Svg.Skia/SkiaSharp 跨平台）、
工具栏与编辑器 UI 层。

### 需要 Linux 等价物的能力清单

| 能力 | Windows 实现 | Linux 注意点 |
|------|--------------|--------------|
| 抓屏 | DDA + BitBlt 回退 | X11（XShm/XGetImage）与 Wayland（xdg-desktop-portal + PipeWire，需用户授权）路径完全不同；Wayland 无全局坐标概念 |
| 全局热键 | RegisterHotKey 专用消息线程 | X11 XGrabKey；Wayland 无全局热键协议（需 portal GlobalShortcuts 或桌面环境特定方案） |
| 剪贴板图片 | 纯 Win32 CF_DIB + DPI 头 | X11/Wayland 走 image/png MIME；"只写 CF_DIB"决策是 Win11 画图特有坑，Linux 不适用，但 DPI 元数据策略需重新验证目标应用 |
| 托盘 | Avalonia TrayIcon | Linux 走 StatusNotifierItem/AppIndicator，桌面环境差异大 |
| 录屏排除自身 | WDA_EXCLUDEFROMCAPTURE | 无直接等价物；portal 捕获可选择性共享 |
| 点击穿透窗口 | WS_EX_TRANSPARENT | X11 shape input region；Wayland input region |
| 置顶贴图 | Avalonia Topmost | Wayland 下 Topmost 不可靠（layer-shell 需扩展协议） |
| MP4 编码 | Media Foundation | 候选：ffmpeg 调用、VAAPI、或纯托管编码器；GIF 路径纯托管可直接用 |
| 开机自启 | HKCU Run | XDG autostart |
| 单实例 | 命名 Mutex | 命名 Mutex 在 Linux 上 .NET 有实现差异，需验证或换 socket/文件锁 |

### 移植策略建议

1. 先做接口收敛重构（把上表泄漏点抽回 `Platform/` 接口），Windows 行为不变、AOT 回归通过，
   作为独立里程碑；
2. X11 先行（能力完整、可自动化测试），Wayland 的 portal 授权流程与全局热键限制单独评估；
3. 混合 DPI 模型不同：Linux/X11 多为统一缩放，Wayland 每输出缩放但坐标模型与 Windows
   虚拟屏不同——"虚拟屏物理像素为唯一真源"的坐标系假设需重新审视；
4. Avalonia 陷阱一节（Measure 时序、Popup、命中测试等）是跨平台的，照常遵守；
   §7/§8/§10 的跨 DPI 移屏分裂是否在 Linux 复现未知，单窗口架构本身建议保留。

## 剩余工作（v1.0 前）

- 聚光灯工具（暂缓中，采样取反基建已具备）
- 录屏增强：帧源升级 DDA 持久会话或 WGC；帧率/含光标做成设置项；录音并入 MP4
- MCP 扩展工具：pin_image（需与托盘实例 IPC）、record_gif/mp4
- 升 1.0 由用户拍板

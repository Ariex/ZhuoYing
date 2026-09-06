# 捉影（Zhuoying）

跨平台桌面截屏标注工具（Windows / Linux）：全局热键呼出 → 框选区域 → 原位标注 →
输出（剪贴板 / 文件 / 贴图 / GIF / MP4 录屏）。交互体验对标 Snipaste / 微信截图。
当前版本 **0.19**，需求 v1 主体功能已全部落地（仅"聚光灯"暂缓）；
Linux 支持 X11 与 Wayland 两种会话（见 [docs/LINUX-PORT.md](docs/LINUX-PORT.md)）。

## 功能一览

- **全局热键截屏**：默认 `Ctrl+1`（可在设置中改键），触发瞬间冻结**整个虚拟屏幕**；
  Windows 抓屏走 DXGI Desktop Duplication（独占全屏游戏 / 视频硬件叠加层 / HDR 桌面
  均正确），远程桌面等场景自动按显示器回退 GDI BitBlt；Linux 按会话类型分流——
  X11 走 XGetImage，Wayland 走 xdg-desktop-portal ScreenCast + PipeWire（首次授权
  一次后静默）
- **跨屏抓取**：所有显示器同时进入截屏态，默认选区为鼠标所在屏全屏；
  选区可跨显示器拖拽新建、移动、8 手柄调整，混合缩放（如 200% + 150%）下
  按物理像素拼接，像素级准确
- **拖拽框选**：实时显示物理像素尺寸；选区外按下即扩展选区到该点
- **窗口吸附**：截屏开始后移动鼠标自动高亮所指窗口（绿色框），点击即选中
  该窗口区域；拖动则照常手动框选（Wayland 合成器不暴露窗口列表，此功能在
  Wayland 下自动降级为纯手动框选）
- **右键重新开始**：已框选/已标注时右键 = 整体重置重新捕捉（微信截图式），
  初始状态再右键才退出
- **复制到剪贴板**：选区右下角工具条按钮 / `Enter` / `Ctrl+C` / 双击选区；
  输出为 100% 物理分辨率并携带来源屏幕 DPI（Windows 写 CF_DIB，Linux 写
  PNG + pHYs），高分屏粘贴不缩小、不模糊
- **保存 / 另存为**：`Ctrl+S` 自动存 PNG 到设定目录（默认 `图片\捉影`，
  设置可改）；`Ctrl+Shift+S` 弹出对话框自选目录与文件名；PNG 携带来源 DPI
- **录制 GIF / MP4**（`F4` GIF / `F5` MP4，或工具条 ⏺ / 🎬）：框选区域后
  活屏录制（24fps、含光标），红框与控制条不入镜（Linux 无排除入镜能力，
  红框改贴区域外沿、控制条只停区域外），完成存到设定目录；
  GIF 手写流式编码（相同画面合并时长、内存与时长无关）随处可贴自动循环，
  MP4 全彩且体积小一个数量级、适合长录制（Windows 走 Media Foundation
  H.264 优先硬编，Linux 走 ffmpeg）
- **贴图到屏幕**（`F3`）：选区连同标注钉成置顶小窗（原位、像素 1:1）；
  拖动移动、滚轮缩放、Ctrl+滚轮调透明度、双击/中键关闭，右键菜单可复制/
  另存/关闭全部；多张贴图共存无上限
- **标注：形状工具**（快捷键 S）：矩形，圆角 0–100% 可调（100% = 椭圆）+ 旋转，
  8 缩放手柄 + 4 圆角手柄 + 旋转手柄；颜色（预设可改）/ 填充 / 线形（5 种，易扩充）/
  粗细（1–100px）/ 透明度；撤销/重做（Ctrl+Z / Ctrl+Y）；复制时标注按物理像素合成到输出
- **标注：箭头 / 折线工具**（A / L）：箭头拖拽创建（默认起细尾粗 + 三角头）；
  折线逐次点击、双击结束；起/末端头样式 11 种（实心/空心三角菱形圆方块、
  双线箭头、破甲箭头）、两端粗细独立（不等粗 = 渐变带）、弧线（样条）开关
- **标注：文字工具**（T）：矩形文本框（多行、换行、对齐、内边距、外框、描边、
  旋转），就地编辑原生支持中文输入法；点内部编辑、点边框拖动
- **标注：编号工具**（N）：点击放置圆形徽章，标号自动递增（可连续盖章）；
  实心（标号自动黑/白反差色）/ 空心两种形制；标号类型 123/ABC/abc/罗马/汉字
  六种、各自独立序列；徽章大小恒定、文字自动缩小适配；选中后四角按钮
  增减编号 / 删除（不重排）/ 重置本序列
- **标注：区域模糊**（M 像素化 / B 模糊化）：拖拽框定区域对底图打马赛克
  （块 1–50px）或高斯式模糊（半径 1–50px）；永远在所有标注之下、只作用底图；
  内置抗逆向处理（跨块像素交换 / 抗反卷积噪声）
- **标注：画笔**（P）：自由描线（圆头圆拐角、连续绘制）；颜色 / 粗细 /
  **荧光**开关——荧光笔与底图正片叠底，划过黑字字仍是黑的、白底变色
- **标注：图章**（I）：素材图片（SVG/PNG）点击贴入截图，内置 ✓✗⭐⚠❤→ 六个
  素材、可导入自定义（`%AppData%\Zhuoying\stamps\` 目录即库，Linux 为
  `~/.config/Zhuoying/stamps/`）；SVG 任意缩放
  不糊；透明度 + 沿轮廓描边（非矩形框）；透明区点击穿透
- **标注：橡皮**（E）：只擦画笔/荧光笔笔迹（其余标注与底图不受影响），
  拖拽涂抹实时生效，仅撤销可恢复；画笔/橡皮为实时圆形光标（直径=粗细）
- **反色颜色**：形状/画笔色板的黑白特殊块——对底图逐像素取反，
  跨明暗背景永远清晰；Alt+点击可在重叠元素间向下层循环选择
- **放大镜**：框选时跟随光标的像素放大镜（网格 + 坐标 + 颜色值，C 键复制颜色），
  空间不足自动换边
- **托盘驻留**：无主窗口，托盘菜单提供 截屏 / 设置 / 退出；单实例运行
- **设置**：截屏快捷键、保存目录、开机自启、标注预设颜色（持久化到
  `%AppData%\Zhuoying\settings.json`，Linux 为 `~/.config/Zhuoying/`）；
  各工具上次使用的样式自动记忆并落盘（`presets.json`，重启保持）；
  保存成败与热键冲突有右下角气泡提示
- **DPI**：Per-Monitor DPI Aware v2，混合缩放多屏为一级支持场景

需求 v1 的完整范围见 [REQUIREMENTS.md](REQUIREMENTS.md) 与
[TOOLS-SPEC.md](TOOLS-SPEC.md)，逐阶段实现记录见 [DEVPLAN.md](DEVPLAN.md)。

## 已知限制

- **Wayland 抓屏目前只取一路流（单屏）**：portal ScreenCast 以 `multiple:false`
  建会话，多屏需改多流合并——开发环境为单屏，无条件验证，暂不支持
  （X11 与 Windows 的多屏 / 混合 DPI 均完整支持）
- 聚光灯工具暂缓（采样取反基建已具备）
- X11 的窗口吸附依赖 EWMH 窗口枚举，未在带窗口管理器的真实桌面验证过

## Agent API（AI Agent 获取屏幕信息）

两种通道让 AI Agent（如 Claude）获取屏幕信息，各有独立开关、默认全关；
坐标一律为虚拟屏幕物理像素，混合 DPI 多屏下准确；只提供"看"（截图/窗口/
显示器信息），不提供输入注入。

**命令行**（设置勾选「允许 Agent API」）：第二进程即用即退，与托盘实例互不干扰。

```powershell
Zhuoying.exe --api-monitors                        # 显示器拓扑 JSON（边界/缩放比/主屏）
Zhuoying.exe --api-windows                         # 可见窗口 JSON（标题+矩形，Z 序）
Zhuoying.exe --api-capture "100,100,800,600" --out shot.png   # 区域截图（也可用 full）
```

**MCP 服务**（设置勾选「启用 MCP 服务」，即时启停无需重启）：托盘常驻实例
内置本机 HTTP 服务器（仅 127.0.0.1，端口默认 8990、settings.json `McpPort`
可改）。Claude Code 项目 `.mcp.json` 一行接入：

```json
{ "mcpServers": { "zhuoying": {
    "type": "http", "url": "http://127.0.0.1:8990/mcp" } } }
```

工具：`take_screenshot`（region / monitor / window_title 三选一定位）、
`list_windows`、`get_monitors`。

## 版本号

版本号单一来源为根目录 `Directory.Build.props`，规则：

- **`主.次` = 里程碑**（手动维护）：`0.1` 阶段一，`0.2` 跨屏抓取，…，`1.0` = 需求 v1 范围全部落地；每个里程碑打对应 git tag（如 `v0.1`），变更记录见 [CHANGELOG.md](CHANGELOG.md)
- **文件版本第三、四段 = 构建时间戳**（构建时自动生成）：`距 2026-01-01 的天数 . 当日分钟数`，如 `0.1.205.872` = 2026-07-25 14:32 构建，精确到分钟且不受 PE 版本资源每段 65535 上限影响
- **产品版本**（exe 属性 / 界面显示）：`主.次+本地时间及时区偏移`，如 `0.1+2026-07-25 14:32+1000`
- AssemblyVersion 保持 `主.次.0.0` 稳定；本应用非库，不采用 SemVer 三段语义

## 技术栈

- [Avalonia](https://avaloniaui.net/) 11.3 + .NET 10；全项目 NativeAOT 兼容（硬约束）
- 支持平台：Windows（Win10 1903+ / Win11）与 Linux（X11 + Wayland，
  同一份构建按会话类型自动分流）
- 平台能力（抓屏 / 全局热键 / 剪贴板 / 录屏帧源 / 视频编码等）收敛在 `Platform/`
  接口层，经 `PlatformServices` 统一取用；`Platform/Windows` 与 `Platform/Linux`
  在 csproj 里互斥编译。Windows 后端以 P/Invoke 实现（DXGI Desktop Duplication +
  GDI BitBlt 回退、RegisterHotKey、Win32 剪贴板、Media Foundation），DXGI/MF 的
  COM 调用为手写 vtable 函数指针；Linux 后端为手写 Xlib / GDBus 互操作
  （portal ScreenCast、GlobalShortcuts）+ PipeWire / ffmpeg 子进程

## 构建与运行

Windows：

```powershell
# 依赖：.NET 10 SDK
dotnet build src\Zhuoying\Zhuoying.csproj
dotnet run --project src\Zhuoying
```

Linux（X11 与 Wayland 均支持，见 `docs/LINUX-PORT.md`）：

```bash
./build-linux.sh build            # 构建
./build-linux.sh run -- --api-monitors
./build-linux.sh publish          # 自包含 linux-x64 → publish/linux-x64（约 105MB）
./build-linux.sh aot              # NativeAOT → publish/aot-linux（约 46MB，前置 clang + zlib1g-dev）
./install-linux.sh                # 安装桌面项（全局热键必需，见下）
```

`build-linux.sh` 已带必需的环境变量。两点 Linux 特有的注意：

- **全局热键必须经 .desktop 启动**。xdg-desktop-portal 的 GlobalShortcuts 要求
  调用方有 app id，而 app id 是从进程的 systemd scope 名反推的——从终端直接
  运行的进程没有，portal 会直接拒绝。跑 `install-linux.sh` 后从应用菜单启动
  （或开启开机自启）即可。
- **Wayland 首次抓屏会弹一次「共享屏幕」授权框**，点允许后 restore_token 落盘，
  之后静默恢复（实测二次抓屏 0.23～0.30s）。

可选的运行时依赖：MP4 录制需 `ffmpeg`；Wayland 抓屏需
`gstreamer1.0-pipewire`；剪贴板需 `wl-clipboard`（Wayland）或 `xclip`（X11）。

### 发布

根目录两个脚本（双击或命令行运行）：

- **`aot-win.bat`** — NativeAOT 发布 → `publish\aot\`：exe 约 19MB + 3 个原生库
  （共约 35MB；`.pdb` 分发不带）。启动最快、免运行时、难反编译。
  前置：VS「使用 C++ 的桌面开发」工作负载（ILCompiler 的 vcvars 探测在
  VS 2026 上不可靠，脚本内用 vcvars64 + `IlcUseEnvironmentalTools` 绕过）。
- **`release-singlefile-win.bat`** — 自包含单文件发布 → `publish\singlefile\Zhuoying.exe`
  （约 46MB，原生库内嵌，免运行时，首启解压略慢）。

发布后用 `--test-copy` / `--test-shape` / `--test-line` / `--test-text`
系列参数对产物做回归。注意 .bat 为 GBK 编码 + CRLF（cmd 对 UTF-8/LF
中文批处理会解析错位），编辑时保持编码。

Linux 的对应产物由 `./build-linux.sh publish`（自包含，约 105MB）或
`./build-linux.sh aot`（NativeAOT，exe 32MB + 两个原生库共约 46MB，`.dbg` 不分发）
生成，回归用同一套 `--test-*` 参数（`--test-dda` 除外，DDA 是 Windows 专属后端）。
Linux AOT 前置：`clang` + `zlib1g-dev`。

运行后驻留系统托盘，按 `Ctrl+1`（或托盘菜单"截屏"）开始截屏；
`Esc` / 右键取消。开发自测参数：`--test-capture`（启动 1.5s 后自动触发抓屏）、
`--test-settings`（启动即打开设置窗口）、`--test-copy x,y,w,h` /
`--test-select x,y,w,h`（自动抓屏后按虚拟屏幕物理像素设选区并复制/仅设选区）、
`--test-shape x,y,w,h[,圆角%[,填充 0/1[,粗细[,旋转[,线形[,透明度]]]]]]`（添加形状标注）、
`--test-line "x:y;x:y;...[,起端,末端,起粗,末粗,样条,线形,透明度]"`（添加线/箭头标注）、
`--test-text "x:y:w:h:字号:旋转:外框:描边|文本"`（添加文字标注，%20=空格）、
`--test-number "x:y[:值[:类型 0-5[:直径[:空心 0/1[:RRGGBB]]]]][;下一个…]"`（添加编号徽章）、
`--test-mosaic "x,y,w,h[,模糊 0/1[,强度[,旋转°]]][;下一个…]"`（添加区域模糊）、
`--test-pen "x:y;x:y;...[,粗细[,荧光 0/1[,RRGGBB]]][|下一条…]"`（添加画笔笔迹）、
`--test-stamp "x,y,w,h[,旋转[,描边宽 0=关[,透明度]]]|素材名或路径"`（添加图章）、
`--test-eraser "x:y;x:y;...[,粗细]"`（添加橡皮擦除笔迹，2.6s 生效晚于画笔）、
`--test-save x,y,w,h`（设选区并保存到默认目录）、`--test-pin x,y,w,h`（设选区并贴图）、
`--test-dda "x,y,w,h[|输出目录]"`（DDA 与纯 BitBlt 双路抓取同区域，输出两张 PNG、
逐像素 diff 与耗时到目录，默认 `%TEMP%\zhuoying-dda`，完成即退出）、
`--test-record "x,y,w,h[,fps[,毫秒]]"`（GIF 录屏核心自测：区域内自带变色动画源，
录完输出帧数/大小到 `%TEMP%\zhuoying-record-test.txt`）、
`--test-mp4 "x,y,w,h[,fps[,毫秒]]"`（MP4 录制 + SourceReader 解码回读验证）、
`--test-record-ui "x,y,w,h[,毫秒[,mp4]]"`（完整录制 UI 流程，自动完成落盘）。

## 目录结构

```
src/Zhuoying/
  Platform/            平台抽象接口 + PlatformServices（实现的唯一取用口）
  Platform/Windows/    Windows 实现（P/Invoke、手写 DXGI/MF vtable）
  Platform/Linux/      Linux 实现（X11 与 Wayland 双后端 + 共用部分）
  Capture/             截屏会话：控制器、遮罩窗口、选区/标注/编辑器分层、
                       工具栏、录屏、钉屏贴图
  Annotations/         标注模型：元素、样式、线形表、命令式撤销/重做
  Agent/               Agent API：--api-* CLI、MCP 协议层与内置 HTTP 服务
  Settings/            配置模型、持久化、设置窗口
docs/TROUBLESHOOTING.md  疑难问题根因记录（DPI / 剪贴板等）
docs/LINUX-PORT.md       Linux 移植的环境、设计取舍与逐项验证记录
```

## 已知问题与经验

高分屏（DPI 缩放）相关的坑较多，均已定位并记录在
[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)，其中最有价值的一条：
**剪贴板同时写入 CF_DIB 与 "PNG" 自定义格式会导致 Win11 画图粘贴时把图像按 DPI 减半**，
捉影因此只写携带正确 DPI 头的 CF_DIB。

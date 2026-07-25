# 捉影（Zhuoying）

Windows 桌面截屏标注工具：全局热键呼出 → 框选区域 → 原位标注 → 输出（剪贴板 / 文件 / 贴图）。
交互体验对标 Snipaste / 微信截图。当前处于早期开发阶段。

## 功能现状（阶段一、二已完成）

- **全局热键截屏**：默认 `Ctrl+1`（可在设置中改键），触发瞬间冻结**整个虚拟屏幕**
- **跨屏抓取**：所有显示器同时进入截屏态，默认选区为鼠标所在屏全屏；
  选区可跨显示器拖拽新建、移动、8 手柄调整，混合缩放（如 200% + 150%）下
  按物理像素拼接，像素级准确
- **拖拽框选**：实时显示物理像素尺寸；选区外按下即扩展选区到该点
- **复制到剪贴板**：选区右下角工具条按钮 / `Enter` / `Ctrl+C` / 双击选区；
  输出为 100% 物理分辨率的 CF_DIB（携带来源屏幕 DPI），高分屏粘贴不缩小、不模糊
- **标注：形状工具**（快捷键 S）：矩形，圆角 0–100% 可调（100% = 椭圆）+ 旋转，
  8 缩放手柄 + 4 圆角手柄 + 旋转手柄；颜色（预设可改）/ 填充 / 线形（5 种，易扩充）/
  粗细（1–100px）/ 透明度；撤销/重做（Ctrl+Z / Ctrl+Y）；复制时标注按物理像素合成到输出
- **标注：箭头 / 折线工具**（A / L）：箭头拖拽创建（默认起细尾粗 + 三角头）；
  折线逐次点击、双击结束；起/末端头样式 11 种（实心/空心三角菱形圆方块、
  双线箭头、破甲箭头）、两端粗细独立（不等粗 = 渐变带）、弧线（样条）开关
- **标注：文字工具**（T）：矩形文本框（多行、换行、对齐、内边距、外框、描边、
  旋转），就地编辑原生支持中文输入法；点内部编辑、点边框拖动
- **托盘驻留**：无主窗口，托盘菜单提供 截屏 / 设置 / 退出；单实例运行
- **设置**：修改截屏快捷键、标注预设颜色（持久化到 `%AppData%\Zhuoying\settings.json`）
- **DPI**：Per-Monitor DPI Aware v2，混合缩放多屏为一级支持场景

后续规划见 [DEVPLAN.md](DEVPLAN.md)：下一步是标注编辑器其余工具
（画笔/箭头/文字/编号/马赛克/橡皮），之后是保存文件与贴图窗口等，
完整需求见 [REQUIREMENTS.md](REQUIREMENTS.md) 与 [TOOLS-SPEC.md](TOOLS-SPEC.md)。

## 版本号

版本号单一来源为根目录 `Directory.Build.props`，规则：

- **`主.次` = 里程碑**（手动维护）：`0.1` 阶段一，`0.2` 跨屏抓取，…，`1.0` = 需求 v1 范围全部落地；每个里程碑打对应 git tag（如 `v0.1`），变更记录见 [CHANGELOG.md](CHANGELOG.md)
- **文件版本第三、四段 = 构建时间戳**（构建时自动生成）：`距 2026-01-01 的天数 . 当日分钟数`，如 `0.1.205.872` = 2026-07-25 14:32 构建，精确到分钟且不受 PE 版本资源每段 65535 上限影响
- **产品版本**（exe 属性 / 界面显示）：`主.次+本地时间及时区偏移`，如 `0.1+2026-07-25 14:32+1000`
- AssemblyVersion 保持 `主.次.0.0` 稳定；本应用非库，不采用 SemVer 三段语义

## 技术栈

- [Avalonia](https://avaloniaui.net/) 11.3 + .NET 10（仅 Windows，Win10 1903+ / Win11）
- 平台能力（抓屏 / 全局热键 / 剪贴板 / 显示器信息）收敛在 `Platform/` 接口层，
  Windows 后端以 P/Invoke 实现（GDI BitBlt、RegisterHotKey、OLE 剪贴板），
  为未来跨平台预留接口

## 构建与运行

```powershell
# 依赖：.NET 10 SDK
dotnet build src\Zhuoying\Zhuoying.csproj
dotnet run --project src\Zhuoying
```

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

运行后驻留系统托盘，按 `Ctrl+1`（或托盘菜单"截屏"）开始截屏；
`Esc` / 右键取消。开发自测参数：`--test-capture`（启动 1.5s 后自动触发抓屏）、
`--test-settings`（启动即打开设置窗口）、`--test-copy x,y,w,h` /
`--test-select x,y,w,h`（自动抓屏后按虚拟屏幕物理像素设选区并复制/仅设选区）、
`--test-shape x,y,w,h[,圆角%[,填充 0/1[,粗细[,旋转[,线形[,透明度]]]]]]`（添加形状标注）、
`--test-line "x:y;x:y;...[,起端,末端,起粗,末粗,样条,线形,透明度]"`（添加线/箭头标注）、
`--test-text "x:y:w:h:字号:旋转:外框:描边|文本"`（添加文字标注，%20=空格）。

## 目录结构

```
src/Zhuoying/
  Platform/            平台抽象接口（IHotkeyService / IScreenCapture / IClipboardImage）
  Platform/Windows/    Windows P/Invoke 实现
  Capture/             截屏会话：控制器、遮罩窗口、选区/标注/编辑器分层、工具栏
  Annotations/         标注模型：元素、样式、线形表、命令式撤销/重做
  Settings/            配置模型、持久化、设置窗口
docs/TROUBLESHOOTING.md  疑难问题根因记录（DPI / 剪贴板等）
```

## 已知问题与经验

高分屏（DPI 缩放）相关的坑较多，均已定位并记录在
[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)，其中最有价值的一条：
**剪贴板同时写入 CF_DIB 与 "PNG" 自定义格式会导致 Win11 画图粘贴时把图像按 DPI 减半**，
捉影因此只写携带正确 DPI 头的 CF_DIB。

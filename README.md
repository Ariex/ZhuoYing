# 捉影（Zhuoying）

Windows 桌面截屏标注工具：全局热键呼出 → 框选区域 → 原位标注 → 输出（剪贴板 / 文件 / 贴图）。
交互体验对标 Snipaste / 微信截图。当前处于早期开发阶段。

## 功能现状（阶段一已完成）

- **全局热键截屏**：默认 `Ctrl+1`（可在设置中改键），触发瞬间冻结屏幕
- **单屏抓取**：自动选择鼠标所在显示器，默认选区为该屏全屏
- **拖拽框选**：可重新拖拽任意矩形区域，实时显示物理像素尺寸
- **复制到剪贴板**：选区右下角工具条按钮 / `Enter` / `Ctrl+C` / 双击选区；
  输出为 100% 物理分辨率的 CF_DIB（携带来源屏幕 DPI），高分屏粘贴不缩小、不模糊
- **托盘驻留**：无主窗口，托盘菜单提供 截屏 / 设置 / 退出；单实例运行
- **设置**：修改截屏快捷键（持久化到 `%AppData%\Zhuoying\settings.json`）
- **DPI**：Per-Monitor DPI Aware v2，混合缩放多屏为一级支持场景

后续规划见 [DEVPLAN.md](DEVPLAN.md)：阶段二为跨屏抓取，之后是标注编辑器
（画笔/形状/箭头/文字/编号/马赛克/橡皮 + 撤销重做）、保存文件与贴图窗口等，
完整需求见 [REQUIREMENTS.md](REQUIREMENTS.md) 与 [TOOLS-SPEC.md](TOOLS-SPEC.md)。

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

运行后驻留系统托盘，按 `Ctrl+1`（或托盘菜单"截屏"）开始截屏；
`Esc` / 右键取消。开发自测参数：`--test-capture`（启动 1.5s 后自动触发抓屏）、
`--test-settings`（启动即打开设置窗口）。

## 目录结构

```
src/Zhuoying/
  Platform/            平台抽象接口（IHotkeyService / IScreenCapture / IClipboardImage）
  Platform/Windows/    Windows P/Invoke 实现
  Capture/             截屏会话：控制器、遮罩窗口、选区层、位图工具
  Settings/            配置模型、持久化、设置窗口
docs/TROUBLESHOOTING.md  疑难问题根因记录（DPI / 剪贴板等）
```

## 已知问题与经验

高分屏（DPI 缩放）相关的坑较多，均已定位并记录在
[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)，其中最有价值的一条：
**剪贴板同时写入 CF_DIB 与 "PNG" 自定义格式会导致 Win11 画图粘贴时把图像按 DPI 减半**，
捉影因此只写携带正确 DPI 头的 CF_DIB。

# 捉影 — 开发计划

> 需求见 REQUIREMENTS.md / TOOLS-SPEC.md。本文件跟踪阶段进度。

## 阶段一：热键抓屏 + 单屏选区 + 复制 ✅（2026-07-24 完成）

- [x] 项目骨架：Avalonia 11.3 + .NET 10，`src/Zhuoying/`，平台能力收敛到 `Platform/` 接口层（IHotkeyService / IScreenCapture / IClipboardImage），Windows 后端 P/Invoke 实现
- [x] Per-Monitor DPI Aware v2（app.manifest）
- [x] 单实例（Mutex）+ 托盘驻留（截屏 / 退出菜单，左键点击托盘 = 截屏）
- [x] 全局热键 Ctrl+1（专用消息线程 + RegisterHotKey）；冲突时降级为托盘提示文案
- [x] 触发即冻结：GDI BitBlt 抓取鼠标所在显示器 → 全屏遮罩窗口显示冻结帧
- [x] 默认选区 = 当前屏幕全屏；可拖拽重新画矩形选区（选区外暗化 + 蓝框 + 物理像素尺寸标签）
- [x] 选区右下工具条：复制（另有 Enter / Ctrl+C / 双击等效；Esc / 右键取消）
- [x] 剪贴板输出 CF_DIB + PNG 双格式，按物理像素裁剪
- [x] 开发自测参数 `--test-capture`（启动 1.5s 后自动触发一次抓屏）、`--test-settings`（打开设置窗口）
- [x] 工具条改版：白色背景 + 35×35 图标按钮（22×22 矢量复制图标），显式覆写 Fluent 悬停/按下状态色
- [x] 设置窗口（托盘菜单进入）：修改截屏快捷键（字母/数字/小键盘/F1–F12，改键即时生效，冲突时报错并恢复原热键），持久化到 %AppData%/Zhuoying/settings.json

- [x] 修复：Win11 画图粘贴截图被减半 —— 根因是剪贴板同时写入 "PNG" 自定义格式会触发画图按 DPI 缩小粘贴内容；现只写 CF_DIB（头部携带来源屏幕 DPI，ppm），经 OLE（OleSetClipboard+Flush）从独立 STA 线程写入。画图/微信等粘贴均为完整物理分辨率
- [x] 修复：冻结帧位图必须保持 96 DPI（Avalonia Image 对非 96 DPI 位图渲染错误，曾导致遮罩画面放大 2 倍）；输出位图在裁剪时才标记真实 DPI

已知遗留（按需求属后续阶段）：
- 热键冲突的托盘气泡提示（当前为托盘悬停文案提示 + 设置窗口内报错）
- 选区 8 手柄调整 / 拖动选区（标注编辑阶段与"选择"工具一并做）
- 本机 PixPin 默认占用 Ctrl+1，可在设置中改键绕开

## 阶段二：跨屏抓屏（下一步）

- [ ] 触发时抓取全部显示器冻结帧（IScreenCapture.GetAllMonitors 已预留）
- [ ] 每个显示器一个遮罩窗口，共享一个选区状态（虚拟屏幕物理像素坐标系）
- [ ] 选区可跨显示器拖拽；混合 DPI（主屏 200% + 副屏其他缩放）下按物理像素拼接输出 —— 项目最高技术风险点（REQUIREMENTS §5）
- [ ] 验收：混合 DPI 双屏跨屏选区像素级准确

## 后续阶段（概要）

- 标注编辑器核心（矢量元素模型 + 命令模式撤销/重做）与各工具（TOOLS-SPEC.md）
- 输出扩展：保存文件、贴图窗口
- 设置界面 + settings.json / presets.json 持久化
- 生命周期完善：开机自启、热键改键、托盘气泡

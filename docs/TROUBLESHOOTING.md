# 疑难问题记录

> 开发过程中定位并解决的问题，记录根因与结论，避免重蹈覆辙。
> 环境：Windows 11 Pro，双 4K 显示器（主屏 200% 缩放），Avalonia 11.3 + .NET 10。

---

## 1. 截屏粘贴到 Win11 画图中图像被减半（最曲折的一个）

**现象**：捉影复制 3840×2160 的截图后，粘贴进 Win11 新版画图变成 1920×1080（正好一半）；
粘贴到其他程序正常。PixPin 等工具粘贴到画图则是完整尺寸。

**排查过程中排除的假设**（均做过对照实验）：

| 假设 | 实验 | 结果 |
|------|------|------|
| DIB/PNG 缺少 DPI 元数据 | 写入 biXPelsPerMeter=7559（192DPI）与 PNG pHYs | 无效，仍减半 |
| 写入线程 DPI 上下文 | unaware / system-aware / PMv2 线程分别写入 | 全部正常，与减半无关 |
| 进程清单级 PMv2 | 带 PMv2 清单的最小控制台程序写入 | 正常 |
| 进程持有高 DPI 窗口 | 最小程序创建窗口后写入；捉影先关遮罩再写入 | 正常 / 仍减半 |
| 第三方（PixPin）改写剪贴板 | 序列号 + 所有者监控 | 无第三方接手 |
| 原始 Win32 vs OLE 写入路径 | OleSetClipboard + OleFlushClipboard | 仍减半 |
| 数据字节本身 | 把捉影写入的字节原样由脚本重写回剪贴板 | **恢复正常** ← 关键线索 |

**根因**：剪贴板中同时存在 **CF_DIB + 自定义 "PNG" 格式**时，Win11 画图的粘贴逻辑会被
"PNG" 格式带偏，将图像按 DPI 折算减半（96/192）。对照实验：

- 只写 CF_DIB → 画图粘贴完整 3840×2160
- CF_DIB + "PNG" → 画图粘贴 1920×1080
- 只写 "PNG" → 画图什么都粘不出来（它根本不支持从该格式读取）

**解决方案**（`WindowsClipboardImage`）：

1. 剪贴板**只写 CF_DIB**，不再附带 "PNG" 自定义格式；
2. DIB 头 `biXPelsPerMeter/biYPelsPerMeter` 写入来源显示器真实 DPI
   （`round(96 × 缩放比 × 1000 / 25.4)`，200% → 7559），使 Word 等按物理尺寸
   排版的应用获得正确插入大小；
3. 在独立 STA 线程上经 OLE（`OleSetClipboard` + `OleFlushClipboard`）写入，
   数据固化后不依赖进程存活。

**经验**：排查剪贴板问题时，`IsClipboardFormatAvailable` 偶发误报，以
`EnumClipboardFormats` 枚举为准；读 DIB 头部（宽/高/ppm）比截图肉眼比对可靠得多。

---

## 2. 遮罩画面被放大 2 倍，只能看到屏幕左上 1/4

**现象**：为解决问题 1 曾把冻结帧 `WriteableBitmap` 的 DPI 标为 192，遮罩上的画面
立即变成 2 倍放大。

**根因**：Avalonia 的 `Image` 控件对**非 96 DPI 位图**渲染不正确：布局按 DIP 尺寸
（PixelSize ÷ 缩放）计算源矩形，而绘制时源矩形按**像素**解释，结果只取了位图左上
1/4 区域再拉伸到全窗口。

**解决方案**：

- 供 UI 显示的位图（冻结帧）一律保持 **96 DPI**；
- 输出位图（剪贴板/文件）在 `BitmapUtil.Crop` 裁剪时才写入真实 DPI 元数据——
  DPI 只是元数据，不影响像素内容。

---

## 3. 默认全屏选区时，鼠标事件穿透选区层，无法拖拽画框

**现象**：遮罩打开后拖拽鼠标画选区没有任何反应；日志显示 `SelectionLayer` 收不到
`PointerPressed`。

**根因**：暗化遮罩用 EvenOdd 几何实现"全屏暗化 + 选区镂空"。默认选区=全屏时，
镂空后整层**没有任何绘制像素**，Avalonia 的命中测试基于实际绘制内容，整层不可命中，
指针事件全部落到下层的 Image 上。

**解决方案**：`SelectionLayer.Render` 先铺一层 `Brushes.Transparent` 矩形——
透明画刷参与命中测试但不可见。

---

## 4. 工具条按钮按下后图标与背景混色不可辨

**现象**：Fluent 主题下按钮按下时，主题会替换按钮背景为主题色并把前景改白，
在自定义配色的工具条上图标几乎不可见。

**解决方案**：

- 图标不依赖 `Foreground`，用固定描边色的矢量图形（两张纸片形状的复制图标）绘制；
- 用代码构造样式选择器显式覆写模板内 `ContentPresenter` 的悬停/按下背景：
  `Button:pointerover|:pressed /template/ ContentPresenter#PART_ContentPresenter`
  （悬停 `#EAEAEA`、按下 `#D4D4D4`），控件自身 Styles 后应用、优先生效。

---

## 5. 全局热键 Ctrl+1 被 PixPin 占用

**现象**：`RegisterHotKey` 静默失败，热键无响应。

**解决方案**：

- 注册失败时托盘悬停文案提示冲突（气泡提示待后续阶段）；
- 设置窗口支持改键（即时重注册，失败自动恢复原热键并报错）；
- 本机开发约定：捉影用 **Ctrl+2**，PixPin 保留 Ctrl+1。

---

## 6. 自动化测试相关的坑（开发自查用）

- 用 PowerShell + System.Drawing 截屏验证 UI 前必须 P/Invoke `SetProcessDPIAware()`，
  否则进程被 DPI 虚拟化，只能抓到左上角 1/4 物理像素，坐标全部失真；
- PixPin 截屏会话持有全局鼠标钩子，会吞掉 `mouse_event` 注入的拖拽事件；其会话
  内右键可取消；
- 捉影是单实例（Mutex），重启测试前必须先结束旧进程；
- `--test-capture` 启动参数：1.5s 后自动触发一次抓屏；`--test-settings`：启动即开设置窗口。

# 疑难问题记录

> 开发过程中定位并解决的问题，记录根因与结论，避免重蹈覆辙。
> 环境：Windows 11 Pro，双 4K 显示器（主屏 200%、副屏 150% 缩放，混合 DPI），
> Avalonia 11.3 + .NET 10。

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
3. 写入走纯 Win32 剪贴板 API（`SetClipboardData` + HGLOBAL，所有权交给系统后
   数据不依赖进程存活）。早期版本曾用 OLE（`OleSetClipboard`+`OleFlushClipboard`），
   为 NativeAOT 兼容（AOT 不支持内置 COM interop）改为纯 Win32——两者写出的
   字节完全一致，减半问题的根因与写入 API 无关（见上表"OLE 路径"实验行）。

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

- 用 PowerShell + System.Drawing 截屏验证 UI 前必须把进程设为 **PMv2 感知**
  （`SetProcessDpiAwarenessContext(-4)`），坑的演进见问题 9；
- PixPin 截屏会话持有全局鼠标钩子，会吞掉 `mouse_event` 注入的拖拽事件；其会话
  内右键可取消；
- 捉影是单实例（Mutex），重启测试前必须先结束旧进程；
- 自测启动参数：`--test-capture`（1.5s 后自动触发抓屏）；`--test-settings`（启动即开
  设置窗口）；`--test-copy x,y,w,h` / `--test-select x,y,w,h`（自动抓屏后按虚拟屏幕
  物理像素直接设选区并复制/仅设选区，免键鼠注入验证跨屏输出与渲染）。

---

## 7. 副屏遮罩窗口尺寸错误：DPI 切换与手动设置尺寸互搏
> 注：发生于"每屏一个遮罩窗口"的中间方案，该方案最终被 §10 的单窗口架构取代；
> 但"投递收敛校验环"保留在单窗口上（防御性）。

**现象**：跨屏会话中副屏（150%）的遮罩窗口只覆盖部分屏幕。窗口在 `Opened` 里按
`RenderScaling` 重算尺寸，但副屏窗口的 `WM_DPICHANGED` **晚于 `Opened` 到达**
（窗口在主屏 DPI 上下文创建，移动到副屏才切换），当时读到的还是主屏缩放 2.0。
改成在 `ScalingChanged` 里同步重设尺寸后更糟：窗口被连乘多次 0.75
（3840×2160 → 2880×1620 → 2160×1215）——DPI 切换过程中系统按"建议矩形"缩放窗口，
与我们的设置互相覆盖并再次激起 DPI 重排。

**解决方案**（`CaptureOverlayWindow.PostGeometryCheck`）：不在 `ScalingChanged` 里
同步改尺寸，而是**投递（Post）一个事后校验**：几何与目标显示器不符则重设，再投递
下一轮校验，直至收敛（设上限防死循环）。目标值恒为
`显示器物理尺寸 ÷ 当前 RenderScaling`，无论 DPI 切换把窗口折腾成什么样，最终都
收敛到精确覆盖。

---

## 8. 跨 DPI 移屏后 PointToScreen/PointToClient 与实际渲染缩放不一致
> 注：同 §7，发生于中间方案；"物理↔DIP 手工换算"保留在单窗口上（§10）。

**现象**：副屏窗口几何收敛后（客户区精确覆盖副屏），选区边框/镂空仍画在错误位置，
偏差恰为 192/144 = 4/3：`PointToClient` 按屏幕缩放 1.5 换算，渲染却按窗口
`RenderScaling` 2.0 进行——窗口经历跨 DPI 移动后，Avalonia 内部这两条换算路径
可能停在不同的缩放值上（`GetDpiForWindow` 实测也可能停在 192 而非 144）。

**解决方案**：涉及"虚拟屏幕物理像素 ↔ 窗口 DIP"的换算**全部手工计算**：
`DIP = (物理 - 显示器原点) ÷ RenderScaling`（及其逆）。只要窗口客户区精确覆盖
显示器（问题 7 的收敛环保证），该映射与渲染**定义上自洽**——指针事件的 DIP 坐标
本就是客户区物理像素 ÷ RenderScaling 得来，无论 RenderScaling 具体停在什么值。
跨屏拖拽（指针捕获后坐标越出窗口）同样成立。

---

## 9. 混合 DPI 下，系统级 DPI 感知的测试脚本坐标被虚拟化

**现象**：验证脚本（`SetProcessDPIAware()`，系统级感知）查询到副屏在
`5120,0 5120×2880 dpi=192`，与应用（PMv2）看到的 `3840,0 3840×2160 dpi=144`
完全不同，一度误判"显示器之间有 1280px 间隙"、"边框画错位置"。

**根因**：**系统级 DPI 感知**进程的坐标空间按主屏 DPI 虚拟化——与主屏缩放不同的
显示器，其坐标/尺寸被乘以 `主屏DPI/该屏DPI`（192/144 = 4/3），`GetDpiForMonitor`
也一律返回主屏 DPI。只有 **PMv2 感知**进程才看到真实物理坐标。

**解决方案**：所有验证脚本改用 `SetProcessDpiAwarenessContext(-4)`
（PER_MONITOR_AWARE_V2），与应用同一坐标系。**经验**：混合 DPI 问题排查前先确认
观测工具自身的 DPI 感知级别，否则测量结果本身就是失真的。

---

## 10. 跨 DPI 移屏窗口按钮点不中：渲染缩放与输入命中缩放分裂（最终改单窗口架构）

**现象**：问题 7/8 修复后自动化渲染验证全过，但真人操作发现副屏上**复制按钮点不中**：
tooltip 在光标处弹出，但光标距绘制出的图标有明显距离（偏移约 4/3 倍）；选区外
点击扩展选区同样偏移。即渲染正确、输入命中错位。

**根因**：问题 8 的手工换算只能救"应用自己画/自己算"的部分；**决定哪个控件收到
指针事件的命中测试发生在 Avalonia 内部**，它把客户区物理坐标换算成 DIP 用的缩放
（实测 1.5，屏幕真实值）与渲染缩放（2.0，滞留值）不一致，应用层无法修正。
结论：**"窗口创建后跨 DPI 移屏"这条路线在 Avalonia 11.3 上不可靠**。

**解决方案（最终架构）**：放弃"每屏一个遮罩窗口"，改为**单一窗口覆盖整个虚拟屏幕**
（所有显示器的包围盒；ShareX 同款方案）：

- PMv2 窗口横跨多屏时**不会被系统缩放**，客户区像素与虚拟桌面物理像素 1:1；
- 窗口从创建到关闭不跨 DPI 移动，渲染/输入自始至终同一个缩放，定义上自洽
  （该缩放值具体是 2.0 还是 1.5 无所谓，只要唯一且稳定）；
- 附带大幅简化：不再需要每屏裁剪冻结帧、不再需要工具条/尺寸标签的跨窗口归属调度；
- 代价：UI 控件（工具条/手柄/文字）在不同缩放的屏上物理像素尺寸相同、毫米尺寸
  略有差异，可接受。

**验证**：注入真实点击复制按钮 → 剪贴板获得正确输出（3000×900）；选区外点击
（含跨屏点到另一块屏）扩展后边框位置像素级精确。**教训**：本次自动化起初只验证了
渲染（截图比对），没有验证输入命中——两者在 DPI 异常状态下会分裂，输入路径必须
用注入点击单独验证。

---

## 11. 半透明标注元素内部重叠区颜色变深

**现象**：透明度调低后，箭头头部与线身重叠处、填充矩形的粗边框与填充重叠处
颜色明显比其他区域深。

**根因**：`DrawingContext.PushOpacity` **按图元逐个**乘透明度（不是把元素作为
一组先栅格化再统一混合），同一元素内两个半透明图元重叠 = 混合两次。
Avalonia 11.3 没有公开的"带边界建合成层"的 PushOpacity 重载可用。

**解决方案**：从几何上消除元素内部重叠，使每个像素只被一个图元覆盖：

- 填充形状：填充矩形**内缩到描边内沿**（半线宽），圆角相应减小，与描边零重叠；
- 线端头：线身让位距离取端头**背沿精确值**；等粗线是圆头笔帽、端点外还凸出
  半线宽，有几何端头的一端额外让出半线宽（空心端头再加其描边半宽补偿），
  100% 不透明时圆头恰好与端头背沿相切、无豁口；
- 双线（V 形）箭头：两根线合并为**单条开放路径**（一个图元），交点不叠加；
- 渐变粗细箭头（最常用场景）：实心三角/破甲端头**直接融合进渐变带多边形**
  （单图元填充，端头与线身零重叠零缝隙）；
- 渐变带锐角折点用**斜切双点（bevel）**替代斜接，避免偏移边交叉产生
  自交尖刺与叠加区。

**验证**：50% 透明度下逐像素检验混合系数——边框中线/填充中心/头基交界/头内部
全部 = 0.5×底图 + 0.5×标注色（±3）。

**排查陷阱**：验证脚本连续两次"启动应用 → 拷贝 → 读剪贴板"时，第二次拷贝偶发
失败会静默读到上一次的旧内容，表现为"前后景完全相同/元素消失"的假象——
每次读取前先 EmptyClipboard，空结果即重试。

## 12. Desktop Duplication 新会话首帧"成功"但内容全黑

**现象**：DDA 抓屏（DuplicateOutput → AcquireNextFrame → CopyResource → Map）
每一步都返回 S_OK，但读出的像素全为 0。早期测试多次正常（diff=0），
之后同一代码在所有配置（Debug/Release/AOT）下稳定全黑——"曾经好过"极具迷惑性。

**根因**：`AcquireNextFrame` 成功 ≠ 桌面纹理有效。新建 duplication 会话后，
若 DWM 尚未产生过新的合成帧，首次 Acquire 会返回一个
**`LastPresentTime=0`、`AccumulatedFrames=0` 的"仅指针"帧**，此时桌面纹理
未播种、内容全黑。是否踩中取决于会话创建与 DWM 合成的时序：桌面有任何
动画/重绘时首帧总是有效（因此早期测试"恰好"通过），桌面完全静止时必黑。

**解决方案**：只接受 `LastPresentTime != 0` 的帧；仅指针帧 ReleaseFrame 放回
重试（数次、短超时）；超时视为桌面静止，回退 BitBlt——静止桌面 BitBlt 本就
像素正确，而 DDA 的价值场景（独占全屏游戏、视频 MPO）必有持续合成帧，
且创建会话本身会触发 MPO 拆除重合成、很快送来真帧。

**验证陷阱**：给 --test-dda 做像素一致性验证时，静止桌面下 DDA 会正确回退
BitBlt，测不到 DDA 路径本身——测试钩子每屏放一个 8×8 闪烁小窗制造合成活动，
并设 `WDA_EXCLUDEFROMCAPTURE`（DDA 与 BitBlt 两路都不可见，不污染 diff）。
另外屏幕自身可能存在低幅动态内容（本机实测 GDI 自身相隔 130ms 双抓
diff≈2900 像素、幅度 ±1~3），两路抓取非同瞬，diff 达到该本底即视为一致。

## 13. Linux 开发机上 `dotnet restore` 无限挂起（IPv6 黑洞）

**现象**：`dotnet restore` / `dotnet build` 停在 `Determining projects to restore...`
不动，无报错、无超时，`~/.nuget/packages` 一个包都没下来。进程 0.4% CPU、S 状态，
一直在等网络。同时 `curl https://api.nuget.org/v3/index.json` 秒回 200，
下载 13MB 的 .nupkg 只要 3.3 秒（3.9MB/s）——"网络明明是好的"。

**根因**：开发机有全局 IPv6 地址和默认路由，但 IPv6 出网是**黑洞**
（丢包而非 REJECT，所以是超时而不是立即失败）。`api.nuget.org` 的 DNS
同时返回 A 和 AAAA 记录：`curl` 有 Happy Eyeballs，几百毫秒就回退到 IPv4；
.NET 的 HttpClient 不做这个回退，于是一直挂在 IPv6 连接上。

诊断命令（关键是分别测两个协议族，别只测"能不能上网"）：

```bash
curl -4 -sS -o /dev/null -w "IPv4: %{http_code} %{time_total}s\n" https://api.nuget.org/v3/index.json
curl -6 -sS -o /dev/null -w "IPv6: %{http_code} %{time_total}s\n" https://api.nuget.org/v3/index.json
```

**解决方案**：`export DOTNET_SYSTEM_NET_DISABLEIPV6=1`。实测 restore 从
无限挂起变成 2.4 秒。已固化进根目录 `build-linux.sh`，手工敲 dotnet 命令时
也要记得带上。

**排查陷阱**：极易误判成 snap 版 dotnet 的沙箱问题或 NuGet 源配置问题。
判据是**最小项目能否 restore**：无 PackageReference 的空项目 3.6 秒就 build 完
（根本不联网），一加 `<PackageReference Include="Avalonia" />` 就挂——
问题在网络栈，不在 dotnet 安装。

## 14. Linux 上启动即崩：libSkiaSharp 版本不兼容

**现象**：Linux 下应用一启动就 abort（core dumped）：

```
The version of the native libSkiaSharp library (88.1) is incompatible with
this version of SkiaSharp. Supported versions are in the range [119.0, 120.0).
```

栈顶是 `Avalonia.Skia.SkiaPlatform.Initialize` → `SKFontManager.get_Default`，
即 Avalonia 初始化字体管理器的第一步。Windows 上同一份代码从无此问题。

**根因**：NuGet 传递依赖把 `SkiaSharp.NativeAssets.Linux` 解析成了 **2.88.9**，
而托管 `SkiaSharp` 是 **3.119.2**。查依赖树可见 Win32 与 macOS 的 NativeAssets
都被正确带到 3.119.2，**唯独 Linux 那个停在 2.88.9**——所以这个坑在 Windows
上永远不会暴露，是纯粹的移植期陷阱。

```bash
dotnet list src/Zhuoying/Zhuoying.csproj package --include-transitive | grep -i skia
```

**解决方案**：在 csproj 里对非 Windows 目标显式钉死 native 包版本，
与托管 SkiaSharp 对齐：

```xml
<ItemGroup Condition="'$(ZyWindows)' != 'true'">
  <PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="3.119.2" />
</ItemGroup>
```

**同类风险**：`HarfBuzzSharp.NativeAssets.Linux`（本项目实测 8.3.1.3 与托管端
一致，无需干预）。日后升级 Avalonia 或 Svg.Skia 后应重新核对这两行依赖树。

## 15. Xvfb 里设的测试图案抓出来是全黑（不是抓屏代码的锅）

**现象**：在 Xvfb 上用 `magick display -window root pattern.png` 或
`xsetroot -solid '#FF0000'` 铺好测试图案，应用的 X11 抓屏读回来却全是黑色，
一度以为 `XGetImage` 实现有 bug。

**根因**：这两个命令设置的是 root window 的**背景 pixmap**，而 pixmap 属于
设置它的那个客户端；命令进程一退出，X server 就按默认的 close-down mode
释放掉它，root 背景随即变回黑色。用 ctypes 直接调 `XGetImage` 复现出
同样的全黑，证明 Xlib 侧行为一致——问题在测试夹具，不在被测代码。

**解决方案**：测试内容改用**常驻的真实 X 窗口**（`xlogo -geometry
400x300+200+150 -bg blue -fg white &` 之类），窗口只要不关就一直在。
以此为基准，应用抓屏与 `ffmpeg -f x11grab` 抓同一区域**逐字节一致**
（400×300 = 120000 像素零差异），抓屏管线随即确认无误。

**排查陷阱**：定位时务必**同一时刻**跑对照组。早期一次对比里 ffmpeg 抓到红色
而应用抓到黑色，看着像应用的 bug，实际只是 ffmpeg 那次跑在
`magick display` 尚未退出的窗口期内——两次抓取隔了几秒，夹具状态已经变了。

## 16. GIF 的 LZW 码宽提前一个码切换，整帧数据流损坏（平台无关，Windows 同样中招）

**现象**：录出的 GIF 用 ffmpeg 解码报 `LZW decode failed`，Pillow 直接拒绝
（`broken data stream when reading image file`，只读得出第一帧）。但**看起来是好的**——
ffmpeg 和浏览器对 LZW 错误容错，照样把画面显示出来，`magick identify`
也能报出正确的帧数与帧差分矩形。Linux 移植期做逐帧像素校验才暴露出来。

**定位**：手写一个 GIF LZW 解码器逐码走，报告
「第 257 码读到码值 1002，而字典下一个可用码是 512」。第 257 码正是码宽
9→10 位的切换点。用同一个解码器去解 ImageMagick 生成的参考 GIF 则完全正常
（60000/60000 像素、正常 EOI），证明问题在编码器不在解码器。

**根因**：升位时机差一个码。编码器每输出一个码就建一条新表项，而解码器读到的
**第一个数据码建不了表项**（还没有前缀可拼），此后解码器的表恒比编码器少一条。
原代码按 `nextCode == 1 << codeSize` 升位，与解码器"同步"只是看起来同步——

- 编码器：输出第 N 个数据码后 `nextCode = 258 + N`，`== 512` 时 N=254，第 255 码起用 10 位；
- 解码器：读完第 N 个数据码后 `next = 257 + N`，`== 512` 时 N=255，第 256 码起才用 10 位。

编码器提前一个码切到新位宽，从此整条码流比特错位，解出天文数字的非法码。

**解决方案**：编码器的升位条件晚一个码：

```csharp
if (nextCode == (1 << codeSize) + 1 && codeSize < 12)
    codeSize++;
```

三档升位点（513 / 1025 / 2049）正好对上解码器的 512 / 1024 / 2048。
修复后 ffmpeg 零错误、Pillow 读全部帧，压缩率不受影响（29006 → 29005 字节）。

**为什么一直没被发现**：帧间差分让**除首帧外的每一帧都很小**（本项目实测差分帧
只有 60×40、约 70 个码），根本到不了 512 这个升位点，一路正常；只有数据量大的
整帧（首帧、或画面大改动时）才会踩中。加上主流播放器容错，肉眼完全看不出来。

**验证方法**：别只看"能不能播"。`ffmpeg -v error -i x.gif -f null -` 有任何输出即为损坏；
Pillow 逐帧 `seek` 是最严格的判据。

## 17. Wayland 抓屏首帧要等十几秒到一分钟（PipeWire + GStreamer 三连坑）

**现象**：portal ScreenCast 会话建好了、gst 管道也起来了，但读第一帧要 15 秒起步，
屏幕完全静止时能等到 57 秒。图像内容本身是对的，纯粹是慢。

**根因有三层，逐层剥开**：

1. **`fdsink` 默认 `sync=true`**（决定性）。`GstBaseSink` 会按 buffer 的时间戳
   等到"该播放的时刻"才把数据吐给下游。这对播放器是对的，对抓屏是灾难。
   管道末端加 `sync=false` 后首帧从 15700ms 降到 **50ms**。
2. **不能丢首帧**。§12 的 Windows DDA 教训是"首帧可能未播种、必须丢弃重取"，
   在 PipeWire 上**正好相反**：合成器在流建立时就推一帧当前内容，
   此后**只在画面变化时才推新帧**。静止桌面上多要一帧就是无限期干等。
   同一个直觉在两个平台上要反着用。
3. **`Process.Kill(entireProcessTree: true)` 在 Linux 上很慢**。它要扫 `/proc`
   重建整棵进程树，实测耗时抖动到 2 秒。子进程不 fork 时用无参 `Kill()`。
   另外别对一次性管道先 `WaitForExit` 等优雅退出——gst-launch 收到 stdin 关闭后
   要走完整套 EOS 流程，白等满超时。

**结果**：单次抓屏 2.4s → **0.30s**，且不再有抖动。

**排查提醒**：先加分段计时再动手优化。这个问题最初被判断成"portal 会话协商慢"，
准备去做"启动即预热会话"——一测才发现协商只占 33ms，预热一分钱省不下来，
全部开销在别处。

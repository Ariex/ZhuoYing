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

- [x] 版本号体系：`Directory.Build.props` 单一来源，主.次=里程碑（手动），文件版本三、四段=构建时间戳（距 2026-01-01 天数.当日分钟，自动），产品版本=`主.次+本地时间±时区偏移`；UI（设置窗口标题/托盘提示）显示版本；CHANGELOG.md 记录变更
- [x] 选区编辑：8 手柄调整大小（可越过对边翻转）、选区内拖拽移动、按方位联动光标
- [x] 选区外按下：选区即时扩展到按下点（包围盒），松开保留；拖拽超阈值则改为画新矩形
- [x] 全屏默认选区时内部按下直接画新矩形（否则无法重新框选）
- [x] 工具条上使用默认箭头光标

已知遗留（按需求属后续阶段）：
- 热键冲突的托盘气泡提示（当前为托盘悬停文案提示 + 设置窗口内报错）
- 本机 PixPin 默认占用 Ctrl+1，可在设置中改键绕开（本机已设 Ctrl+2）

## 阶段二：跨屏抓屏 ✅（2026-07-25 完成，里程碑 0.2）

- [x] 触发时一次 BitBlt 冻结整个虚拟屏幕（各屏画面同一时刻）
- [x] **单一遮罩窗口覆盖整个虚拟屏幕**（所有显示器包围盒，ShareX 同款方案）。曾走过"每屏一个窗口"的弯路：窗口跨 DPI 移屏后 Avalonia 渲染缩放与输入命中缩放永久分裂，按钮点不中（TROUBLESHOOTING §7/§8/§10）
- [x] 选区状态与交互逻辑收敛到 SelectionController（虚拟屏幕物理像素坐标系），SelectionLayer 为视图/输入适配层，物理↔DIP 手工换算
- [x] 选区可跨显示器拖拽/扩展/移动/手柄调整
- [x] 混合 DPI（本机主屏 200% + 副屏 150%）下输出按物理像素从全帧裁剪，跨屏选区像素级准确（渲染：18 点抽样 ×3 场景；输入：注入点击复制按钮/选区外扩展点击全过）；输出 DPI 取选区覆盖面积最大的显示器
- [x] 自测参数 `--test-copy` / `--test-select`（按物理像素直接设选区，免键鼠注入）

## 阶段三：标注编辑器核心 + 形状工具 ✅（2026-07-25 完成，里程碑 0.3）

- [x] 标注模型骨架（`Annotations/`）：元素 = 几何 + 样式（record 快照）；命令模式撤销/重做栈（创建/删除/移动/缩放/圆角/属性修改全部入栈，滑条拖动合并为单条命令）
- [x] 分层架构：冻结帧 → AnnotationLayer（元素渲染，选区外随遮罩暗化）→ SelectionLayer（纯渲染）→ EditorLayer（统一输入路由：元素手柄 → 元素本体 → 选区交互）
- [x] 形状工具：矩形，圆角 0–100%（CSS 百分比语义，100% = 精确椭圆而非药丸形）；拖拽创建，画完自动回选择工具
- [x] 选中编辑：8 缩放手柄 + 四角内 4 个圆角手柄（橙色圆点，向中心拖增大圆角；按所属角有向计算，越过角/中心钳位不回弹；手柄显示位置与中心保持间距避免 100% 时重合）+ 包围盒上方旋转手柄（Shift 吸附 15°），移动（形状内部整体可拖拽，优先于选区操作）、Delete 删除
- [x] 元素旋转：角度随样式快照进撤销栈；渲染/命中/手柄/输出合成统一走绕中心旋转变换；工具栏旋转弹层（-180°..180°）
- [x] 透明度 0–100%（作用于整个元素，TOOLS-SPEC §0.2）：工具栏弹层，输出混合已像素级验证
- [x] 样式：颜色（预设 6 种红黄绿蓝黑白，设置中可改、最多 10 个，色板方块 + 选中彩虹渐变外框含 1px 白色间隔）、填充（与描边同色）、线形（实线/虚线/点虚线/短横一点/短横两点，虚线段圆头 + 拐角圆连接，PixPin 风格；数据驱动可扩充——`LineStyles.All` 加一行即可，无需 SVG）、粗细（1–100px，紧凑滑条 MiniSlider + 滚轮）
- [x] 工具栏两行：工具行（选择/形状/撤销/重做/复制/取消）+ 形状属性行（形状工具激活或选中元素时显示）；粗细/圆角为悬浮弹层——自管理 Popup + 300ms 看门狗（指针在按钮/弹层上或滑条拖拽中不关闭；注意弹层按钮不能挂 ToolTip，气泡会使 IsPointerOver 失真导致误关）
- [x] 输出合成：RenderTargetBitmap 按物理像素合成底图 + 标注后走剪贴板（无标注时直接像素裁剪）
- [x] 快捷键：V/S 切工具，Ctrl+Z/Y，Delete；Esc 级联（取消绘制 → 退出工具/取消选中 → 取消截屏）
- [x] 自测参数 `--test-shape x,y,w,h[,圆角%[,填充[,粗细]]]`
- 暂缓：聚光灯（用户同意）、"反色"（需对底图取反的混合模式，矢量描边做不到）、样式预设 presets.json

## 阶段四：箭头 / 折线工具 ✅（2026-07-25 完成，里程碑 0.4）

- [x] 模型泛化：AnnotationElement 基类（Render/HitTest/状态快照），撤销命令改用 CaptureState/RestoreState 通用快照——后续新元素类型只需实现基类
- [x] LineElement：N 控制点折线；箭头 = 两点折线 + 末端实心三角（模型统一，工具层区分）
- [x] 箭头工具（A）：拖拽创建，画完自动回选择工具；默认起细(3)尾粗(40) + 末端实心三角
- [x] 折线工具（L）：逐次点击加点（移动实时预览下一段）、双击结束、可连续画下一条；Esc/右键取消进行中折线
- [x] 主工具栏"箭头/折线"合并按钮：悬浮弹出子工具选择，按钮图标随最近使用变化
- [x] 端头样式（起/末端独立）：无、实心/空心 × 三角/菱形/圆/方块、双线箭头、破甲箭头，共 11 种；下拉预览复用产线端头绘制代码；端头随该端线宽等比、线身自动为端头让位
- [x] 起端/末端粗细独立（1–100px）：不等粗时渲染为实心渐变带（此时忽略线形——变宽路径上的虚线无良好定义）；等粗时支持全部线形（圆头圆拐角）
- [x] 弧线开关：Catmull-Rom 样条穿过全部控制点（渲染/命中一致）
- [x] 颜色/线形/透明度与形状工具同套实现；箭头与折线各有独立的当前样式槽
- [x] 编辑：控制点圆形手柄拖拽（十字光标）、线身任意处拖拽整体移动、Delete 删除、撤销/重做全覆盖
- [x] 自测参数 `--test-line "x:y;x:y;...[,起端,末端,起粗,末粗,样条,线形,透明度]"`（两点+末端头自动视为箭头）
- [x] UI 迭代：线形/端头选择器改横向子工具条式面板（PaletteButton，悬浮弹出、当前项高亮）；端头预览缩短突出端头本身；起/末端粗细弹层加"应用到另一端"；箭头上下文隐藏"弧线"
- [x] 半透明修复（TROUBLESHOOTING §11 补遗）：渐变箭头的三角/破甲头融合进渐变带单多边形（零重叠零缝隙）；渐变带锐角折点斜切（bevel）防自交尖刺；50% 下四关键点混合系数逐像素验证 = 0.5

## 阶段五：文字工具 ✅（2026-07-25 完成，里程碑 0.5）

- [x] 模型再泛化：BoxedElement 基类（包围盒 + 绕中心旋转），形状/文字共用移动/8 手柄缩放/旋转手柄逻辑
- [x] TextElement：矩形内多行纯文本（宽度自动换行、超出矩形裁剪）；字体、字号（物理像素，范围默认 9–50、settings.json 的 FontSizeMin/Max 可改）、颜色、加粗、斜体、水平/垂直对齐（左中右 × 上中下）、内边距
- [x] 外框（背景矩形）：颜色/圆角 %/透明度/内边距，子菜单弹层设置；透明度烘入颜色 alpha（单图元，零重叠）
- [x] 文字描边：颜色/粗细（0=无）/透明度，BuildGeometry 描边画在填充之下
- [x] **就地编辑用叠加真实 TextBox**（透明底、样式同步、压掉 Fluent 悬停/聚焦底色）——IME/光标/选择原生支持；旋转元素编辑时编辑框以同角度显示（RenderTransform）
- [x] 交互：文字工具（T）点击创建默认框并立即编辑；选择工具下**点内部=进编辑改光标、点边框环带=拖拽移动**；空文本自动删除；点框外/Esc/右键提交；编辑中屏蔽全局快捷键（Ctrl+Z 归编辑框）
- [x] 工具栏文字属性行：字体列表（约 10 项可见滚动、无字体预览）、字号弹层、B/I、双对齐面板、外框/描边子菜单、色板
- [x] 自测参数 `--test-text "x:y:w:h:字号:旋转:外框:描边|文本"`（%20=空格 %0A=换行）
- [x] 元素右键图层菜单（上移/下移/置顶/置底，ReorderElementCommand 可撤销）；编辑中选中框+手柄常显；编辑框带边框/占位符、编辑中样式实时同步
- [x] 自定义颜色选择器（ColorPickButton 复用组件，内嵌官方 ColorView、禁 Alpha、仅点击弹出、light-dismiss）：三工具行色板 + 文字两个子菜单 + 设置窗口色槽；取色实时生效、合并单条撤销；嵌套弹层用 ColorPickButton.AnyOpen 防外层看门狗误关
- ~~文字整体透明度~~ 已于 0.7 补做（属性行滑条，文字/文本框/描边同调，编辑中实时预览）；样式预设 presets.json 仍待后续

## 发布形态：NativeAOT 兼容改造 ✅（2026-07-25 完成）

- [x] 剪贴板写入 OLE/COM → 纯 Win32（SetClipboardData + HGLOBAL，字节与格式不变）；移除 BuiltInComInteropSupport
- [x] settings.json 改 System.Text.Json 源生成（文件格式不变）
- [x] NativeAOT 发布打通（vcvars64 + IlcUseEnvironmentalTools 绕过 VS 2026 探测问题）；`aot-win.bat` / `release-singlefile-win.bat` 发布脚本（GBK+CRLF）
- [x] AOT 产物全量回归（剪贴板三场景、各元素渲染、ColorPicker 主题、配置读取）

## 阶段六：编号工具 ✅（2026-07-26 完成，里程碑 0.6）

- [x] NumberElement：圆心 + 编号值 + 样式（不继承 BoxedElement——无缩放/旋转语义）；徽章直径恒定，标号变长时内部文字按比例缩小适配
- [x] 形制：实心圆（标号色按圆色灰度 ≥0.6 自动取黑/白）/ 空心圆（环与标号同色，环粗随直径等比）
- [x] 标号类型 6 种（123/ABC/abc/i ii iii/I II III/一二三），每种独立序列（EditorState 按类型记"下一个编号"，切换类型各自续接）；字母 Excel 列名式、罗马 >3999 与汉字 ≥10000 退回阿拉伯数字
- [x] 编号工具（N）：点击放置（按住可拖至准确位置），工具保持激活连续盖章；Esc/右键取消当次并回退序列
- [x] 工具栏编号属性行：形制面板、№ 下一个编号（上下小箭头 + 滚轮）、类型面板、大小滑条（直径 10–200px）、色板 + 取色器
- [x] 选中态：仅虚线框，无缩放/旋转手柄；四角真实按钮（可挂 tooltip，NumberActionsPanel 叠加层）——左上 ▲▼ 改值（≥1，可重复，不影响他号）、右上红✕删除（不重排）、右下 ↺ 重置本序列（按当前值稳定排序后从最小值重排连续，BatchMutateCommand 单撤销单元，下一个编号顺延）
- [x] 移动/Delete/右键图层菜单/输出合成沿用通用路径
- [x] 自测参数 `--test-number "x:y[:值[:类型[:直径[:空心[:色]]]]][;…]"`（渲染 7 组合像素级验证：反差色、缩字、空心、各类型格式化、四角按钮）

## 阶段七：区域模糊（像素化 / 模糊化）✅（2026-07-26 完成，里程碑 0.7）

- [x] MosaicElement（BoxedElement 派生）：矩形区域对**冻结帧**做像素化或模糊化；无描边；缓存处理结果位图，几何/样式变化时按 (Bounds, Style) 键重建
- [x] 像素化：块 1–50px 默认 10，块网格锚定元素左上角；模糊化：半径 1–50px 默认 10，可分离箱式模糊 ×2 近似高斯（边缘按半径外扩取样）
- [x] 抗逆向：像素化前按元素固定种子**跨块**随机像素交换（约 1/4 像素 ±块距互换；块内乱序不改变均值故必须跨块）；模糊后叠加 ±2 级确定性噪声破坏反卷积；种子存元素、重绘稳定
- [x] **永远最底层**：模型列表头部"模糊前缀组"（创建插入组尾、AddElementCommand 记录 index 保证重做层序、右键图层菜单限制组内调序）；只采样底图，不影响其他标注
- [x] 工具栏"区域模糊"合并按钮（像素化 M / 模糊化 B，悬浮弹出子选择，同箭头/折线模式）；属性行按当前模式显示"像素大小"或"模糊半径"滑条
- [x] 交互同矩形：拖拽创建（复用 CreateShape 泛化到 BoxedElement）、8 手柄缩放、旋转（块保持轴对齐、按旋转矩形裁剪）、移动、Delete、撤销/重做
- [x] 自测参数 `--test-mosaic "x,y,w,h[,模糊0/1[,强度[,旋转°]]][;…]"`（10/30px 马赛克、模糊 20、标注压模糊层序均已截图验证）

## 阶段八：画笔工具 ✅（2026-07-26 完成，里程碑 0.8）

- [x] PenElement：自由笔迹（拖拽采点，2 物理像素步距抽稀；圆头圆拐角 Pen 描线；单点=圆点戳记）；选中=虚线包围盒，移动整体平移，不提供缩放/旋转/顶点编辑
- [x] 荧光笔：与底图正片叠底——采样冻结帧 × 画笔色位图 + 笔迹带状几何裁剪（两侧等距偏移 + bevel 折点 + 半圆端帽，NonZero 填充防自交孔洞）；缓存按 (样式,点数,首尾点) 键重建；只叠底图（同马赛克语义）
- [x] 工具栏画笔属性行：粗细（1–100 默认 6）、荧光复选、色板 + 取色器；工具行按 TOOLS-SPEC 顺序置于"选择"之后（P）
- [x] 自测参数 `--test-pen "x:y;...[,粗细[,荧光[,色]]][|…]"`（实心波浪/荧光划字/来回涂抹三场景截图验证：黑字保持黑、白底变色、无孔洞）
- 注：橡皮将按 §9 组遮罩实现（全部标注一次裁剪，无需逐笔迹处理），画笔无需特殊结构

## 阶段九：图章工具 ✅（2026-07-26 完成，里程碑 0.9）

- [x] StampElement（BoxedElement：移动/8 手柄/旋转全套）；快照存素材路径 + Bounds + 样式
- [x] 素材库 StampLibrary：目录即库（%AppData%\Zhuoying\stamps）、内置 6 个 SVG（.initialized 标记防复活）、导入=复制入库；工具栏素材选择器（缩略图横条 + ＋导入 StorageProvider 文件对话框）
- [x] StampRasterizer：SVG 走 Svg.Skia 5.1（SkiaSharp 升 3.119，Avalonia 11.3 渲染实测兼容）按目标尺寸矢量光栅化；PNG/JPG SKBitmap 解码 + Mitchell 重采样；统一输出预乘 BGRA
- [x] 轮廓描边：alpha chamfer 距离变换（两遍 O(n)），0<d≤宽填色、外缘 1px 渐隐抗锯齿，含镂空内缘；描边铺底 + 主体 premul over + 整体透明度烘入 = **单张合成位图**（零重叠，§11）
- [x] 尺寸自适应：合成缓存按 (路径,样式) 键 + 尺寸偏离 >25% 重光栅化（SVG 任意放大锐利，700px 星形验证）
- [x] 命中按合成 alpha 采样（透明区点击穿透，slop 范围粗采样）
- [x] AOT 回归通过（Svg.Model 有 IL2104 裁剪警告但 SVG 光栅化实测正常）
- [x] 自测参数 `--test-stamp "x,y,w,h[,旋转[,描边宽[,透明度]]]|素材名或路径"`

## 阶段十：橡皮 + 笔刷光标 + 放大镜 + 保存 ✅（2026-07-26 完成，里程碑 0.10）

- [x] 保存（Ctrl+S）：PNG 落设定目录（AppSettings.SavePath，空=图片\捉影；设置窗口文本框+浏览）；时间戳文件名重名加序号；Avalonia Save → PngDpiWriter.WithDpi 写 pHYs（DPI 取选区覆盖面积最大屏）；保存后关会话
- [x] 另存为（Ctrl+Shift+S）：StorageProvider.SaveFilePickerAsync；取消回会话；工具条 保存/另存为 按钮（复制旁）
- [x] 自测参数 `--test-save x,y,w,h`（验证：文件生成、2000×1300 物理像素、192dpi、标注合成、会话关闭）

- [x] EraserElement：只擦画笔/荧光笔（用户定稿，取代旧"组遮罩"草案）；不渲染自身、不可选中，BuildClip 汇总全部橡皮带 → CombinedGeometry(Exclude)，AnnotationLayer 与输出合成渲染 PenElement 时统一套用；仅撤销可恢复
- [x] StrokeGeometry 提取共享（画笔荧光裁剪 / 橡皮带共用带状几何）
- [x] 橡皮属性行只有大小（1–100 默认 24，跨会话记忆 StyleMemory.Eraser）
- [x] 画笔/橡皮圆形光标：直径=粗细、画笔填色（荧光半透明）、橡皮半透明灰、区域模糊同款可见性边框；按参数缓存 RTB 光标（上限 64 清空）
- [x] 放大镜（PixPin 式）：21×13 源像素 ×10 DIP 网格放大（层级 NearestNeighbor）+ 中心像素黑白双圈 + 坐标/色块/#RRGGBB/C 键复制颜色；光标右下 22 DIP、越界翻转、屏内钳位；仅 选择工具 && 无选中元素 时显示；逐帧仅一次小 DrawImage + 单像素读取，无感知开销
- [x] 自测参数 `--test-eraser "x:y;...[,粗细]"`（2.6s 晚于画笔钩子）；验证：橡皮竖穿实心笔+荧光+矩形——笔迹断开、矩形完好

## 阶段十一：窗口吸附 / 右键重置 / 反色 / 持久化 / 生命周期 ✅（2026-07-26 完成，里程碑 0.12）

- [x] 窗口吸附：IScreenCapture.GetVisibleWindowRects（EnumWindows + DWM 扩展边界去阴影，滤最小化/隐身/WS_EX_TRANSPARENT 穿透层；抓帧同瞬间快照故不含遮罩自身）→ SelectionController WindowPick 模式（默认态 hover 绿框高亮+尺寸标签、点击吸附、拖拽超阈值转手动框选）
- [x] 右键重新开始捕捉：会话被改动时右键=整体重置（AnnotationModel.Reset + 选区 ResetToDefault + 编号序列清零 + 回选择工具，吸附恢复）；初始态再右键才退出；原级联保留
- [x] "反色"颜色：InvertPaint.Sentinel（alpha=1 黑哨兵）；形状=描边环带/填充块圆角几何裁剪取反位图，画笔=荧光同管线反色优先；形状/画笔色板加黑白对角特殊块；`--test-shape` 参数 11 / `--test-pen` 色值 INV
- [x] presets.json：StyleMemory 落盘（PresetsService + 源生成 + ColorJsonConverter "#AARRGGBB"；会话结束与退出时写、启动读）；注意源生成对 init 属性缺字段=default 而非初始化器值——文件始终全字段写出，手改需保留全部字段
- [x] 开机自启（StartupManager，HKCU Run 键，设置窗口开关）；通知气泡（NotificationToast 自绘置顶 toast，保存成败/热键冲突接入）
- [x] Alt+点击下钻重叠元素下一层（直接点击保持"拖动已选元素"语义不变）
- [x] "连续绘制"开关降级不做（各工具行为已按语义固化）

## 阶段十二：钉屏贴图窗口 ✅（2026-07-27 完成，里程碑 0.13）

- [x] PinWindow：无边框置顶小窗承载选区合成图（叠加式 1px 蓝边不挤占布局）；初始位置=选区原位、物理像素 1:1（显示位图必须 96 DPI——§2 老坑复现一次即修；1:1 时最近邻采样，静态区域像素 diff 0/20350）
- [x] 交互：BeginMoveDrag 拖动、滚轮缩放 0.1–8×（10% 步进、100% 附近吸附回 1:1 并切无损采样、窗内百分比提示）、Ctrl+滚轮透明度 15–100%、双击/中键/Esc 关闭
- [x] 右键菜单：复制（CloneWithSourceDpi 标记来源 DPI 走 CF_DIB 管线）/另存为（PNG+pHYs）/关闭/关闭全部（静态列表管理，退出全销）
- [x] ScalingChanged 时按物理尺寸重算窗口（跨屏拖动不跳变）；F3 + 工具条图钉按钮；`--test-pin x,y,w,h`

## 阶段十三：DDA 抓屏后端 ✅（2026-08-09 完成，里程碑 0.14）

- [x] DesktopDuplicator：DXGI Desktop Duplication 为主抓屏——解决真·独占全屏 /
  MPO 硬件叠加层黑屏、HDR 洗白；全部 COM 调用为手写 vtable 函数指针
  （delegate* unmanaged），NativeAOT 直接可用
- [x] 结构：D3D11 设备按适配器常驻预热（纯渲染卡跳过）；显示器每次抓屏现枚举
  （热插拔/改分辨率天然正确）；duplication 会话即建即弃（不长期禁用 MPO、
  不占会话名额）；IsCurrent 检测适配器变化时重建
- [x] 回退：单输出失败（远程桌面/受保护内容/旋转屏/会话占满/无合成帧超时）
  仅该区域回退 BitBlt（矩形减法拆分）；设备级异常整块回退并重建实例；
  BitBltInto 抽出为按矩形写入共享缓冲的帮助函数
- [x] **只接受 LastPresentTime≠0 的真实合成帧**（TROUBLESHOOTING §12：新会话
  首帧可能是"未播种"仅指针帧，S_OK 但纹理全黑；静止桌面正确回退 BitBlt）
- [x] HDR：FP16 桌面按显示器 SDR 白电平（DISPLAYCONFIG 查询，失败按 240 nits）
  归一 + sRGB 编码，65536 项 half→byte LUT 按显示器缓存
- [x] 自测 `--test-dda "x,y,w,h[|输出目录]"`（每屏 8×8 WDA_EXCLUDEFROMCAPTURE
  闪烁小窗制造合成活动）：全虚拟屏（双 4K 混合 DPI）backend=dda、与 BitBlt
  diff=2930 ≈ GDI 自身 130ms 双抓本底 2921（即完全一致）、预热后 48ms vs
  BitBlt 132ms（约 3 倍）；--test-copy 回归与 AOT 回归通过

## 阶段十四：Agent API ✅（2026-08-09 完成，里程碑 0.15）

- [x] 无头 CLI（`Agent/AgentCli.cs`）：`--api-monitors` / `--api-windows`（窗口
  枚举核心抽出 EnumVisibleWindows 共享，加标题）/ `--api-capture "x,y,w,h|full"
  [--out 路径]`；Program.Main 在单实例互斥**之前**分流，第二进程免 UI 即抓
  即退；AttachConsole 附加父控制台（WinExe 无控制台补偿）
- [x] 抓取复用 DDA→BitBlt 产线管线（CapturePng 直写 byte[]，不初始化 Avalonia
  平台）；自带 MiniPng 编码器（BGRA→RGB、zlib/Fastest、pHYs DPI；
  坑：PNG 签名不能写 `"\x89..."u8`——UTF-8 编码把 \x89 变两字节）
- [x] MCP stdio 服务器（`--mcp`，手写 JSON-RPC 2.0 而非官方 SDK——只需 4 个
  方法，JsonDocument+Utf8JsonWriter 零反射 AOT 友好零依赖）：initialize/ping/
  tools/list/tools/call；工具 take_screenshot（region > window_title > monitor >
  全虚拟屏，返回 base64 PNG image content）/ list_windows / get_monitors；
  工具失败按 MCP 语义回 isError 结果（坑：stdio 按 newline 分帧，
  inputSchema raw JSON 必须单行）
- [x] 安全边界：只"看"不"动"；AppSettings.AgentApiEnabled 默认 false，
  设置窗口「允许 Agent API」开关；未启用时 exit 2 + `{"error":"agent_api_disabled"}`
- [x] 验证：门禁 exit 2；monitors/windows JSON 正确（双 4K 拓扑）；capture PNG
  640×480@192dpi 内容正常；MCP 全会话（init/list/get_monitors/截图 base64
  解码为合法 PNG/window_title 不存在报 isError）；AOT 回归通过
- 暂缓：pin_image 工具（贴图需与托盘实例 IPC，Agent API 进程无 UI——
  留待"唤起已有实例"通知机制落地后一并做）

## 后续阶段（概要）

- REQUIREMENTS v1 主体功能已全部落地（仅聚光灯暂缓），收尾后可升 1.0
- [ ] **录屏（WGC 视频捕捉）**：Windows.Graphics.Capture 会话式持续取帧
  （最低系统要求 Win10 1903 恰为其门槛）；圈定区域后活屏录制——需新的
  非冻结帧模式（透明边框窗 + 控制条，SetWindowDisplayAffinity 排除自身）；
  编码输出 GIF（ImageSharp，帧间差分+流式写盘）先行，MP4（Media Foundation
  H.264）后续；抓帧管线按双输出设计

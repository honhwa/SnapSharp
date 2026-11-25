# 架构与依赖参考

异常捕获、热键单路径与 ONNX 子进程边界见[异常与推理隔离](异常与推理隔离.md)。

> 最后按代码校准：2026-11-19。本文描述实际模块、运行边界与依赖采用规则；功能验收状态集中在路线图。

[文档中心](README.md) · [构建与测试](构建与测试.md) · [功能规格与路线图](<NimCap Screenshot 功能规格与路线图.md>)

[模块与链路](#2-解决方案结构) · [插件边界](#4-插件边界) · [依赖清单](#dependencies) · [候选组件](#candidates) · [分发规则](#distribution)

## 1. 架构目标

NimCap Screenshot 是 Windows 桌面截图工具。主截图链路必须保持低延迟、可离线运行，并在捕获后端失败时可降级。项目按稳定职责拆分程序集，而不是为每个功能建立项目：

- 捕获、选区、编辑、剪贴板和本地保存属于核心主流程。
- OCR、条码、SLANet 表格及 LaTeX-OCR 公式已有内置实现，专用识别器仍可通过受控插件契约扩展；通用上传、后处理和动作链仍属于后续能力。
- WPF 负责交互预览，SkiaSharp 负责最终像素合成与编码；两条渲染路径共享同一标注模型。
- 捕获使用虚拟桌面物理像素，WPF 使用 DIP，跨边界时必须显式转换。

## 2. 解决方案结构

| 项目 | 当前职责 | 直接依赖 |
|---|---|---|
| `NimCap.Screenshot.Core` | 捕获结果、像素矩形、配置、历史条目和启动参数 | 无其他 NimCap Screenshot 项目 |
| `NimCap.Screenshot.Imaging` | 标注对象、WPF/Skia 绘制、图像变换、编码、缩略图和标注模板 | `Core` |
| `NimCap.Screenshot.Editor` | 编辑状态、对象集合、选择状态、撤销/重做和样式属性 | `Core`、`Imaging` |
| `NimCap.Screenshot.Capture` | GDI、WGC、DXGI/Direct3D 互操作、窗口枚举、滚动采集和坐标映射 | `Core`、`Imaging` |
| `NimCap.Screenshot.Recording` | 录制引擎、设备枚举、录制时钟、编码探测、流式 GIF 编码和输出路径 | `Core`、`Imaging` |
| `NimCap.Screenshot.Recognition` | 独立进程 ONNX CPU / DirectML GPU 推理、PaddleOCR、Windows OCR、条码、抠图与表格导出 | `Core`、`Imaging`、`Infrastructure`、`Plugin.Abstractions` |
| `NimCap.Screenshot.Infrastructure` | JSON 设置、PNG 历史、剪贴板适配器和应用数据路径 | `Core`、`Imaging` |
| `NimCap.Screenshot.Plugin.Abstractions` | 图像处理、识别、输出和工作流动作的稳定契约 | 无其他 NimCap Screenshot 项目 |
| `NimCap.Screenshot.PluginHost` | 插件发现、强名称策略、独立加载上下文和能力注册表 | `Plugin.Abstractions` |
| `NimCap Screenshot` | WPF 视图、托盘、热键、单实例、录屏编排、在线服务授权和应用生命周期 | 上述运行时项目；窗口捕获排除仍直接使用 ScreenRecorderLib |
| `nimcap`（`Cli`） | 独立控制台入口、参数校验、捕获子进程、JSON 结果和原子文件输出 | `Capture`、`Infrastructure`，传递引用 `Core`、`Imaging` |

依赖方向保持为 Shell 指向功能类库。功能类库不得反向引用主程序、`Views` 或具体窗口。

## 3. 当前运行链路

### 3.1 启动与触发

1. `App` 建立单实例互斥体和命名管道。
2. `AppController` 加载设置、插件目录、托盘和全局热键。
3. 热键、托盘或命令行被归一化为 `CaptureMode`。
4. 捕获前临时隐藏 NimCap Screenshot 自身窗口，避免应用界面进入截图。

`LaunchCommand` 已解析 `-region`、`-full`、`-window`、`-delay` 和 `-clip`。当前 `CopyToClipboard` 尚未传入输出决策，因此 `-clip` 只完成了解析和转发，不是完整无交互剪贴板流程。

独立 `nimcap.exe capture` 使用 `CliCommand`，与桌面启动参数分离。父进程校验参数和输出冲突，启动隐藏的 STA 捕获子进程，通过重定向标准输入发送请求、二进制标准输出接收图像及物理边界。取消或超时终止子进程树，父进程通过 `OutputFileService.Write` 原子提交显式目标文件，再输出 JSON 或路径并退出。CLI 不创建 `AppController`，不加载设置或写入历史；已有 GUI 窗口也不会被主动隐藏。协议、退出码及运行限制见 [CLI](CLI.md)。

`DesktopInventory` 为 `windows`、`monitors` 与捕获选择器提供统一快照；显示器 DPI 复用捕获库内部 `DisplayMonitorService`，通过定向友元程序集访问，不扩大公共 API。窗口进程/标题筛选及显示器编号在 STA 子进程内、延时之后解析。`doctor` 单独报告桌面打开、目录临时写入和各后端的真实 1×1 内存捕获/编码探针；探针复用同一进程协议，每项有独立期限，部分失败不会被自动回退结果掩盖。

### 3.2 捕获

`ResilientCaptureService` 以 `ICaptureService` 隔离调用方：

- 首选 `WgcCaptureService`，通过 Windows Graphics Capture 获取显示器或窗口帧。
- WGC 不可用、超时或失败时回退 `ScreenCaptureService` 的 GDI `BitBlt`。
- 全虚拟桌面由各显示器物理矩形拼接，保留负坐标显示器关系。
- 活动窗口在启动捕获前记录目标句柄，避免遮罩窗口抢占前台焦点。

`DxgiCaptureService` 已存在，但当前主配置选择链路使用 WGC/GDI；不要仅因类存在就把 DXGI Desktop Duplication 描述为默认生产后端。

### 3.3 区域选择与内联编辑

区域模式为每个显示器创建一个 `SelectionWindow`。选区完成后不立即关闭窗口，而是在选区上进入内联编辑：

- 支持矩形、椭圆、自由套索和多边形选区。
- 编辑底图使用原始物理像素裁剪；遮罩、手柄和工具栏使用 WPF DIP。
- “元素”模式通过 `IElementDetectionService` 查询 `ElementCandidate`，Win32 子句柄命中作为基础，UI Automation 补充无独立 HWND 的控件和浏览器辅助功能元素。使用遮罩出现前按 Z 序保存的目标窗口，在其子树内查询，不对桌面遮罩调用 UIA `FromPoint`。不添加浏览器扩展，也不自动开启调试端口；CSS 选择器和视口外 DOM 元素截图尚未提供。
- UIA 在单个 MTA 后台线程执行，等待队列仅保留最新请求，查询有遍历预算和等待超时；不可中断的异常 provider 不占用界面线程，也不会无限创建工作线程。会话关闭、进入编辑、切换模式和目标变化时取消查询，响应还需通过请求编号、目标窗口及光标位置校验。输出始终裁剪原始底图；移动后的窗口不再接受其新几何。候选按包含关系去重，滚轮在元素、父级和窗口间切换。
- `AnnotationSurface` 负责绘制预览、命中测试、移动、缩放、旋转、顶点编辑和吸附。
- 主工具栏选择工具；二级工具栏按当前工具或选中对象动态显示形状、填充、线型、粗细、颜色、字号和效果参数。
- 完成新标注后自动切换到选择模式，标注可立即移动或调整。

### 3.4 标注与渲染

`Annotation` 是不可变 record 层次结构。几何、颜色、粗细、旋转和线型保存在对象本身：

- WPF `Draw` 用于交互预览。
- Skia `DrawSkia` 或专用 `ApplySkia` 用于复制、保存和历史输出。
- 实线、虚线和点线必须在两条路径保持一致。
- 马赛克、模糊、聚光灯和放大镜使用专用效果参数，不复用普通边框粗细。
- `.sstemplate` 保存归一化对象坐标、旋转和样式；旧模板缺少新字段时回退为兼容默认值。

`EditorViewModel` 使用完整对象快照实现撤销/重做。一次拖动在松开时提交；二级栏滑块当前按值变化提交，后续可在 `DragCompleted` 合并为单个历史操作。

### 3.5 输出与本地数据

- `SkiaImageService` 负责 PNG、JPEG、WebP、TIFF、BMP、PDF 输出，以及旋转、翻转、画布扩展和缩略图。
- `ClipboardService` 使用 `Clowd.Clipboard.Wpf` 提升常见 Windows 应用中的图像粘贴兼容性。
- `HistoryService` 保存合成后的 PNG、缩略图和 JSON 索引，按条数清理。
- 当前历史没有保存可编辑标注对象，也没有日期/尺寸搜索、收藏或磁盘配额策略。

### 3.6 长截图

`CaptureCoordinator.SelectScrollingRegionAsync` 供托盘、编辑窗口及长截图重新采集共用普通截图的多屏选区，确认时返回原始裁剪像素与屏幕物理区域，取消时恢复原窗口。`ScrollCaptureWindow` 编排手动滚动采集、退出、预览与输出。`ManualScrollInput` 在会话期间监听选区内真实滚轮和 Esc，不拦截滚轮，也不注入滚动；`ScrollingCaptureService` 通过可注入等待回调逐帧捕获，预览同时提供自动采集入口。右上角 × 丢弃，√ 采集最后一帧后预览；悬浮条使用捕获排除。采集和像素重叠匹配复用 Capture／Imaging 能力。会话固定使用物理像素区域，保留原始帧，预览允许裁剪上、下、左、右边缘：上下删除整行，左右删除整列，不能从中间挖除，裁边、裁剪与接缝位移为可还原参数。`.ssscroll` 保存原始 PNG 帧及已应用参数，加载时检查版本、归档内容与资源上限。使用与限制见[使用指南](使用指南.md)。

### 3.7 识别与抠图

托盘“识别粘贴板图片…”通过 `IClipboardService.GetImage` 读取格式感知的独立像素快照，调用结果窗口时禁用自动运行；截图 OCR／抠图入口仍按原有流程自动处理。窗口保留统一模式选择，翻译消费文字而不是直接消费图片。

`CutoutModels` 集中维护六个下载预设；`AppSettings.CutoutModels` 分模型保存路径、契约和尺寸，`CutoutDefaultModelId` 保存默认项。窗口临时选择使用配置快照，不修改默认；旧单模型配置有迁移逻辑。General / Portrait 使用 sigmoid + min-max，Matting 使用 sigmoid，配套 HR Matting 的 `BiRefNetAlpha` 直接保留概率输出。下载服务流式落盘，执行固定校验和 ONNX 契约检查后回填设置。

PaddleOCR、表格、公式与抠图通过 `OnnxWorker` 在每次操作独立启动的进程中执行，主进程不创建 ONNX 原生会话。图片与结构化结果经当前用户专用命名管道传输；同次操作最多缓存四个会话，完成、失败、取消后释放／终止 worker。DirectML 失败或显存预算不足可回退 CPU；出错适配器在本次启动期间后续使用 CPU，不改变保存的偏好。跨请求重载增加延迟，实际 HR 模型和多品牌 GPU 仍需验收。详见[异常与推理隔离](异常与推理隔离.md)。

`RecognitionWindow` 提供编辑与图像定位，`LocalRecognitionService` 选择 PaddleOCR、Tesseract、Windows OCR 或 ZXing 条码。`OnnxImageRuntime` 在后台串行推理，限制线程、超时和会话缓存；输入和结果统一用原图像素坐标。模型为本地文件，不隐式下载。设置页通过 `OcrModelDownloadService` 主动下载固定版本中英文包，限制大小、校验 SHA256，仅提取明确命名的 ONNX 文件并从模型元数据生成字典，验证后回填配置；失败不改原路径。翻译源／目标语言分别配置，默认 `en → zh`。

内置识别器以 `nimcap.screenshot.ocr`／`nimcap.screenshot.barcode` 注册到插件目录。表格默认由 `TableRecognition` 解码 SLANet 结构与合并单元格，再调用所选 OCR 并匹配文字；公式由 `FormulaRecognition` 执行尺寸预测、编码器和有界自回归解码，输出 LaTeX。`SpecialistModelDownloadService` 提供固定大小／SHA256 的配套模型安装。表格／公式也可按已配置 ID 使用外部插件；缺失模型明确提示配置，不降级冒充识别结果。抠图保留原始分辨率，并将蒙版与已有透明度相乘。

`OnlineRecognitionService` 仅在保存的隐私开关与逐次确认同时满足后发送请求；默认禁用重定向。`ScreenWordService` 为会话级离线局部 OCR，受鼠标停留、任务并发、超时和应用活动状态限制。本地翻译由 `LocalTranslationService` 启动按需下载的 llama.cpp 子进程，通过带随机鉴权的本机回环端口交互，结束、取消或超时后收尾；不把本地路径作为 API 授权。模型契约、引擎选择和 API 边界见[配置与依赖](配置与依赖.md)。

### 3.8 录屏

录制引擎 `RecordingService` 位于 `NimCap.Screenshot.Recording`，由主程序的 `RecordingWindow` 编排，不复用截图后端的单帧循环。连续 WGC、原生帧时间戳、WASAPI 混音和摄像头叠加由 ScreenRecorderLib 管理；H.264/AAC 使用系统 Media Foundation。设备枚举、录制时钟、编码探测、GIF 编码和输出路径也属于录屏类库；窗口、倒计时、设置保存、环境安装及交互自检保留在主程序。共享配置与状态枚举仍位于 `Core`。

应用通过显式状态机与 `RecordingClock` 管理有效录制时间、暂停、定时、停止和收尾。`RecordingService` 显式接收调用方提供的单线程 `SynchronizationContext`：构造、命令调用、属性读取和 `Changed` 通知都在该上下文所属线程执行（`FrameCount` 可跨线程读取）。原生回调和后台定时器只投递工作，待处理的定时通知最多保留一次，收尾时释放定时器；调用方须保持上下文运行至 `Completion` 完成。WPF Shell 使用 `DispatcherSynchronizationContext` 适配，录制引擎不直接引用 `Dispatcher` 或 `DispatcherTimer`。类库仍使用 WPF 位图与 WinForms 显示器信息，因此不是跨平台库。GIF 转码在后台逐帧进行；`StreamingGifWriter` 使用有界调色板、抖动和 LZW，不引入 FFmpeg 或 Magick.NET。输出先保存在本次专用临时目录，成功后不覆盖移动至目标路径，失败保留临时数据。设备和输出限制见[使用指南的录屏章节](使用指南.md#recording)，环境安装见[配置与依赖](配置与依赖.md#recording)。

`RecordingDeviceService` 通过检查 HRESULT 的 Windows Core Audio COM 接口枚举录制／播放端点，按线程初始化和释放 COM；摄像头使用 Windows 设备枚举 API。避免调用录屏库可能发生原生访问违规的设备列表接口。空设备列表是正常状态，单类枚举失败不阻止无该设备的录屏；枚举不采集音频或摄像头内容。

## 4. 插件边界

当前契约包含：

- `IImageProcessorPlugin`：可选滤镜或后处理。
- `IRecognitionPlugin`：OCR、二维码、条码、公式等识别。
- `IOutputPlugin`：用户授权后的上传、Webhook 或企业输出。
- `IWorkflowActionPlugin`：动作链中的独立步骤。

插件宿主发现 DLL、验证能力 ID、按策略拒绝无强名称程序集并通过 `AssemblyLoadContext` 解析私有依赖。识别窗口已有针对配置 ID 的专用调用入口，但没有覆盖所有插件能力的通用 UI。外部 DLL 运行时目录为 `<数据目录>\Plugins`；源码和插件项目放在仓库 `Libraries`。插件仍运行在主进程内，不能视为安全沙箱。

不应插件化的部分：捕获后端选择、选区交互、基础编辑对象、设置、历史、剪贴板以及常用标注工具。

## 5. 安全与许可边界

- `EnableExternalPlugins` 默认关闭；`RequireSignedPlugins` 默认开启。
- 强名称只用于程序集身份和完整性提示，不等同于可信发布者签名。
- 网络、凭据和图像上传必须由功能层再次获得明确授权，不能因插件成功加载而默认允许。
- GPL 项目可以用于行为和架构研究，但不得直接复制源码到许可证不兼容的发布物。
- FFmpeg、OCR 模型、原生运行库和预编译二进制必须分别检查许可证、再分发条款和模型授权。
- 真正执行不可信插件时，应增加独立进程、受限 IPC、超时、取消、资源上限和最小权限，而不是放宽当前进程内策略。

## 6. 新功能放置规则

| 新能力 | 放置位置 |
|---|---|
| 稳定领域数据、配置字段、启动参数 | `Core` |
| 图像算法、编码、标注类型或模板字段 | `Imaging` |
| 编辑命令、选择状态、历史合并策略 | `Editor` |
| 捕获后端、窗口/显示器互操作、滚动采集 | `Capture` |
| 连续录屏、录制设备、视频/GIF 编码及录制输出路径 | `Recording`；窗口和用户流程由 Shell 编排 |
| 设置、历史索引、凭据或本地持久化适配器 | `Infrastructure` |
| OCR、条码、抠图及模型适配 | `Recognition`；可选专用识别器使用插件 |
| 上传、后处理、工作流能力 | 优先插件项目；授权与用户流程由 Shell 编排 |
| 窗口、托盘、用户流程和跨模块编排 | WPF Shell |

新增功能必须先说明坐标单位、线程边界、取消方式、错误降级、持久化格式和许可证影响。

## 7. 验证入口

构建命令、通用自检、实时桌面采集、真实模型与录屏环境检查统一见[构建与测试](构建与测试.md)。自动测试不替代设备、DPI、性能和实际模型精度验收。

<a id="dependencies"></a>

## 8. 已采用的依赖

### NuGet 组件

| 组件 | 当前用途 | 许可证 | 采用说明 |
|---|---|---|---|
| [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm) | 编辑器属性通知和命令 | MIT | 已使用 |
| [SkiaSharp](https://www.nuget.org/packages/SkiaSharp) | 图像合成、特效、缩放和编码 | MIT | 已使用；包含原生运行库 |
| [Clowd.Clipboard.Wpf](https://www.nuget.org/packages/Clowd.Clipboard.Wpf) | Windows 图像剪贴板兼容 | MIT | 已使用 |
| [MahApps.Metro.IconPacks.Lucide](https://www.nuget.org/packages/MahApps.Metro.IconPacks.Lucide) | 工具栏图标 | MIT / ISC | 已使用 |
| [Vortice.Direct3D11](https://www.nuget.org/packages/Vortice.Direct3D11) | WGC 的 D3D11 / WinRT 设备互操作 | MIT | 已使用；DXGI 由依赖关系提供 |
| [Microsoft.ML.OnnxRuntime.DirectML](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML/1.24.1) | 本地 OCR 与抠图 CPU / DirectML GPU 推理 | MIT，原生依赖另有声明 | 已使用；模型许可独立，OCR 可一键下载 PP-OCRv4，抠图可一键下载 U²-Net 小型版 |
| [Microsoft.AI.DirectML](https://www.nuget.org/packages/Microsoft.AI.DirectML) | 主程序复制 DirectML 原生运行库 | 二进制适用 Microsoft DirectML 分发条款；源代码许可与第三方声明另附 | 主程序直接引用，避免依赖系统中的 DirectML.dll；发布包保留原始许可与声明 |
| [Tesseract](https://www.nuget.org/packages/Tesseract) | 可选离线 OCR 引擎 | Apache-2.0 | 已使用，原生引擎来自 Charles Weld 的 Tesseract NuGet 分发；官方 tessdata_fast 简中／英文字库按需下载并固定 SHA256 校验 |
| [GTranslate](https://www.nuget.org/packages/GTranslate) | 可选 Google 在线翻译适配器 | MIT | 已使用，包随程序提供；不是离线翻译模型，服务可用性与额度由第三方决定；逐次确认接收地址与文字 |
| [ZXing.Net](https://www.nuget.org/packages/ZXing.Net) | QR 与指定一维码离线解码 | Apache-2.0 | 已使用；限制格式、旋转重试及输入大小 |
| [WPFLocalizeExtension](https://www.nuget.org/packages/WPFLocalizeExtension) | 多语言界面与运行时切换 | Ms-PL | 已使用；共享 RESX 资源位于 Core，新增语言及校验见[本地化](本地化.md)；传递依赖 XAMLMarkupExtensions 同为 Ms-PL |
| [ScreenRecorderLib](https://www.nuget.org/packages/ScreenRecorderLib) | 连续 WGC／DXGI 录屏、WASAPI 音频、摄像头叠加和 Media Foundation 编码 | MIT | 已使用；VC++ x64 官方下载／签名验证和 N/KN 媒体组件系统安装提供一键配置；合成图实际编码检查；系统组件及编解码器许可独立 |

版本由各 `.csproj` 和 NuGet 还原结果管理，文档不重复维护易过期的版本号。

### 按需下载的运行库与模型

| 组件 | 当前用途 | 许可证 | 采用说明 |
|---|---|---|---|
| [llama.cpp](https://github.com/ggml-org/llama.cpp) / [Qwen3.5 GGUF catalog](https://huggingface.co/bartowski) | 本地 CPU / Vulkan GPU 翻译 | MIT / Apache-2.0 | 2B / 4B / 9B / 27B Q4_K_M；27B Q5_K_M 仅在检测到至少 24 GiB 专用显存时显示。固定修订、大小与 SHA256 校验；无 API 费用 |

PaddleOCR、Tesseract 字库及抠图权重的固定来源、大小、SHA256 与适配契约统一维护在[配置与依赖](配置与依赖.md)，实际分发义务见[第三方声明](../THIRD-PARTY-NOTICES.md)。

## 9. 已使用的平台 API 与模型参考

| API／项目 | 当前用途 | 限制 |
|---|---|---|
| [Windows.Media.Ocr](https://learn.microsoft.com/uwp/api/windows.media.ocr.ocrengine) | 用户手动选择的 OCR 后端 | 依赖系统语言包，不是 PaddleOCR 失败时的静默回退 |
| [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) | 首选 OCR 模型架构参考，适配 DB 检测与 CTC 识别 ONNX | 没有引入 Python/Paddle 运行时或打包权重；不是所有导出都兼容 |
| [Media Foundation](https://learn.microsoft.com/windows/win32/medfound/microsoft-media-foundation-sdk)／WASAPI | 由 ScreenRecorderLib 使用的系统编码、音频捕获与混音 | 设备、媒体组件与 H.264/AAC 许可需分别核查 |

抠图的 U2Net／ISNet／BiRefNet 名称表示当前适配器预处理契约，不表示已验证其所有权重。模型来源、归一化、输出格式与许可要求见[配置与依赖](配置与依赖.md)。

<a id="candidates"></a>

## 10. 未采用的候选组件

| 功能域 | 候选组件 / API | 许可证 | 建议与限制 |
|---|---|---|---|
| Windows 11 新 OCR | [Windows AI Text Recognition](https://learn.microsoft.com/windows/ai/apis/text-recognition) | Windows 平台 API | 能力和部署要求高于当前基线，作为可选后端，不替换 Windows 10 路径 |
| 长截图特征匹配 | [OpenCvSharp4](https://www.nuget.org/packages/OpenCvSharp4) | Apache-2.0 | 当前灰度重叠算法不足时再引入；原生包体积较大，优先保留轻量实现 |
| 专用表格／公式 | PP-Structure SLANet / LaTeX-OCR | Apache-2.0 / MIT，来源与文件 SHA256 独立记录 | 已内置 ONNX 适配与一键配置，也可选插件；模型按需下载 |
| 大规模历史索引 | [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) | MIT | JSON 在数千条历史记录下成为瓶颈后迁移；先设计 schema 版本和迁移事务 |
| 独立音频管线 | [NAudio](https://www.nuget.org/packages/NAudio) | MIT | 当前音频由 ScreenRecorderLib 处理；只有独立音频需求时再评估 |
| 视频编码互操作 | [FFmpeg.AutoGen](https://www.nuget.org/packages/FFmpeg.AutoGen) | MIT（当前包装层） | FFmpeg 二进制仍保留其原始 LGPL/GPL 许可且取决于构建选项；必须单独审查编解码器专利和再分发 |
| 高级 GIF 优化 | [Magick.NET-Q8-AnyCPU](https://www.nuget.org/packages/Magick.NET-Q8-AnyCPU) | Apache-2.0 | 当前为仓库内流式 GIF 编码；仅在需要更强量化／优化且可接受体积时评估 |
| 网络重试 | [Polly](https://www.nuget.org/packages/Polly) | BSD-3-Clause | 只用于未来上传/Webhook；仅重试幂等或明确可重试动作，必须有超时和取消 |

选择候选组件时依次检查：目标框架兼容、最后维护时间、传递依赖、原生文件、AOT/裁剪影响、安装体积、许可证、模型或二进制授权、离线能力和失败降级。

## 11. 行为与架构参考项目

以下项目只用于研究交互和架构。除许可证允许且完成来源记录外，不复制源码：

| 项目 | 参考范围 | 许可证 / 注意事项 |
|---|---|---|
| [ShareX](https://github.com/ShareX/ShareX) | 截图工作流、选区、长截图和动作链 | GPL-3.0；仅作行为参考 |
| [Flameshot](https://github.com/flameshot-org/flameshot) | 标注工具和快捷交互 | GPL-3.0；仅作行为参考 |
| [ScreenToGif](https://github.com/NickeManarin/ScreenToGif) | 录屏状态机、编辑和导出流程 | MS-PL；采用代码前单独审查兼容性 |
| [Text Grab](https://github.com/TheJoeFin/Text-Grab) | Windows OCR、识别结果编辑和区域映射 | 当前仓库可公开审阅；版本和二进制条款可能变化，采用前再次核对 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 托管 Skia 渲染和编码 API | MIT |

<a id="distribution"></a>

## 12. 引用与分发规则

- 新增直接或传递依赖时同步更新项目文件、本文和根目录 `THIRD-PARTY-NOTICES.md`。
- 发布包必须保留 `ThirdPartyLicenses/`；按需下载的模型和运行库必须在安装目录保留对应完整许可文本，不能只记录 SPDX 名称或网页链接。
- 记录包名、版本、版权所有者、许可证、原生二进制来源和修改情况。
- GPL 项目不得通过“只复制少量代码”规避许可证义务；不确定时只参考可观察行为并独立实现。
- NuGet 包许可证不自动覆盖其下载的模型、FFmpeg 构建、训练数据或其他外部资产。
- 引入云服务 SDK 前必须先完成隐私、凭据存储、数据发送确认、超时、区域合规和退出机制设计。

# NimCap Screenshot CLI

`nimcap.exe` 是独立的 Windows x64 命令行入口，可用于 PowerShell、批处理和 Agent 调用。它随桌面发布包分发；完整解压后使用，无需先启动托盘程序。

## 截图

```powershell
# 全部显示器组成的虚拟桌面
.\nimcap.exe capture --full --output .\screen.png --json

# 固定区域：x,y,width,height，物理像素，支持负坐标
.\nimcap.exe capture --rect "-1920,0,1280,720" --output .\region.png --json

# 延时结束时的前台窗口
.\nimcap.exe capture --window --delay 3 --output .\window.png --json

# 指定句柄：十进制或 0x 前缀十六进制
.\nimcap.exe capture --window-handle 0x123456 --output .\window.png --json

.\nimcap.exe capture --full --output .\screen.webp --overwrite --timeout 10 --json
.\nimcap.exe --help
.\nimcap.exe --version
```

每次指定一个截图目标，`--output` 必填。相对路径以调用者当前工作目录为基准，结果中的路径是绝对路径。扩展名决定编码，支持 PNG、JPG/JPEG、WebP、TIFF/TIF、BMP 和 PDF。保存时会创建缺失的父目录。

## 查找窗口和显示器

```powershell
.\nimcap.exe windows --json
.\nimcap.exe monitors --json
.\nimcap.exe capture --monitor 1 --output .\primary.png --json
.\nimcap.exe capture --process notepad.exe --output .\notepad.png --json
.\nimcap.exe capture --process notepad --window-title "会议记录" --output .\notes.png --json
.\nimcap.exe capture --window-title "会议记录" --output .\notes.png --json
```

`windows` 返回当前可见的顶层捕获候选窗口，过滤工具窗口、隐藏和被系统 cloaked 的窗口。每项包含 `handle`（十六进制字符串，避免脚本整数精度丢失）、`title`、`processId`、`processName`、物理像素 `bounds` 和 `minimized`。进程已退出或信息不可读取时，可能省略 `processName`。列表是瞬时快照，列出不代表目标稍后仍能捕获；最小化窗口可出现在列表中，但不能通过选择器捕获。

进程名精确匹配，可省略 `.exe`，不接受路径；标题按包含关系匹配。两者均忽略大小写，组合使用时必须同时满足。选择发生在 `--delay` 结束后，只匹配可见、未最小化的候选窗口。没有匹配返回退出码 3 / `target_not_found`；多个匹配返回退出码 3 / `ambiguous_target`，不会任意选择。可通过列表中的句柄进一步指定目标。

`monitors` 每项包含 `index`、`deviceName`、`primary`、`bounds`、`dpiX`、`dpiY`、`dpiKnown`、`scaleX`、`scaleY`。分辨率由物理像素边界的 `width`、`height` 表示；缩放 1.0 表示 100%。DPI 不可查询时回退为 96，`dpiKnown=false`。编号从 1 开始，主屏排第一，其余按设备名排序。截图时重新枚举，插拔或调整主屏后应重新查询编号；编号不是永久设备标识。

列表 JSON 统一使用 `{"success":true,"exitCode":0,"schemaVersion":1,"windows":[...]}` 或 `monitors` 数组。无 `--json` 时输出制表符分隔的人类可读列表。列表命令只接受 `--json` 或单独的 `--help`。

## 环境诊断

```powershell
.\nimcap.exe doctor --json
.\nimcap.exe doctor --output-dir C:\captures --backend auto --timeout 10 --json
```

`doctor` 检查输入桌面是否可以打开、现有输出目录是否可写，以及捕获后端能否实际捕获并编码主屏左上角的 1×1 像素。像素仅在内存中处理，不保存截图；目录写入检查使用独有临时文件，并在关闭时删除。它不会创建缺失的目录，默认检查当前工作目录。

`--backend` 接受 `all`（默认）、`gdi`、`wgc`、`auto`。`all` 分别检查强制 GDI、强制 WGC 和自动回退，每项独立报告。`--timeout` 为每个捕获探针的期限，整数 1–3600 秒，默认 10 秒；`all` 最多使用三次探针期限，另加目录/桌面检查及进程清理时间。Ctrl+C 返回 7，并终止当前捕获子进程。

JSON 结果包含 `checks` 数组，每项包含 `name`、`success`、`message`，失败时另含 `code`。检查名为 `desktop`、`outputDirectory`、`capture:gdi`、`capture:wgc`、`capture:auto`。所有检查成功才返回 0；任何一项失败返回 4 / `diagnostic_failed`。因此某个强制后端不可用时，默认 `all` 会失败，即使 `auto` 能成功；需要验证脚本实际使用的后端时指定 `--backend auto` 等参数。参数错误仍返回 2。无 `--json` 时逐项输出 OK/FAIL。

桌面打开成功仅说明具有打开权限，后端探针用于进一步确认像素访问；小区域探针成功不能保证受保护内容、特定窗口、所有显示器和驱动组合均可捕获。

## 参数与行为

| 参数 | 行为 |
|---|---|
| `--full` | 捕获全部显示器组成的虚拟桌面 |
| `--rect x,y,width,height` | 桌面物理像素区域，整个矩形必须位于虚拟桌面边界内；不自动裁剪 |
| `--window` | 捕获执行截图时的前台窗口 |
| `--window-handle handle` | 指定可见、未最小化窗口；失效、隐藏或最小化返回目标错误 |
| `--monitor index` | 捕获 `monitors` 中的显示器，编号从 1 开始 |
| `--process name` | 按进程名匹配唯一窗口，可结合标题筛选 |
| `--window-title text` | 按标题包含关系匹配唯一窗口，可独立使用或结合进程名 |
| `--output path` | 输出路径，必填 |
| `--overwrite` | 允许替换已有文件，默认同名报错 |
| `--delay seconds` | 捕获前等待，整数 0–3600，默认 0 |
| `--timeout seconds` | 捕获超时，整数 1–3600，默认 30 |
| `--backend auto/wgc/gdi` | 默认 `auto`，首选 WGC、失败回退 GDI；指定 `wgc` 或 `gdi` 时直接使用对应后端 |
| `--cursor` | 包含鼠标指针，默认关闭 |
| `--json` | stdout 输出一个 JSON 对象，stderr 输出错误诊断 |

矩形宽高各为 1–16384，总面积最多 6400 万像素。全屏和窗口输出也受编码器的相同尺寸限制。混合 DPI 环境下仍使用物理像素，不使用 WPF DIP。显示器之间的布局空隙属于虚拟桌面矩形的一部分。

超时涵盖延时、子进程启动、捕获与编码。超时或 Ctrl+C 取消会终止捕获子进程。成功取得图像后，最后的文件写入使用同目录临时文件和原子替换；这一提交阶段完成后返回成功，不在文件提交中途响应取消。最终退出前目标文件已经写完；默认禁止覆盖的规则也适用于并发调用。

CLI 直接调用捕获与编码类库，不加载桌面配置、写截图历史、自动识别二维码、修改剪贴板或启动托盘。CLI 与桌面程序可以独立运行；CLI 截图不会主动隐藏已有的设置窗口或贴图，它们可能出现在截图中。

需要可访问的交互式 Windows 桌面。任务计划程序应选择用户已登录的会话；锁屏、断开的远程桌面、Session 0、受保护内容和权限边界可能导致失败、空白内容或后端限制。窗口被遮挡时，WGC 和 GDI 结果可能不同：GDI 获取屏幕上实际显示的像素。

## 结果与退出码

成功结果示例：

```json
{"success":true,"exitCode":0,"output":"C:\\captures\\screen.png","bounds":{"x":0,"y":0,"width":1920,"height":1080},"bytes":123456,"schemaVersion":1}
```

失败结果示例：

```json
{"success":false,"exitCode":3,"error":{"code":"target_not_found","message":"The target window is missing, hidden or minimized."},"schemaVersion":1}
```

`bounds` 使用桌面物理像素坐标；`bytes` 是输出文件大小。错误信息面向人阅读，可能包含系统语言；自动化应判断 `exitCode` 或 `error.code`，不要依赖消息文本。未指定 `--json` 时，成功仅输出绝对文件路径，失败通过 stderr 输出诊断。

| 退出码 | error.code | 含义 |
|---|---|---|
| 0 | — | 已成功保存 |
| 1 | `internal_error` | 未预期的内部错误 |
| 2 | `invalid_arguments` | 命令、参数、矩形或输出格式错误 |
| 3 | `target_not_found` | 窗口不可用或区域在桌面边界外 |
| 3 | `ambiguous_target` | 进程/标题选择器匹配多个窗口 |
| 4 | `capture_failed` | 捕获、编码或子进程失败 |
| 4 | `diagnostic_failed` | doctor 至少一项检查失败，详见 checks |
| 5 | `output_failed` | 路径冲突、无写入权限或保存失败 |
| 6 | `timed_out` | 捕获超时，包含延时 |
| 7 | `canceled` | 用户按 Ctrl+C 取消 |

PowerShell 示例：

```powershell
$text = & .\nimcap.exe capture --full --output .\screen.png --json
$captureExit = $LASTEXITCODE
$result = $text | ConvertFrom-Json
if ($captureExit -ne 0) { throw $result.error.message }
Write-Host "Saved $($result.output), $($result.bytes) bytes"
```

## 开发与验证

```powershell
dotnet build Cli/NimCap.Screenshot.Cli.csproj -c Release
dotnet run --project tools/CliChecks/CliChecks.csproj -c Release -- --cli Cli/bin/Release/net10.0-windows10.0.19041.0/nimcap.exe

# 在允许桌面捕获的交互式会话中验证实际像素、六种编码、相对路径与并发保存
dotnet run --project tools/CliChecks/CliChecks.csproj -c Release -- --cli Cli/bin/Release/net10.0-windows10.0.19041.0/nimcap.exe --hardware
```

普通检查覆盖参数冲突、缺失值、坐标溢出、JSON 错误、窗口列表契约、输出冲突、诊断目录失败、延时超时、失效句柄、挂起子进程取消和截断数据。诊断检查会尝试一个有期限的微小捕获探针；硬件检查用临时纯色窗口核对实际截图像素、标题/进程筛选、歧义和最小化处理、显示器枚举与捕获边界一致性、诊断成功及临时文件清理；产物保存在 `.artifacts/cli-checks`。

桌面发布脚本会一起发布 CLI、核对版本，并对发布目录中的程序运行普通检查。CLI 和桌面主程序共享的 .NET runtime 固定版本需要同步维护；`NimCapCliVersion` 用于发布时与桌面产品版本对齐。

## English quick reference

Run `nimcap.exe capture` with exactly one target (`--full`, `--rect x,y,width,height`, `--window`, or `--window-handle decimal/0xHEX`) and an explicit `--output path`. Paths are relative to the caller's working directory. Coordinates are physical virtual-desktop pixels, including negative origins. PNG, JPEG, WebP, TIFF, BMP and PDF are supported.

Use `windows --json` and `monitors --json` to discover targets. `capture --monitor 1` captures the primary display; indices are 1-based, primary first, then device name, and may change with topology. Window selectors accept `--process name[.exe]` (exact) and/or `--window-title text` (substring), both case-insensitive; combined filters intersect. Selection happens after any delay. No match returns 3 / `target_not_found`; multiple matches return 3 / `ambiguous_target`.

`doctor --json` checks desktop access, writes and removes a unique temporary file in the existing output directory (current directory by default), and performs real in-memory 1x1 GDI, WGC and auto capture/encoding probes. Use `--output-dir path`, `--backend all|gdi|wgc|auto` and `--timeout seconds` (per probe, default 10). JSON `checks` reports each result. Any failure returns 4 / `diagnostic_failed`; a failing forced backend causes `all` to fail even if auto succeeds. Ctrl+C returns 7. A successful tiny probe does not establish capture support for every window or display.

Use `--json` for one result on stdout and error diagnostics on stderr; inspect the exit code or `error.code`. Existing files require `--overwrite`. `--delay` defaults to 0 seconds and `--timeout` to 30 seconds. The deadline includes delay, process startup, capture and encoding; the final atomic file commit finishes before success is returned. Ctrl+C cancels capture and terminates its worker. `--backend` accepts `auto`, `wgc`, or `gdi`; `--cursor` includes the pointer.

Exit codes: 0 saved, 1 internal error, 2 invalid arguments, 3 unavailable target, 4 capture/encoding/worker failure, 5 output failure, 6 timeout, 7 cancellation. Capture requires an accessible interactive Windows desktop. The CLI runs independently of the tray app and does not hide existing NimCap windows or modify clipboard, settings or history.

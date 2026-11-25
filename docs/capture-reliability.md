# 截图可靠性修正

## 行为

- 普通截图和长截图选区使用 `CaptureCoordinator.BeginAsync`。捕获、桌面合成和窗口枚举在后台执行；选区窗口创建和完成事件回到 UI 线程。
- 隐藏窗口之前占用截图状态。捕获期间重复请求被忽略；Esc 或退出取消等待，迟到结果不会再打开选区。
- WGC 设备由租约管理。创建失败不缓存异常；失效的设备代次停止接受新租约，已有会话全部退出后释放。帧回调关闭时先停止接收、退订并等待在途回调，最后在锁外释放原生对象。
- 普通截图 WGC 失败后回退 GDI，分别冷却 5、15、60 秒。到期后的下一次真实截图仅允许一个 WGC 试探；成功重置退避。取消、参数错误和已关闭的目标窗口不作为后端故障处理。主动关闭“优先 WGC”时不做试探。
- 编辑器 Undo/Redo 各最多保留 100 步，淘汰最旧快照；新编辑清空 Redo。此限制不等同于图像内存预算。
- 长截图可选择自动、WGC、DXGI、GDI，并保存偏好。自动模式依次尝试 WGC、DXGI、GDI；明确选择 WGC 或 DXGI 时失败回退 GDI。每次长截图复用帧池或 duplication，仅打开与选区相交的显示器；实际后端出现在捕获进度和预览中。所有后端失败时保留异常链和已捕获帧。
- 当前未提供编辑会话自动落盘和重启恢复。

## 自动检查

在仓库根目录执行：

```powershell
dotnet run --project tools/WindowChecks/WindowChecks.csproj -- --capture-reliability-only
dotnet run --project tools/WindowChecks/WindowChecks.csproj -- --dpi-only
dotnet run --project tools/ScrollingChecks/ScrollingChecks.csproj
```

可靠性检查使用可控后端和时钟验证 UI 响应、线程归属、重复请求、取消传递、迟到结果、退避恢复、历史淘汰、会话复用及回调关闭。长截图回归包含合成样本和已有浏览器样本，不代表实际滚轮验收。

真实后端检查需要有可用捕获服务和桌面访问权限的 Windows 会话：

```powershell
dotnet run --project tools/WindowChecks/WindowChecks.csproj -- --capture-hardware-only
# 也可单独检查某个后端：
dotnet run --project tools/WindowChecks/WindowChecks.csproj -- --capture-hardware-only --backend DXGI
```

该检查显示一个纯色测试窗口，验证 WGC、DXGI、GDI 会话能够观察颜色变化，并检查 WGC 取消关闭。图像只在内存中处理，不保存桌面截图。任一请求后端回退都会判定该后端验证失败。

## 历史验证记录与当前边界

2026-11-25 文档整理时保留以下旧环境记录；原记录未注明测试日期和提交版本，不能视为当前桌面的状态：WGC 捕获项创建返回 `0x80070424`（服务未安装），DXGI duplication 创建返回 `0x80070005`（拒绝访问），GDI 返回 Win32 错误 5（拒绝访问）。该次运行未完成真实后端验收，记录仅用于说明桌面访问受限时的失败表现。

CLI 的 1×1 捕获探针、单次截图与上述持续会话硬件检查覆盖不同范围，不能互相替代。混合 DPI 实机、显示器热插拔、驱动重置及持续资源占用仍需在可捕获的桌面会话验证；每次记录测试日期、提交版本、桌面环境、后端及报告路径。统一验收入口见[构建与测试](构建与测试.md)。

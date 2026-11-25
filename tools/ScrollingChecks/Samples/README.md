# 竖向样本回归

从仓库根目录运行 `dotnet run --project tools/ScrollingChecks -c Release`。默认覆盖位移、往返、无重复行、固定栏、待修正帧、工程往返以及实际 WPF 接缝修改／撤销／重做。

`Edge` 是 Edge 144.0.3719.82 在 2026-11-18 渲染的本地 HTML 样本。三张视口通过 CSS 将文章分别平移 0、150、300px，固定导航保持不动，匹配和拼接排除顶部 40px。它验证真实浏览器字体／页面像素，**不是桌面滚轮或真实应用动态滚动验收**。重建：

```powershell
dotnet run --project tools/ScrollingChecks -c Release -- --browser-sample 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
```

## 真实桌面竖向滚动检查

2026-11-25 已在 Windows 10.0.26300、96 DPI 上运行 Edge 149.0.4022.69 和 Chrome 158.0.8127.37 的真实桌面检查。测试打开隔离配置的本地公开夹具页，通过原生滚轮和生产 `ManualScrollInput`／`ScrollCaptureWindow` 采集，手动路径实际选择 WGC，自动路径指定 GDI。

```powershell
dotnet run --project tools/ScrollingChecks -c Release -- --desktop-browser 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
dotnet run --project tools/ScrollingChecks -c Release -- --desktop-browser 'C:\Program Files\Google\Chrome\Application\chrome.exe'
```

该模式会打开测试窗口、移动鼠标并发送滚轮和 Esc，请在空闲的可交互桌面运行；不要与其他窗口回归并行执行。只操作带本次唯一标识的本地测试窗口，输入前验证窗口归属，不使用用户浏览器配置。测试需要浏览器缩放为 100% 或 200%，其他 DPI 应通过独立审核样本验收，不能套用此像素标尺的预期值。

覆盖内容：

- 手动下滚、向上回访和重新向下；同一文段只合成一次。
- 手动重新采集先向上扩展、再向下，成功后替换旧会话；取消重新采集保留原会话。
- 自动竖向采集至页尾，保持区域宽度，停止时所有接缝均有可靠匹配。
- 从原始帧左侧颜色解出绝对文档行坐标，独立计算预期位移及完整行并集，再逐像素比对输出；不使用 `Match()` 计算预期。
- 保存原帧、工程、输出、独立预期和环境报告；工程重开后像素一致。

`EdgeDesktop` 保留实际向上扩展／回访的四帧样本，`ChromeDesktop` 保留实际向下／回访的五帧样本。每组的 `provenance.md` 记录版本、来源和真实位移，`sample.json` 与预期 PNG 可在无桌面输入时重放。默认回归自动重放全部样本目录。

测试页包含固定导航，实测选择区域位于导航下方；这不代表固定栏自动检测、动态布局、虚拟列表、Electron、Office、混合 DPI 或全部驱动兼容性已通过。完整产物位于 `.artifacts/scrolling-checks/<GUID>/desktop-browser`；`report.json` 记录本次环境与各场景结果，`samples/<场景>` 可直接用 `--samples` 重放。

## 扩充应用样本

生成场景 `browser-article`、`electron-chat`、`office-pages` 同样不是对应应用实测。真实兼容样本应从实际竖向长截图保存 `.ssscroll`，人工比对原始帧并修正接缝后导出：

```powershell
dotnet run --project tools/ScrollingChecks -c Release -- --export-sample 'capture.ssscroll' 'Word' '具体版本' '分页文档，150% DPI，慢速竖向滚动'
dotnet run --project tools/ScrollingChecks -c Release -- --samples '导出的样本目录'
```

产物位于 `.artifacts/scrolling-checks/<GUID>`。导出保留原帧、实际参数和原自动匹配可靠标记；人工修正不会把自动匹配失败改写成成功。提交前独立核对 `ExpectedOffsets`，不要以算法自己的输出充当真值。可设置 `ExpectedImage` 为人工确认的完整输出 PNG，重放逐像素比较（容许单通道一级舍入）。不要提交私人聊天、业务文档或凭据。

JSON 必填字段：`Application`、`Version`、`Scenario`、`Frames`（按顺序的 PNG 相对路径）、`ExpectedOffsets`（人工标定的相邻竖向有符号位移）、`ExpectedReliable`（期望自动算法是否可靠）、`Trim`（每帧固定栏裁边），以及可空的 `ExpectedImage`。重放检查所有相邻匹配；实际采集链路在首个不确定帧结束，保留该帧和零位移待修正状态，并验证工程往返。

实际应用覆盖与剩余矩阵：

| 应用 | 场景 | 核对 |
|---|---|---|
| Edge / Chrome | 本地正文、固定导航区域外框选、向上回访、重新采集、自动页尾 | 上述版本／96 DPI 已通过；延迟加载、动画和任意真实网站未覆盖 |
| Firefox | 正文、固定导航、延迟加载图片、动画 | 未实测；文段不缺失、不重复，布局变化时拒绝不确定接缝并保留原帧 |
| VS Code / Teams 等 Electron | 代码行、聊天虚拟列表、向上回滚 | 行号／消息顺序、动态重绘、停止后可修正 |
| Word / Excel | 分页文档、冻结表头、不同缩放 | 页边界、行号、重复表头裁除、拼接像素和高度 |

每项记录应用版本、DPI、内容来源、滚动步长、固定栏裁边、自动与手动结果，并保留工程和预期 PNG。横向不在矩阵内。只有完成桌面实际滚轮采集并审核原帧才能记为真实应用通过。

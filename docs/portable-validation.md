# Windows 便携交付验收（0.7.0）

本轮在原生窗口捕获重试仍超时后，按用户安排把原生交互放到最后，转做阶段 F 的便携交付。没有操作刻录设备或要求实盘材料。

## 包与构建

`tools/Publish-Portable.ps1` 用 SDK 10.0.203 发布 Release / win-x64 / self-contained，固定 .NET 10.0.7，不裁剪、不合并为单文件。WPF 与 CLI 共用包内运行时；针对 `WindowsBase.dll` 的 Core facade / WPF 实现差异，只允许与两个官方运行时包字节哈希分别匹配的已知冲突，保留 WPF 实现，其余不同文件拒绝合并。

普通 solution 构建仍使用清空源的 `NuGet.Config`。打包首次可用 `-RuntimeSource https://api.nuget.org/v3/index.json` 下载平台运行时包到 `.local/nuget`，不改全局包源。后续直接调用即可离线复用。

包包含桌面/CLI、运行时许可证、使用说明、CHANGELOG、SHA256 清单与 `source.zip`。对应源码取当前工作区，包含未提交变更，排除 `.git`、缓存、编译产物和验收输出；不会用旧 HEAD 的源码代替当前二进制。`source-files.json` 支持源码归档在没有 Git 元数据时继续枚举构建输入。源码修改后应重新打包，包并不保证跨构建 ZIP 字节相同。

```powershell
./tools/Publish-Portable.ps1 -Out artifacts/portable-new
./tools/Test-Portable.ps1 -Archive artifacts/portable-new/DvdCalibrator-0.7.0-win-x64.zip -Out artifacts/portable-recheck
```

输出目录均须尚不存在。中间发布目录保留在忽略的 `.local/portable-*`，失败不会覆盖旧交付包。

## 已执行的软件检查

| 检查 | 结果与依据 |
| --- | --- |
| 编译及旧行为 | Release 0 警告/错误；54/54 Core、23/23 Windows 图像/任务/进程内状态检查、CLI 全流程通过 |
| ZIP 与来源 | 检查逐文件长度/SHA256、缺失/多余文件，包含完整对应源码与两种运行时许可证 |
| 迁移路径 | 直接从 ZIP 解压到 `中文 路径/解压目录`，所有进程用独立工作目录 |
| 包内运行时 | 子进程的 `DOTNET_ROOT` / `DOTNET_ROOT_X64` 指向空目录，PATH 仅保留 Windows 系统路径；实际报告 CoreCLR、PresentationFramework 都从包目录加载 |
| 桌面软件维护入口 | 自检进程不显示窗口：合成描线拟合/确认、`.dvdphoto` 与 `.dvdcal` 保存回读、双盘一致性、最终配置及 WPF PNG 回读通过 |
| CLI | 合成生成/inspect/predict、描线拟合、拒绝未确认导出、显式确认后配置/工程导出通过 |
| 保护行为 | 桌面自检、CLI 对已有输出拒绝覆盖；非法维护参数返回非零；检查后包文件哈希不变 |
| 结果路径 | 最终工程在自检目录正式发布后单独导出，输出路径不指向已迁移的暂存目录 |

`portable-verification.json` 记录包 SHA256 与检查状态；`installation-check.json` 记录实际运行时路径、进程架构和软件检查状态。WPF PNG 已实际打开查看，它是离屏视图。

新增的三个进程内桌面状态检查覆盖：已确认候选重算取消保留、矫正取消不部分提交、遮罩草稿切换保护及提交后旧候选失效。它们不发送 OS 鼠标或键盘输入。

本轮未改变 Core 几何、拟合算法、阈值及快照内容契约，因此未重复 0.6.0 的完整数值矩阵；矩阵既有结果见 [模拟矩阵](simulation-matrix.md)。

## 仍待验收

- 没有安装 .NET 的全新 Windows x64 环境。隔离搜索路径并核对实际加载位置提供本机证据，不能代替另一套 Windows 环境验收。
- 原生拖点、滚轮、右键平移、文件对话框、关闭提示及交互中断恢复，统一放在最后。本轮捕获失败为 `FrameArrived timed out: timed out waiting on channel`，重新选窗并重试后为 `window capture timed out: timed out waiting on channel`；未继续输入。
- 实际介质、刻录及真实拍照盲测。所有演示和安装检查均为软件数据，`nativeInteraction` / `cleanMachine` / `physicalValidation` 分别保留 pending。

当前包仅提供 win-x64；不包含 DiscImageStudio 生成引擎，不自动产生或刻录真实 ISO。

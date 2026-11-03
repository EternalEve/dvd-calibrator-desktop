# DVD Calibrator Desktop

12 cm 单层 DVD 标定工具。当前版本 **0.1.0，阶段 A：数据与正向模型基础**。
本阶段提供本地命令行，还没有桌面界面、照片矫正或粗调/精调反推。

目标是在人工确认下，以一张粗调盘、一张精调盘为正常路径，获得供
DiscImageStudio 使用的等效补偿参数。两张盘完成率需要后续实盘验证。

## 当前能力

- 固定 DiscImageStudio 提交 `374c0630326ea61be9adca8d7de3c96f083c00df`，直接复用其完整 DVD 几何源文件。
- 计算三次轨距、总轨长、半径与数据进度互换、累计角度和完整正向相位映射。
- 显式处理 CW/CCW、mm/nm/rad、观察面和零起始 LBA；拒绝越界、非正轨距及不完整参数。
- 冻结合成生成快照，计算内容哈希；定义观测与拟合结果契约，防止沿用过期输入。
- 解析指定范围的上游真实生成旁车，保留原始记录，列出缺失证据。
- 补齐显式声明后冻结外部生成快照，附带源图与旁车字节副本，可换目录回读验证。
- 导出合成预测 JSON、CSV、SVG 和 PGM 栅格示例。

## 构建和检查

需要 .NET SDK **10.0.203**（同一补丁系列可滚动）。Windows 可直接运行：

```powershell
./tools/Test.ps1
```

检查包含编译、独立数值对照、数据与失败路径检查，以及命令行端到端检查。
项目没有外部 NuGet 包；`NuGet.Config` 清空源，正常构建和运行无需联网。
SDK 自带的运行时和参考包须已安装。`.local/`、`bin/`、`obj/`、`artifacts/` 不进入版本控制。

也可以手动运行：

```powershell
dotnet build DvdCalibrator.slnx -c Release
dotnet run --project tests/DvdCalibrator.Tests -c Release --no-build
```

## 运行合成示例

所有输出目录必须尚不存在。重复运行时请使用新目录名。

```powershell
dotnet run --project src/DvdCalibrator.Cli -c Release -- demo --out artifacts/demo-001
dotnet run --project src/DvdCalibrator.Cli -c Release -- inspect --record artifacts/demo-001/generation.json
dotnet run --project src/DvdCalibrator.Cli -c Release -- predict --generated artifacts/demo-001/generation.json --actual artifacts/demo-001/actual.json --out artifacts/predict-001
dotnet run --project src/DvdCalibrator.Cli -c Release -- import-discimage --record fixtures/upstream-fast-fill.iso.json --out artifacts/import-001
```

真实任务的快照冻结方式见 [数据契约](docs/data-contracts.md)。`import-discimage` 仅解析，
不会凭缺失条件自动冻结；`freeze-discimage` 要求显式提供源图、介质、容量和生成器身份。

`generation.json` 是不可变合成快照，`actual.json` 是模拟的实际模型。
SVG 显示半盘边界或十字中心线；PGM（P5）是标准灰度图，`source.pgm` 与 `predicted.pgm`
展示解析图案的变形。PGM 可用支持 Netpbm 的图像工具查看；SVG 可在浏览器打开。
栅格是点采样，可能混叠；SVG 检出相邻角差过大时拒绝连接曲线并提示增加采样。
这些输出均不是光学亮度模拟、ISO、拟合结果或实刻通过证明。

退出码：`0` 成功，`2` 输入不合法，`3` 文件读写失败，`130` 取消。Ctrl+C 可取消计算；
输出先在同级临时目录构建，再一次性发布，失败不会覆盖旧任务。

## 结构与后续开发

| 路径 | 内容 |
| --- | --- |
| `src/DvdCalibrator.Core` | 几何适配、数据契约、哈希、正向预测、记录解析 |
| `src/DvdCalibrator.Cli` | 命令行入口 |
| `third_party/DiscImageStudio` | 固定版本几何与许可证 |
| `schemas` | JSON Schema 结构定义，语义验证由 Core 执行 |
| `tests/DvdCalibrator.Tests` | 无外部框架的检查程序和独立 Simpson 数值参照 |
| `fixtures` | 真实上游输出的最小软件测试样例 |
| `docs` | 架构决策、坐标约定、数据与阶段验收 |

下一阶段实现原图保存、EXIF 定向、实体外缘和孔缘标注、联合矫正与遮罩。
随后完成人工描线闭环，最后加入密集环、多候选诊断与两张盘工作流。
Windows 桌面采用 WPF；核心与 CLI 保持独立，核心项目目标为 `net10.0`。

详见 [架构与坐标](docs/architecture.md)、[数据契约](docs/data-contracts.md)、
[阶段 A 验收](docs/phase-a-validation.md) 与 [CHANGELOG](CHANGELOG.md)。
原始制作指南保留为完整设计参考；其中列出的最终 CLI 和 schema 草稿不等于当前实现。

采用 GPL-3.0-or-later，见 [LICENSE](LICENSE)、[第三方声明](THIRD-PARTY-NOTICES.md)。

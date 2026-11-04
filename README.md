# DVD Calibrator Desktop

12 cm 单层 DVD 标定工具。当前版本 **0.8.4：直接显示粗调参数，简化第二张准备流程**。
已有照片标注、密环/描线分析、第二张 G1 计划、独立精调、两盘恢复与最终配置追溯，提供自带运行时的 Windows x64 桌面/CLI 包；实盘验收尚未完成。

目标是在人工确认下，以一张粗调盘、一张精调盘为正常路径，获得供
DiscImageStudio 使用的等效补偿参数。两张盘完成率需要后续实盘验证。

## 当前能力

- [8 页分步向导](docs/guided-workflow.md)，直接显示在哪里操作、按钮顺序、产物与继续条件；[从零准备第一张](docs/material-preparation.md) 补齐生成、刻录、拍照与记录整理。
- 随程序提供可离线打开的“新手教程.html”和原始十字标定 PNG，每页可跳到对应教程，区分原项目记录与本工具记录。
- 默认[深色界面](docs/dark-theme.md)，统一工具栏、输入控件、照片工作区、预览标签及按钮状态；保留原始照片与标注颜色。
- Windows x64 便携包，自带 .NET / WPF 10.0.7；含完整对应源码、许可证、逐文件哈希和独立软件自检入口。
- 重算取消保留原结果，矫正与预览完成后一起提交；未结束遮罩保持草稿并阻止遗漏遮罩的计算和导出。
- 导入原图并应用全部 8 种 EXIF 定向，保留原始字节和原分辨率定向 PNG。
- 桌面外缘/孔缘布点与拖点、缩放平移、反光遮罩、主径向线及对侧 180° 检查线。
- 保留累计圈数的完整模型拟合，主线外侧 25% 留出；默认修半径，可按证据增加一至三阶形状。
- 候选曲线叠加、逐点残差、训练/留出诊断、输入变化失效及人工确认。
- 已知解析半盘靶的 RGB 通道评估、多角度相位、局部变频谱、缺失/过零分片与整片圈数分支。
- 全盘相位提取不足时，采用 pyGPA WFR 局部条纹提取及 Math.NET 计算，分别检查独立角度和外侧半径数据。
- 密环可联合局部连续描线，保留全局明暗极性歧义，使用径向及角向留出原图评分。
- 完整候选重建、第二张解析十字预测、候选差异/臂间距/线宽像素诊断及中文报告；信息不足明确拒绝。
- 保留固定上游 `target_slim.png` 原图与逐像素采样规则，核对 DVD 外半径缩放、1199.5 像素中心、方向和灰度映射。
- 第二张全部生成参数直接显示并可复制，自动保存与粗调结果的关联、检查预计分辨率及候选差；使用原软件自带辅助标定图案。
- 第二张实际旁车/原图逐项核对，拒绝 G0、换图、错误设置及模拟记录混入实际任务；使用 G1 独立精调。
- `.dvdcal` 两盘任务包、盘间形状与分辨率检查、最终完整配置/工程及两盘追溯报告。
- CW/CCW、两种位长和非零轨距形状的 54 项双盘模拟矩阵，分别注入像素/描点/轮廓误差，输出完整参数、误差、拒绝原因和可恢复样本。
- 桌面、CLI 和报告共用稳定诊断码与中文操作建议，指出何时补拍、补点、核对 G0/G1 或重新创建计划。
- 导出完整补偿 JSON、CSV/SVG/HTML 报告；兼容时附带经实际加载器回读的 DiscImageStudio 工程。
- 联合双圆透视恢复，直接采样原图生成正视/极坐标预览，显示轮廓 RMS 并人工确认。
- 自带照片和生成附件的 `.dvdphoto` 任务包，支持换目录恢复与哈希检查。
- 固定 DiscImageStudio 提交 `374c0630326ea61be9adca8d7de3c96f083c00df`，直接复用其完整 DVD 几何源文件。
- 计算三次轨距、总轨长、半径与数据进度互换、累计角度和完整正向相位映射。
- 显式处理 CW/CCW、mm/nm/rad、观察面和零起始 LBA；拒绝越界、非正轨距及不完整参数。
- 冻结合成生成快照，计算内容哈希；定义观测与拟合结果契约，防止沿用过期输入。
- 解析指定范围的上游真实生成旁车，保留原始记录，列出缺失证据。
- 补齐显式声明后冻结外部生成快照，附带源图与旁车字节副本，可换目录回读验证。
- 导出合成预测 JSON、CSV、SVG 和 PGM 栅格示例。

## 构建和检查

便携包完整解压后双击 `DvdCalibrator.App.exe`，不需要 SDK。使用方式见 [便携版说明](docs/portable-quickstart.md)。
打包工具需要 PowerShell 7、上述 SDK 与平台运行时包。首次打包显式下载官方运行时包，之后可省略源参数离线复用缓存：

```powershell
./tools/Publish-Portable.ps1 -Out artifacts/portable-001 -RuntimeSource 'https://api.nuget.org/v3/index.json'
```

默认打包后从 ZIP 解压到中文含空格目录，隔离全局 .NET 并检查桌面软件流程、CLI 及包内文件完整性；可用 `-SkipChecks` 仅构建，不能将其记为验收通过。
完整边界与复查方式见 [便携交付验收](docs/portable-validation.md)。

完整 solution 需要 Windows 与 .NET SDK **10.0.203**（同一补丁系列可滚动），包含 WPF 参考包和桌面运行时：

```powershell
./tools/Test.ps1
```

检查包含编译、独立数值对照、照片与任务包检查、离屏 WPF 布局和命令行端到端检查。
默认还运行完整模拟矩阵：28 个接受断言、24 个精确拒绝分类断言，另记录 2 个边界探测。
矩阵是有限软件用例，不给出真实介质成功率。独立运行或快速检查：

```powershell
./tools/Test-SimulationMatrix.ps1
./tools/Test-SimulationMatrix.ps1 -Quick
./tools/Test.ps1 -SkipSimulationMatrix
```

矩阵在新建 `artifacts/simulation-*` 目录输出 HTML/CSV/JSON 及样本任务，见 [模拟矩阵说明与结果](docs/simulation-matrix.md)。
Math.NET Numerics 5.0.0 的固定 NuGet 包随源码提供；`NuGet.Config` 只使用本地 `third_party/MathNet` 源，正常构建和运行无需联网。
SDK 自带的运行时和参考包须已安装。`.local/`、`bin/`、`obj/`、`artifacts/` 不进入版本控制。

也可以手动运行：

```powershell
dotnet build DvdCalibrator.slnx -c Release
dotnet run --project tests/DvdCalibrator.Tests -c Release --no-build
```

## 运行合成示例

首次启动进入“准备第一张标定盘”。按第 1 页说明准备黑白盘。右上角“新手教程”可打开当前步骤的完整离线说明。
原始生成旁车的冻结方式见 [生成记录准备说明](docs/material-preparation.md)。

启动带已知生成模型与主/检查线的拟合演示（去掉参数即为空白任务入口）：

```powershell
dotnet run --project src/DvdCalibrator.App -c Release -- --trace-demo
dotnet run --project src/DvdCalibrator.App -c Release -- --dense-demo
dotnet run --project src/DvdCalibrator.App -c Release -- --two-disc-demo
```

真实任务先按[分步教程](docs/guided-workflow.md)用黑白半盘图和默认 DVD 参数生成并刻录 ISO。第 1 页提交 ISO 同目录、同名的 `.iso.json` 和记录面照片。
沿实体外缘与真实孔缘各标 8–16 点，计算、核对并确认矫正。任务包使用新文件名保存修订。
矫正确认后沿同一条源径向边界描点，点击“拟合描线”，核对完整预测线与留出区域，再确认导出。
密环演示点击“分析密环 / 混合”，紫线为完整模型，蓝线为有效相位片段，“导出密环诊断”查看所有候选与下一张预测。
密环入口只接受明确的解析 `half-disc` 快照，不猜测外部源图片靶型；当前只修正两个半径并保留生成轨距形状。
操作细节见 [照片工作流](docs/photo-workflow.md)、[描线拟合](docs/trace-fitting.md) 和 [密环与混合观测](docs/dense-analysis.md)。演示是数学生成图，不是实盘验证。

两盘演示直接进入第 7 步的第二张合成照片。点“拟合描线”，核对并确认候选曲线，再到第 8 步查看可复制的补偿参数。
真实流程在第 4 页直接查看或复制粗调参数，填入 DiscImageStudio，再从当前参数创建自带的辅助标靶，无需导出文件或手动摆放图案。生成并刻录后，第 5 页直接提交 ISO 同目录的 `.calibration.json` 和第二张照片。
见 [两盘工作流](docs/two-disc-workflow.md) 和 [上游标靶契约](docs/upstream-target-contract.md)。生成计划不会自动写 ISO，也不把软件模拟标记为真实刻录。

所有输出目录必须尚不存在。重复运行时请使用新目录名。

维护指导文案时，修改 `docs/guided-workflow.md` 与 `docs/material-preparation.md` 后运行 `./tools/Render-BeginnerGuide.ps1`，同步生成随程序提供的离线 HTML；正常构建和使用无需生成工具。

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
| `src/DvdCalibrator.Imaging.Windows` | WPF 编解码、EXIF、照片任务包 |
| `src/DvdCalibrator.App` | 桌面照片标注与矫正工作区 |
| `third_party/DiscImageStudio` | 固定版本几何与许可证 |
| `schemas` | JSON Schema 结构定义，语义验证由 Core 执行 |
| `tests/DvdCalibrator.Tests` | 无外部框架的检查程序和独立 Simpson 数值参照 |
| `fixtures` | 真实上游输出的最小软件测试样例 |
| `docs` | 架构决策、坐标约定、数据与阶段验收 |

后续补充全新 Windows 环境验收、更多误差模型与真实盘盲测；原生交互验收按安排放在最后。
第二张参数关联与拍摄要求由软件内部检查并保存，实刻状态一直保持 `pending`；两盘完成率和质量阈值仍需真实样本验证。
Core/CLI 继续目标为 `net10.0`；图像与桌面项目为 `net10.0-windows`。

详见 [架构与坐标](docs/architecture.md)、[数据契约](docs/data-contracts.md)、
[阶段 A 验收](docs/phase-a-validation.md)、[阶段 B 验收](docs/phase-b-validation.md)、[阶段 C 验收](docs/phase-c-validation.md)、[阶段 D 验收](docs/phase-d-validation.md)、[阶段 E 软件验收](docs/phase-e-software-validation.md) 与 [CHANGELOG](CHANGELOG.md)。
原始制作指南保留为完整设计参考；其中列出的最终 CLI 和 schema 草稿不等于当前实现。

采用 GPL-3.0-or-later，见 [LICENSE](LICENSE)、[第三方声明](THIRD-PARTY-NOTICES.md)。

# DVD Calibrator Desktop

12 cm 单层 DVD 标定工具。当前版本 **0.5.0，阶段 E 的软件部分：固定标靶契约与两盘任务闭环**。
已有照片标注、密环/描线分析、第二张 G1 计划、独立精调、两盘恢复与最终配置追溯；实盘验收尚未完成。

目标是在人工确认下，以一张粗调盘、一张精调盘为正常路径，获得供
DiscImageStudio 使用的等效补偿参数。两张盘完成率需要后续实盘验证。

## 当前能力

- 导入原图并应用全部 8 种 EXIF 定向，保留原始字节和原分辨率定向 PNG。
- 桌面外缘/孔缘布点与拖点、缩放平移、反光遮罩、主径向线及对侧 180° 检查线。
- 保留累计圈数的完整模型拟合，主线外侧 25% 留出；默认修半径，可按证据增加一至三阶形状。
- 候选曲线叠加、逐点残差、训练/留出诊断、输入变化失效及人工确认。
- 已知解析半盘靶的 RGB 通道评估、多角度相位、局部变频谱、缺失/过零分片与整片圈数分支。
- 密环可联合局部连续描线，保留全局明暗极性歧义，使用径向及角向留出原图评分。
- 完整候选重建、第二张解析十字预测、候选差异/臂间距/线宽像素诊断及中文报告；信息不足明确拒绝。
- 保留固定上游 `target_slim.png` 原图与逐像素采样规则，核对 DVD 外半径缩放、1199.5 像素中心、方向和灰度映射。
- 冻结第二张全部 G1 参数、独立计划确认、预计分辨率/候选差诊断；导出原图、生成参数数组和预测报告。
- 第二张实际旁车/原图逐项核对，拒绝 G0、换图、错误设置及模拟记录混入实际任务；使用 G1 独立精调。
- `.dvdcal` 两盘任务包、盘间形状与分辨率检查、最终完整配置/工程及两盘追溯报告。
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

完整 solution 需要 Windows 与 .NET SDK **10.0.203**（同一补丁系列可滚动），包含 WPF 参考包和桌面运行时：

```powershell
./tools/Test.ps1
```

检查包含编译、独立数值对照、照片与任务包检查、离屏 WPF 布局和命令行端到端检查。
项目没有外部 NuGet 包；`NuGet.Config` 清空源，正常构建和运行无需联网。
SDK 自带的运行时和参考包须已安装。`.local/`、`bin/`、`obj/`、`artifacts/` 不进入版本控制。

也可以手动运行：

```powershell
dotnet build DvdCalibrator.slnx -c Release
dotnet run --project tests/DvdCalibrator.Tests -c Release --no-build
```

## 运行合成示例

启动带已知生成模型与主/检查线的拟合演示（去掉参数即为空白任务入口）：

```powershell
dotnet run --project src/DvdCalibrator.App -c Release -- --trace-demo
dotnet run --project src/DvdCalibrator.App -c Release -- --dense-demo
dotnet run --project src/DvdCalibrator.App -c Release -- --two-disc-demo
```

真实任务先打开第一阶段冻结的 `generation.json`，再导入对应盘片的记录面照片。
沿实体外缘与真实孔缘各标 8–16 点，计算、核对并确认矫正。任务包使用新文件名保存修订。
矫正确认后沿同一条源径向边界描点，点击“拟合描线”，核对完整预测线与留出区域，再确认导出。
密环演示点击“分析密环 / 混合”，紫线为完整模型，蓝线为有效相位片段，“导出密环诊断”查看所有候选与下一张预测。
密环入口只接受明确的解析 `half-disc` 快照，不猜测外部源图片靶型；当前只修正两个半径并保留生成轨距形状。
操作细节见 [照片工作流](docs/photo-workflow.md)、[描线拟合](docs/trace-fitting.md) 和 [密环与混合观测](docs/dense-analysis.md)。演示是数学生成图，不是实盘验证。

两盘演示直接进入已关联 G1 的第二张合成照片。点击“拟合描线”→核对并“确认候选曲线”→“检查两盘一致性”→“导出两盘最终配置”。
真实流程先从第一张创建、核对并确认第二张计划，导出固定原图与完整参数，用目标软件生成，再冻结实际旁车后导入第二张照片。
见 [两盘工作流](docs/two-disc-workflow.md) 和 [上游标靶契约](docs/upstream-target-contract.md)。生成计划不会自动写 ISO，也不把软件模拟标记为真实刻录。

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
| `src/DvdCalibrator.Imaging.Windows` | WPF 编解码、EXIF、照片任务包 |
| `src/DvdCalibrator.App` | 桌面照片标注与矫正工作区 |
| `third_party/DiscImageStudio` | 固定版本几何与许可证 |
| `schemas` | JSON Schema 结构定义，语义验证由 Core 执行 |
| `tests/DvdCalibrator.Tests` | 无外部框架的检查程序和独立 Simpson 数值参照 |
| `fixtures` | 真实上游输出的最小软件测试样例 |
| `docs` | 架构决策、坐标约定、数据与阶段验收 |

后续补充更广泛的双盘/误差模拟、实盘盲测、原生交互验收和 Windows 便携交付。
第二张计划确认是可追溯的软件操作，实刻状态一直保持 `pending`；两盘完成率和质量阈值仍需真实样本验证。
Core/CLI 继续目标为 `net10.0`；图像与桌面项目为 `net10.0-windows`。

详见 [架构与坐标](docs/architecture.md)、[数据契约](docs/data-contracts.md)、
[阶段 A 验收](docs/phase-a-validation.md)、[阶段 B 验收](docs/phase-b-validation.md)、[阶段 C 验收](docs/phase-c-validation.md)、[阶段 D 验收](docs/phase-d-validation.md)、[阶段 E 软件验收](docs/phase-e-software-validation.md) 与 [CHANGELOG](CHANGELOG.md)。
原始制作指南保留为完整设计参考；其中列出的最终 CLI 和 schema 草稿不等于当前实现。

采用 GPL-3.0-or-later，见 [LICENSE](LICENSE)、[第三方声明](THIRD-PARTY-NOTICES.md)。

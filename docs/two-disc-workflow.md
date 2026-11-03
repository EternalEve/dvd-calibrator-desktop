# 两盘软件工作流（0.5.0）

## 先体验完整合成流程

```powershell
dotnet run --project src/DvdCalibrator.App -c Release -- --two-disc-demo
```

演示第一张包含变频半盘和缺失区，先计算粗调候选 P1，再冻结与确认 G1；
第二张用固定上游原图生成独立合成照片，并保留各自生成快照和照片几何。
演示中的计划确认来自已知软件样本，不代替真实任务的人为核对。

1. 当前显示第二张 G1。点击“拟合描线”，确认其完整橙线与对侧留出点。
2. 点击“确认候选曲线”，再“检查两盘一致性”。
3. “导出两盘最终配置”输出最终参数、兼容工程、两张原始任务包及两盘追溯报告。
4. “保存两盘任务”写 `.dvdcal`，可换目录恢复。查看第一/第二张不会把两张的 G、H、照片转角或描线混合。

本例中第一张 G0 的 ri/ro 为 24/58 mm；隐藏实际模型为 24.003/58.006 mm，三项轨距形状为 .02/-.03/.015。
粗调参数冻结为第二张 G1，第二张拟合的真值是实际模型，不能把 G1 直接当最终测量结果。
当前全部结果实刻状态为 `pending`，不宣布两张盘实际成功。

## 真实任务：第一张到第二张计划

第一张先按 [照片工作流](photo-workflow.md) 关联真实冻结生成记录、原图、外缘/孔缘、矫正和遮罩。
可使用已确认的连续描线候选，或有受支持候选且无未解决冲突的半盘密环分析。
当前界面使用已确认的描线选中候选；没有已确认描线时使用首个受支持密环候选。
全部仍受支持的竞争模型均进入下一张预测，不会因为未选中而静默删除。

“预计精调像素/mm”是第二张拍摄计划的有效源分辨率输入，默认 8，不是从未来照片测得的数值。
修改该输入后需重新创建计划；已冻结 G1 不随文本改动。近拍、清晰度、遮挡和单应投影都会影响实际有效分辨率。

点击“创建第二张计划”，核对 G1、候选差、预计臂间距/线宽，以及“第二张完整预测”页签中的四臂中心线。
预测只去掉无关的整体照片转角，保留模型径向形状差；竞争差超过 0.2 mm、预计臂间间隔不足 8 像素、
线宽不足 2 像素或曲线采样不足，均不能确认。它们是待实盘校准的软件筛查值。
核对第一张原图和完整曲线后，“确认第二张计划”记录绑定整个计划的独立确认。
第一张会话、候选或结果改变，使旧计划失效；需重新创建并保留旧任务修订。

## 导出、生成和验证实际 G1

“导出第二张靶”新建目录，包含：

- 原字节 `target_slim.png`，完整 G1/来源/确认的 `fine-target-plan.json`。
- `generator-arguments.json`：传给固定引擎的精确 token 数组。
- `next-disc.svg` 和 `report.html`：基于固定原图名义线宽的全部支持候选预测。

此操作不写 ISO。请按报告用固定上游引擎，以导出目录为工作目录运行。PowerShell 示例：

```powershell
$enginePath = 'X:/your-pinned-engine/DvdImageSolver.dll'
$jobArguments = Get-Content -LiteralPath './generator-arguments.json' -Raw | ConvertFrom-Json
& dotnet $enginePath @jobArguments
```

执行后需要实际 `fine.iso.json` 与同一份原图，按真实介质和容量声明冻结：

```powershell
dotnet run --project src/DvdCalibrator.Cli -c Release -- freeze-discimage --record PATH/fine.iso.json --image PATH/target_slim.png --media DVD+R --confirmed-sectors YOUR_REAL_SECTORS --generator-version 1.3.6 --generator-commit 374c0630326ea61be9adca8d7de3c96f083c00df --out NEW_FROZEN_FOLDER
dotnet run --project src/DvdCalibrator.Cli -c Release -- verify-fine-generation --plan PATH/fine-target-plan.json --generated NEW_FROZEN_FOLDER/generation.json
```

不要套用测试 fixture 的 16 扇区容量；值必须来自该任务。不得换图、重新编码原图、额外旋转或平移。
验证要求 G1 全参数、Disc 声明、原图哈希、固定版本/提交声明、起始角、PSN offset、阈值、tone 和采样设置匹配。
源文件名和旧绝对路径不作为身份依据。详见 [固定标靶契约](upstream-target-contract.md)。

## 第二张独立精调及最终导出

在原两盘任务中点击“导入第二张照片”，依次选择验证后的冻结 `generation.json` 和第二张记录面原图。
实际流程不接受 `reference-target-simulation`；也拒绝第一张快照或原图的重复绑定。
软件无法仅靠不同照片哈希证明它来自不同实体盘，实际盘片关联仍需操作者核对。

重新标注本张双圆、核对并确认矫正，沿明确的右侧十字中心线从内向外描点，必要时补明确对侧 180° 线。
采用本张 G1 独立拟合半径；原有非零形状保留，有证据时再用阶段 C 的形状选项。
第一张不进入第二张优化；不要将两张点拼成一条线。圆环不作为当前角度约束。

确认精调曲线后，“检查两盘一致性”将 P2 放回第一张 G0 的观测径向域，与冻结的粗调支持模型比较。
只允许一项整体转角对齐，用冻结粗调切线计算法向形状差；RMS 超过 .35 mm 或最大超过 .8 mm 报告盘间冲突。
同时根据第二张实际单应投影最保守尺度和 P2 曲线，检查法向线宽至少 2 有效源像素。
这不等于完整不确定度或物理稳定性检验。冲突时独立精调结果保留，共享的两盘最终导出拒绝，不把 P2 强行拉回 P1。

“导出两盘最终配置”沿用完整参数与实际上游工程加载器回读，另保存：

- `fine-target-plan.json`、`two-disc-consistency.json`。
- `coarse.dvdphoto` / `fine.dvdphoto`，携带原图、快照、全部观测和结果。
- `two-disc-report.html`，明确软件模拟/实际记录模式、G0/G1 身份及独立精调来源。

第二张不兼容目标工程可表达字段时，仍按阶段 C 规则仅输出完整配置和应用说明，不偷偷改位长或方向。
`physicalValidation=pending` 始终保留；本版本没有物理刻录、真实盲测或成功率结论。

## 保存与恢复

`.dvdcal` 可保存只有计划、尚未导入第二张的任务，也能保存完成精调的两盘任务。
单盘“保存为新任务包”不代替“保存两盘任务”。所有保存拒绝覆盖，使用新文件名保留修订。
打开两盘包会验证嵌套附件哈希、原图和各自快照，再核对计划来源与确认。
原生鼠标/系统文件对话框自动验收仍为待补项；当前验收包括任务 API 闭环和离屏桌面布局。

0.6.0 对检查失败提供具体操作建议：遮罩内点应在可见同一边界补描，留出失配应核对换臂/漏圈，
分辨率不足需增加实际拍摄像素，旧计划失效需重新创建。提示不改变证据门槛，也不支持删除留出数据来获得确认。
这些路径纳入 [双盘模拟矩阵](simulation-matrix.md)，其中像素、描点和实体轮廓误差分别建模。

# 数据契约 v1

`schemas/*.schema.json` 从实际 C# 序列化契约导出，检查字段类型、必填项和未知字段。
它们是**结构 schema**；版本、范围、轨距正值、哈希、路径与跨记录关系需调用 Core 的 `Validate`，
不能只凭 JSON Schema 通过就接受一个任务。测试检查 schema 与序列化器是否漂移。

开发者改动数据类型后运行：

```powershell
dotnet run --project tests/DvdCalibrator.Tests -c Release -- --write-schemas
```

## 读取规则与冻结

JSON 使用大小写敏感的 camelCase。构造参数全部必填，包含值为零的轨距系数和显式 `null` 的字段。
本工具记录拒绝未知字段、重复字段、整数枚举、不支持的版本及不有限的数值；不提供宽松的历史迁移。
输入记录最大 4 MB，嵌套最大 48 层。照片等大附件不得直接内嵌这些 JSON。
高采样数的预测输出可能大于此输入限制；它是报告产物，不是导入任务或拟合配置。

几何字段见 `geometry.schema.json`。方向取 `cw` 或 `ccw`，并要求
`directionConvention="disc-xy-y-up-recording-side"`。物理盘种与容量确认不能由几何参数推断。

`GenerationSnapshot` 是不可变记录，**仅供合成数据**，要求
`recordType=generation-snapshot`、`schemaVersion=1`、`state=frozen`、生成器来源 `synthetic`。
创建由 `GenerationSnapshot.CreateSynthetic` 执行。不要将手写记录或上游工程改名后宣称为已验证的实盘快照。

哈希 v1：将强类型对象序列化，对对象键按 ordinal 排序，数组保持原序，紧凑 UTF-8 编码后计算 SHA-256。
快照计算时将自身 `contentSha256` 设为空字符串。数字由本项目的 .NET 序列化器写出完整 double 精度。
外部 `originalRecord` 的 JSON 数值 token 保留原始表示，不强制转换为 double。
这不是通用 RFC JSON 规范化；跨语言写入方应复现该规则并回读验证。本规则随 schema 版本固定。
哈希用于发现输入变化，不是数字签名或真实性认证。

## 观测与结果

`Observations` 保留定向后原图像素坐标、应用过的 EXIF 方向、定向尺寸、相对照片路径及字节哈希，
外缘、孔缘和有序描线列表。空列表允许作为编辑草稿；此版本不代表已完成矫正。
验证照片点在图像内、引用当前快照 ID/哈希、曲线 ID 不重复，照片路径不能是绝对路径或包含上级跳转。
阶段 B 新增独立 `photo-session` v2；此 v1 草稿不会自动升级为已矫正任务。

`FitResult` 是阶段 C/D 使用的最小契约，含输入哈希、算法版本、候选、误差与状态。
该 v1 草稿不是当前拟合输出；阶段 C 的实际入口使用独立 `trace-fit-result`，也不自动授予 `coarse-ready` 或 `fine-ready`。
当前实刻状态只允许 `pending`；后续实刻通过必须增加独立验证记录与绑定。
`forward-prediction` 与 `fit-result` 是不同产物，不得将前向输出当作拟合或配置应用成功。

## DiscImageStudio 适配范围

基准为 `upstream.lock.json` 中提交，支持：

- `format=dvd-nrzi-disc-fill-v2`、`algorithm=dispersion`、`fastOutput=true`。
- `imageMapping` 是该快速输出的 `ImageMappingOptions` 结构，全部几何项显式存在。
- `tone=null`，完整零起点、16 扇区对齐、填充长度与总长度相同，无混排文件或 hybrid ISO。
- `clockwise` 按固定上游编码映射为本工具的 CW/CCW，保留起始角、阈值和原始 JSON。

未知几何映射字段、缺失系数、tone 变换、部分写入或其他记录类型会报错。
根层诊断统计等额外字段原样保留，不把它们用于求解；解析成功也不会授予应用配置的权限。
非 fast 旁车、`.calibration.json` schema 6、`.discproj` 导入、流式任务尚未适配。

`import-discimage` 输出原始 `source.json` 字节副本和 `generation-record.json`，后者始终标记
`record-incomplete`，列出实盘类型与容量确认、源图字节与哈希、生成器身份及预处理核对等缺失证据。
侧车中的源路径不是内容哈希；文件结构相同也不能证明其生成程序的提交等于适配器基准。
不能将未补齐证据的导入记录直接作为 `predict` 的冻结快照。

## 外部快照的显式冻结

阶段 A 另有 `ExternalGenerationSnapshot`，记录类型 `external-generation-snapshot`。
将缺失于旁车的介质、真实容量、源图和生成器身份作为显式人工声明，与完整旁车映射一起冻结：

```powershell
dotnet run --project src/DvdCalibrator.Cli -c Release -- freeze-discimage --record my.iso.json --image original.png --media DVD+R --confirmed-sectors 2295104 --generator-version 1.3.6 --generator-commit 374c0630326ea61be9adca8d7de3c96f083c00df --out artifacts/frozen-001
dotnet run --project src/DvdCalibrator.Cli -c Release -- inspect --record artifacts/frozen-001/generation.json
```

上述值必须换成该任务实际使用的值，不能套用示例。输入 `--image` 表示声明此文件就是实际用于
生成的图像，生成器版本和提交同样标记为 `user-declared`，不会被侧车自动证明。
工具会保留阈值、起始角、所有原始映射字段，复制源图和旁车附件，先从副本解析与计算字节哈希，
再计算整个快照哈希。源图可为 PNG/BMP/JPEG/GIF，本阶段只保存字节，图像解码和 EXIF 在阶段 B 实现。

外部快照回读不仅校验内容哈希，也重解析旁车并核对派生模型及两个附件的哈希；换目录不依赖旧绝对路径。
冻结说明生成条件已记录，不等同于身份独立认证、图像已经识别或实刻结果通过。
外部快照可用 `inspect` 验证，也能作为桌面照片任务的生成条件；`predict` 的解析图案入口仍仅接受合成快照。

## 照片会话 v2

`PhotoSession` 使用 `schemaVersion=2`、`recordType=photo-session`，结构见 `schemas/photo-session-v2.schema.json`。
metadata 记录原始/定向文件哈希、RGB 像素哈希、原始/定向尺寸和应用的 EXIF 方向；观察面固定显式为 `recording-side`。
轮廓、遮罩和描线点均在定向后像素中心范围 `[0,w-1]×[0,h-1]`。

矫正绑定快照、照片身份、双圆轮廓、实体尺寸和算法版本哈希；确认再绑定结果整体哈希。
改轮廓清除变换/确认，源描点保留。遮罩/描线不参与透视计算，其改动不影响几何确认，但改变完整观测哈希。
后续拟合应绑定完整观测输入。任务包携带全部照片与生成附件，回读校验哈希，不依赖旧绝对路径。
操作、大小限制和当前范围见 [照片工作流](photo-workflow.md)。

## 描线拟合与补偿配置

阶段 C 的 `TraceFitReview` 包含 `trace-fit-result` v1、选中候选 ID 和候选确认哈希，结构见
`trace-fit-review.schema.json`。结果绑定快照哈希、完整照片会话哈希、生成参数与求解设置。
读回验证固定参数、残差身份/汇总及连续曲线是否与实际候选模型一致，不能给新参数沿用旧叠线。
照片任务以可选 `fit.json` 保存结果；改变会话清除拟合。旧照片 v2 会话结构不变。

`compensation-profile.schema.json` 对应完整参数主配置；导出时保留全部 double 精度，
不把界面显示值回写，不把照片整体转角混入生成模型。所有导出保持 `physicalValidation=pending`。
详情见 [描线拟合](trace-fitting.md)，结构 schema 通过不能代替运行时的输入绑定和语义验证。

## 密环与下一张预测

`dense-analysis.schema.json` 对应独立 `DenseAnalysis` v1，算法标识 `half-disc-multiangle-fragments-v1`。
包含输入哈希、设置、有效源分辨率、相位片段、局部频谱/有符号相位斜率、全参数候选、整片圈数分支、
训练/留出图像及相位误差、混合描线误差、完整重建曲线和下一张解析十字预测。
全局明暗极性是显式离散分支；照片转角不写入补偿几何。固定扇区数、位长、方向和三项轨距形状，
本版本只搜索两个半径。所有 double 原精度归档。

结果状态限于 `photo-insufficient`、`model-mismatch`、`dense-review`、`candidate-ambiguous`；
下一张另有 `next-disc-review`、`next-disc-undersampled` 和 `candidate-ambiguous`，均不是 `coarse-ready`。
`NextDiscDiagnostic.Generated` 记录用于预测的 G1，`FinePattern` 明确 0.3 mm 解析十字；不创建实际 ISO 或宣布可刻录。

可选 `dense.json` 与既有 `fit.json` 独立存入照片任务包。读取/附加/保存/导出时，`Verify` 从哈希绑定的定向原图重新提取观测，
按候选全参数重新评分和构造曲线/下一张诊断，逐项内容哈希比较。不能给新参数沿用旧分支、曲线、分数或支持标记。
这是一致性检查，不是数字签名；更改输入须重新计算。语义验证也不能由单独 JSON Schema 代替。

## 第二张计划与两盘关联

`fine-target-plan` v1 保存 Disc、完整 G1、第一张快照/会话/结果哈希、候选身份及全部受支持替代模型，
固定原图 SHA、版本/坐标/阈值/tone 契约、预计照片像素/mm、完整下一张预测和独立计划确认。
`contentSha256` 对清空自身哈希及确认字段后的内容求哈希；确认等于此哈希，且预测必须为 `plan-review`。
修改设置、参数、来源或预测均使确认失效；不能给不合格预测补写一个确认字符串来绕过检查。

`reference-target-simulation` v1 是明确的软件模拟快照，保存对应 Plan 哈希和固定契约，不冒充旁车。
真实第二张继续用 `external-generation-snapshot`，逐项检查实际附件、G1 全参数、介质声明、固定版本声明、
原图哈希、起始角、PSN offset、阈值和采样设置。原图使用资源原始字节，不依赖文件名判断身份。

`two-disc-task` v1 清单保存任务 ID、`external-record-required` / `simulation` 模式、Plan 和嵌套任务包字节哈希。
`.dvdcal` ZIP 只允许 `workflow.json`、`coarse.dvdphoto` 和可选 `fine.dvdphoto`。草稿计划可归档，未确认不能关联第二张。
上限 710 MB，嵌套单盘各 350 MB，清单 4 MB；不任意解压，拒绝重复/未知条目和解压超限。
打开后先验附件字节，再恢复原图和各自快照/观测，验证 Plan 与第一张当前结果一致。
第二张必须不同于第一张任务、快照和照片身份；模拟快照只允许进入 simulation 模式。

第二张使用 G1 独立拟合；修改其观测仅清除本张拟合，修改第一张则使既有 Plan 失效，需重新创建。
`two-disc-consistency` v1 保存跨盘模型形状 RMS/最大差以及第二张实测投影的有效像素与法向线宽。
最终两盘导出必须有第二张确认且一致性通过，沿用完整 `compensation-profile`，另附 Plan、两张照片任务及两盘报告。
所有输出 `physicalValidation=pending`；计划输入的预计分辨率与第二张由单应估计的分辨率分别保存，不互相替代。

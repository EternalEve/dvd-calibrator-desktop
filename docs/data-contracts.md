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
阶段 B 再加入变换链、遮罩与轮廓确认；格式扩展将显式升级版本。

`FitResult` 是阶段 C/D 使用的最小契约，含输入哈希、算法版本、候选、误差与状态。
阶段 A 不提供拟合器，不生成 `coarse-ready` 或 `fine-ready`；结果契约存在不代表算法已经实现。
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
非 fast 旁车、`.calibration.json` schema 6、`.discproj`、流式任务尚未适配。

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
外部快照目前可用 `inspect` 验证；`predict` 的解析图案入口仅接受合成快照，通用图片预测在图像模块阶段接入。

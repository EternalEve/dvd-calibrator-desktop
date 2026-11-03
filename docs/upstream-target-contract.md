# 固定上游 DVD 标靶契约（0.5.0）

## 基准与原图

DiscImageStudio 1.3.6，提交 `374c0630326ea61be9adca8d7de3c96f083c00df`。
本版本最小适配其 `CalibrationReferencePattern` 与 DVD 普通图片编码路径，原样携带：

- `target_slim.png`：2400×2400 RGB，SHA-256 `c98ffe87db7cf9c3f132352814e1ce611c44f704de5b7212da4083188257c829`。
- `target_slim.gray.gz`：原始灰度像素的压缩资源，解压后 5,760,000 字节，SHA-256 `3d351bdfb817b943c1d1b4aedc97fc4f0a111e7e1537748f70cb5b180ba96852`。
- `CalibrationReferencePattern.cs`：固定上游采样实现，保留文件字节、GPL 声明与锁哈希。

图片不重画、不缩放重编码。Core 用嵌入灰度资源采样，导出直接写原始 PNG 字节。
Windows 检查逐像素比较 PNG 解码的三个通道与灰度资源，避免二者分离。

## 坐标与完整生成映射

DVD 原图中心为 `(1199.5,1199.5)`。源盘面使用记录面 x 右/y 上：

```text
pixel_x = 1199.5 + x_mm * 1199.5 / G.outerRadiusMm
pixel_y = 1199.5 - y_mm * 1199.5 / G.outerRadiusMm
```

比例由生成外半径 G.ro 决定，预览盘的实体半径 60 mm 不参与编码缩放。
上游参考采样器参数采用 y 下，本工具 wrapper 只在边界翻转 y；保持原始双线性灰度、透明度与舍入运算顺序。
圆环源半径为 490/570/650/730/810/890/970/1050/1130 像素，名义线宽 10 像素；
对应毫米尺寸均乘 `G.ro/1199.5`，线宽约 `10*G.ro/1199.5` mm。
圆环目前是视觉辅助，精调拟合只用明确的十字中心线，不额外假定圆环提供角度观测。

轨迹进度使用完整三次轨距和总轨长，`r=inverse_u_G(u)`、
`alpha=startAngle+sign*theta_G(r)`，CW 在 y 上为负。标靶采样及当前两盘拟合使用同一共享几何，禁止线性半径插值或把累计角折回后当作连续相位。

## 最小生成契约

第二张计划支持明确的完整 DVD-R/DVD+R、零 LBA、16 扇区对齐、完整容量映射、无混排文件，保留 G1 全部参数。
当前契约固定起始角 0°、PSN offset `0x30000`、image/alpha threshold 128、constraint-step 1、`tone=null`，要求固定版本声明及原图精确哈希。
计划导出参数数组逐项写明几何、位长、方向、阈值和轨距形状，不依赖生成器几何默认值。

实际引擎为 `dispersion` / `fast-output=true`。legacy `tone=null` 的快速输出使用上游灰度 codeword 规则，
不是简单声称每个输出位都由 128 阈值决定；原始像素/采样契约与旁车设置均保留。
当前照片合成是完整几何和源灰度的数值渲染，不是该 codeword 的光学亮度模型。
这里没有适配上游 `.calibration.json` schema 6 导入、其他光盘类型或任意 GUI 图层变换。
生成器版本、提交和介质容量在外部快照中仍标记为显式用户声明，旁车本身不能独立证明可执行文件身份。

## 实际软件对照

`tools/Test-UpstreamContract.ps1` 使用本机固定 checkout 的实际引擎程序集：

1. 实际 `RasterImage.SampleDisc` 比较 20,000 个空间采样点，含非整数外半径；保存其中 128 点作普通回归。
2. 实际引擎轨迹与采样比较 66 组进度，含 CW/CCW、17° 起始角、133.33 nm 和非零三次形状。非零起始角用于验证坐标，当前两盘契约仍只允许 0°。
3. 读取本工具导出的结构化 argv，实际生成 16 扇区 ISO 和旁车，再冻结原始 PNG 与旁车，执行计划核对。

这是小容量软件 fixture，不能作为足容量刻录、设备或光学效果证明。结果路径在 artifacts，ISO 不入库。
仓库 `fixtures/upstream-target-contract.json` / `upstream-reference-fast-fill.iso.json` 保存可离线回归的真实软件输出。

复现需要固定源码和已构建的 Windows 引擎：

```powershell
# 首次准备 checkout 时固定上述提交。
# 离线参考构建可将 src/DiscImageStudio.ImageIO/*.csproj 的两处 TargetFrameworks
# 仅收窄到 net10.0-windows，避免不使用的 Stb 跨平台包；不修改引擎/几何/采样逻辑。
dotnet build .local/upstream/DiscImageStudio/src/DvdImageSolver -c Release
./tools/Test-UpstreamContract.ps1
```

普通 `tools/Test.ps1` 不依赖忽略目录的上游构建，使用已锁定源码、资源和实际采样 fixture。

# 上游软件输出样例

`upstream-fast-fill.iso.json` 由固定上游源码实际运行产生，没有手工拼写字段。
输入是仓库中的 `upstream-input.bmp`，4×4 黑白 BMP，生成脚本为 `tools/New-UpstreamFixture.ps1`。
为了只写 32 KiB 测试数据，容量设置为 16 扇区；这不是物理 DVD 参数、实盘标定样例或可供刻录的推荐配置。

生成基准：DiscImageStudio `374c0630326ea61be9adca8d7de3c96f083c00df`，版本前缀 `1.3.6`。
本机 .NET SDK 10.0.203。参考构建只把 ImageIO 的 TargetFrameworks（两处）收窄为
`net10.0-windows`，避免还原本次不使用的跨平台 Stb 包；没有修改引擎、几何或序列化逻辑。

复现步骤：

```powershell
# 准备固定提交的上游 checkout；按上述说明收窄参考构建的 ImageIO 框架。
dotnet build .local/upstream/DiscImageStudio/src/DvdImageSolver -c Release
./tools/New-UpstreamFixture.ps1
```

脚本实际执行：

```text
solve --image upstream-input.bmp --iso-output .local/upstream-fixture.iso
  --total-sectors 16 --fill-sectors 16 --inner-radius-mm 24 --outer-radius-mm 58
  --channel-bit-nm 133.33 --pitch-linear 0.02 --pitch-quadratic -0.03 --pitch-cubic 0.015
  --spiral-direction cw --algorithm dispersion --fast-output true --fast-parallelism 1
```

历史 JSON 保留当时绝对路径和耗时，因此重建后的字节哈希可以变化。运行时不依赖这些路径，
冻结/导入从明确提供的源文件复制附件。ISO 不进入版本控制，也不用于操作刻录设备。

`photo-session-v2.synthetic.json` 来自 `SyntheticPhoto.Create()` 的已知投影圆周，用于 CLI 几何回归。
这是无照片附件的独立 JSON，不是实拍任务包。Windows 图像检查在各自 artifacts 目录产出
同类 `draft-session.json`、完整 `.dvdphoto` 和预览，可用于重建该软件样例。

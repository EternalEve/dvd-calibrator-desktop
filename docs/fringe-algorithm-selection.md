# 条纹识别算法选型与实拍试验

优先复用现成条纹分析实现。先验证条纹频率、相位和跨扇区一致性，再接回本项目的 DVD 几何拟合。

## 候选

| 方案 | 已有实现 | 用途与判断 |
| --- | --- | --- |
| WFT / WFR | [pyGPA](https://github.com/TAdeJong/pyGPA) 的 GPA、wfr3；MIT 许可 | 优先验证。直接复用窗口傅里叶脊线算法，逐局部寻找条纹载频和相位。 |
| Gabor 复滤波 | [scikit-image gabor](https://scikit-image.org/docs/stable/api/skimage.filters.html#skimage.filters.gabor) | 对照实现，返回实部和虚部响应。固定频率版本适合检查条纹检测；变频需滤波器组。 |
| 作者 WFT 参考代码 | [Qian Kemao 的 Updated WFT](https://www.mathworks.com/matlabcentral/fileexchange/64962-updated-wft-for-fringe-analysis) | MATLAB 参考实现，用于算法对照。 |
| .NET 图像计算组件 | [OpenCvSharp](https://github.com/shimat/opencvsharp) / [OpenCV Gabor](https://docs.opencv.org/4.x/d4/d86/group__imgproc__filter.html) | 后续 Windows 接入可评估；提供基础图像算子，不等于完整的条纹标定算法。 |

## 本次实拍试验

输入是用户提供的 photo-task-20261104-034519.dvdphoto 的极坐标展开图，取绿色通道，缩至 720×480。用 SciPy 高斯滤波去除缓慢的径向背景，调用 pyGPA 原始 GPA、wfr3 函数；用 scikit-image 的 gabor 交叉比较。

- WFR 搜索 0.025–0.15 周期/像素，步长 0.005；高斯窗口 sigma=9。
- 清楚条纹区的频率中位数约 0.07–0.075 周期/像素，即每周期约 13–14 个展开图像素。
- 两个清楚区域的 WFR 响应幅度中位数约 0.033、0.047；一个弱条纹区域约 0.0017。
- WFR 与 Gabor 均显示出清楚条纹区域的局部响应。此次单图实验支持继续评估 WFR，尚未证明相位符号、断纹圈数或补偿参数的正确性。

本次试验的本地脚本及依赖清单位于 `artifacts/algorithm-evaluation/`。运行时曾将 Python 依赖安装在该目录的 `python-libs/`，试验输出包括结果 JSON、滤波响应和对照图。

## 0.8.3 接入结果

选择 pyGPA WFR，并适配到 C#；FFT、插值和优化使用 Math.NET Numerics，固定依赖包随源码提供。原始 pyGPA 源码、MIT 许可证和来源哈希保存在 `third_party/pyGPA`。生产程序无需 Python 或网络下载。

与原始 Python 实现的幅度、相位、脊线频率和相干性逐项对照，误差不超过 1e-9。已知参数合成照片的半径恢复误差小于 0.0003 mm，并检查空白照片拒绝、取消计算、记录篡改和任务恢复。

同一用户实拍任务由原先 0 个有效片段，改为 2422 个局部观测、约 65% 有效径向覆盖。独立检查法向 RMS 约 0.066 mm，粗调结果可进入第二张计划。该结果是本张照片的拟合与检查结果，最终补偿仍需第二张实盘验证。参数映射继续使用原有完整 DVD 几何模型，具体门槛见 [密环分析](dense-analysis.md)。

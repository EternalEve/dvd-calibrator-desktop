# 默认深色模式（0.7.1）

桌面默认使用深色主题。颜色与控件样式集中在 `src/DvdCalibrator.App/Themes/Dark.xaml`，窗口及手绘预测图使用同一组命名画刷。

窗口为深灰底，左右工作区使用稍亮的表面色，双盘流程和状态栏使用蓝灰色分层。主文字、次要说明、禁用状态分别设色；按钮提供悬停、按下、禁用及键盘焦点样式，预览标签保留选中指示和完整内容拉伸。

输入框、下拉选择、滑块、滚动条和工具提示使用 [WPF 自带 Fluent 深色主题](https://github.com/dotnet/wpf/blob/main/Documentation/docs/using-fluent.md)。应用指定深色模式，不更改 Windows 全局主题。系统文件对话框、原生消息框的外观由 Windows 绘制。

原始照片、EXIF 定向、像素采样及已有标注的语义颜色没有改变。第二张预测图只调整底色、轮廓与曲线的显示样式，不改变几何、采样或拟合参数。

验收使用既有 Windows 图像/任务/状态检查，并直接查看离屏生成的拟合、双盘预测和失败诊断视图。原生窗口与系统对话框交互仍按安排放在最后；颜色预览不代表原生交互通过。

便携包可用 `tools/Publish-Portable.ps1` 重建，发布前会检查 `PresentationFramework.Fluent.dll`，并从 ZIP 迁移后运行安装自检和 CLI 软件流程。

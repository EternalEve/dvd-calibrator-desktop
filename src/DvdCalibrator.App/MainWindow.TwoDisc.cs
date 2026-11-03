using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;
using Microsoft.Win32;

namespace DvdCalibrator.App;

public sealed partial class MainWindow
{
    private TwoDiscTask? twoDisc;
    private readonly TextBox finePixels = new() { Text = "8", Width = 42, Margin = new(3), VerticalContentAlignment = VerticalAlignment.Center,
        ToolTip = "预计第二张照片的有效像素/mm，是拍摄计划输入。修改后请重新创建计划，现有 G1 不会随文本改动。" };
    private readonly TextBlock workflowDetail = new() { Name = "TwoDiscStatus", Margin = new(8, 2, 8, 5), TextWrapping = TextWrapping.Wrap };
    private Button prepareFine = null!, confirmFine = null!, targetExport = null!, importFine = null!, saveTwoDisc = null!, finalExport = null!;
    private string? forecastPlanHash;
    private FrameworkElement BuildTwoDiscToolbar()
    {
        var panel = new StackPanel { Margin = new(12, 0, 12, 5) };
        panel.SetResourceReference(Panel.BackgroundProperty, "WorkflowBackgroundBrush");
        var actions = new WrapPanel();
        actions.Children.Add(new TextBlock { Text = "两盘任务 · 预计精调像素/mm", Margin = new(8), VerticalAlignment = VerticalAlignment.Center }); actions.Children.Add(finePixels);
        prepareFine = Button("创建第二张计划", PrepareFine); actions.Children.Add(prepareFine);
        confirmFine = Button("确认第二张计划", ConfirmFine); actions.Children.Add(confirmFine);
        targetExport = Button("导出第二张靶", ExportFineTarget); actions.Children.Add(targetExport);
        importFine = Button("导入第二张照片", ImportFinePhoto); actions.Children.Add(importFine);
        actions.Children.Add(Button("查看第一张", () => ShowDisc(false))); actions.Children.Add(Button("查看第二张", () => ShowDisc(true)));
        actions.Children.Add(Button("打开两盘任务", OpenTwoDisc)); saveTwoDisc = Button("保存两盘任务", SaveTwoDisc); actions.Children.Add(saveTwoDisc);
        finalExport = Button("导出两盘最终配置", ExportTwoDiscFinal); actions.Children.Add(finalExport);
        actions.Children.Add(Button("检查两盘一致性", CheckTwoDisc));
        actions.Children.Add(Button("两盘合成演示", async () => await LoadTwoDiscDemo()));
        panel.Children.Add(actions); panel.Children.Add(workflowDetail); return panel;
    }
    private void PrepareFine()
    {
        if (task is null || calculation is not null) return;
        if (draftMask.Count > 0) throw new ArgumentException("请先结束或放弃遮罩草稿。");
        if (twoDisc is not null && ReferenceEquals(task, twoDisc.Fine)) throw new ArgumentException("请先查看第一张，再创建第二张计划。");
        if (twoDisc?.Fine is not null && !MayReplace()) return;
        double pixels = double.Parse(finePixels.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
        twoDisc = TwoDiscTask.Create(task, pixels); dirty = true; Refresh();
        SetStatus("计划已创建。G1 保存第一张选中候选的全部参数；请对照第一张完整曲线与已导出的预测诊断，核对预计精调照片像素密度，再确认第二张计划。");
    }
    private void ConfirmFine()
    {
        if (twoDisc is null || calculation is not null || !MaskDraftReady()) return;
        twoDisc.ConfirmPlan(); dirty = true; Refresh(); SetStatus("第二张 G1 与固定原图契约已确认。导出标靶后用固定版本生成，再冻结真实旁车和原图；导入第二张时逐项核对。实刻仍待验证。");
    }
    private void ExportFineTarget()
    {
        if (twoDisc is null || calculation is not null || !MaskDraftReady()) return;
        twoDisc.Validate();
        var dialog = new OpenFolderDialog { Title = "选择父目录，将新建第二张标靶与计划目录" }; if (dialog.ShowDialog() != true) return;
        string output = Path.Combine(dialog.FolderName, "fine-target-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        FineTargetExport.Write(output, twoDisc.Plan); SetStatus("已导出固定原图、完整 G1、结构化生成参数和预测报告。生成工作目录为导出目录；先打开 report.html：" + output);
    }
    private void ImportFinePhoto()
    {
        if (twoDisc is null || calculation is not null) return;
        if (!twoDisc.Plan.IsConfirmed) throw new ArgumentException("请先确认第二张计划。");
        if (draftMask.Count > 0) throw new ArgumentException("请先结束遮罩草稿。");
        var generation = new OpenFileDialog { Filter = "第二张冻结生成快照|generation.json;*.json", Title = "选择第二张实际旁车冻结后的 generation.json" };
        if (generation.ShowDialog() != true) return;
        var snapshot = SnapshotBundle.Load(generation.FileName);
        var photo = new OpenFileDialog { Filter = "第二张记录面照片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff", Title = "选择对应第二张的原始记录面照片" };
        if (photo.ShowDialog() != true) return;
        var next = new PhotoTask(snapshot, PhotoCodec.Import(photo.FileName)); twoDisc.BindFine(next);
        Load(next, true); SetStatus("已验证第二张生成记录和原图契约。请标第二张外缘/孔缘、确认矫正，再描右侧主十字中心线及对侧 180° 检查线；拟合会使用 G1。");
    }
    private void ShowDisc(bool fine)
    {
        if (twoDisc is null || calculation is not null) return;
        if (draftMask.Count > 0) throw new ArgumentException("请先结束或放弃遮罩草稿。");
        var next = fine ? twoDisc.Fine : twoDisc.Coarse;
        if (next is null) { SetStatus("第二张尚未导入。请先导出标靶、生成并冻结实际旁车，再导入对应照片。"); return; }
        bool changed = dirty; Load(next, changed); SetStatus(fine ? "当前第二张：使用独立 G1 和本张照片几何。" : "当前第一张：修改观测或候选会使既有第二张计划失效，请另建计划。");
    }
    private void OpenTwoDisc()
    {
        if (calculation is not null) return;
        var dialog = new OpenFileDialog { Filter = "两盘标定任务|*.dvdcal" }; if (dialog.ShowDialog() != true || !MayReplace()) return;
        LoadTwoDiscTask(TwoDiscTask.Load(dialog.FileName)); SetStatus("两盘任务已恢复，生成参数、原图、计划、候选和确认均已校验。");
    }
    private void SaveTwoDisc()
    {
        if (twoDisc is null || calculation is not null) return;
        if (draftMask.Count > 0) throw new ArgumentException("请先结束遮罩草稿。");
        var dialog = new SaveFileDialog { Filter = "两盘标定任务|*.dvdcal", DefaultExt = ".dvdcal", OverwritePrompt = false, FileName = "two-disc-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        if (dialog.ShowDialog() != true) return;
        twoDisc.SaveNew(dialog.FileName); dirty = false; Refresh(); SetStatus("两盘任务已保存，包含两张原图、各自生成记录和观测、精调计划及结果，可换目录恢复。");
    }
    private void ExportTwoDiscFinal()
    {
        if (twoDisc is null || calculation is not null || !MaskDraftReady()) return;
        var dialog = new OpenFolderDialog { Title = "选择父目录，将新建最终配置与两盘追溯报告" }; if (dialog.ShowDialog() != true) return;
        string output = Path.Combine(dialog.FolderName, "two-disc-final-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        twoDisc.ExportFinal(output); SetStatus("已导出第二张最终配置、工程兼容回读和两盘追溯报告；physicalValidation 仍为 pending：" + output);
    }
    private void CheckTwoDisc()
    {
        if (twoDisc is null || calculation is not null || !MaskDraftReady()) return;
        var result = twoDisc.CheckConsistency();
        SetStatus($"两盘形状差 RMS {result.CoarseShapeRmsMm:F4} mm / 最大 {result.CoarseShapeMaximumMm:F4} mm；第二张有效线宽 {result.FineLineWidthPixels:F2} px。"
            + (result.Status == "final-review" ? "软件一致性检查通过，可导出带两盘来源的最终配置；实刻仍待验证。" : CalibrationGuidance.Describe([CalibrationGuidance.ForCode(result.Status)])));
    }
    public void LoadTwoDiscTask(TwoDiscTask next)
    {
        next.Validate(); twoDisc = next; finePixels.Text = next.Plan.ExpectedPhotoPixelsPerMm.ToString("G17", CultureInfo.InvariantCulture);
        Load(next.Fine ?? next.Coarse, false);
    }
    public async Task LoadTwoDiscDemo()
    {
        if (calculation is not null || !MayReplace()) return;
        calculation = new(); var token = calculation.Token; Refresh(); SetStatus("正在创建两盘合成任务，第一张分析与已确认计划均来自软件样本……");
        try
        {
            var next = await Task.Run(() => SyntheticPhoto.CreateTwoDiscDemo(token), token);
            token.ThrowIfCancellationRequested(); LoadTwoDiscTask(next); dirty = true;
            SetStatus("两盘软件演示：第一张粗调与 G1 计划已模拟确认，第二张使用固定 target_slim 原图和独立照片。点击“拟合描线”，核对并确认候选后可导出两盘最终配置。");
        }
        catch (OperationCanceledException) { SetStatus("两盘演示创建已取消，原有任务保留。"); }
        catch (Exception e) when (Expected(e)) { SetStatus(CalibrationGuidance.Describe(e)); }
        finally { calculation.Dispose(); calculation = null; Refresh(); if (task?.Session.Rectification is not null && IsLoaded && rectified.Source is null) _ = RefreshPreviews(); }
    }
    private void RefreshTwoDisc()
    {
        bool busy = calculation is not null;
        prepareFine.IsEnabled = task?.Session.IsConfirmed == true && !busy;
        confirmFine.IsEnabled = twoDisc is not null && !twoDisc.Plan.IsConfirmed && twoDisc.Plan.Forecast.Status == "plan-review" && !busy;
        targetExport.IsEnabled = importFine.IsEnabled = twoDisc?.Plan.IsConfirmed == true && !busy;
        saveTwoDisc.IsEnabled = twoDisc is not null && !busy; finalExport.IsEnabled = twoDisc?.Fine?.Fit?.IsConfirmed == true && !busy;
        finePixels.IsEnabled = !busy;
        if (twoDisc is null) { workflowDetail.Text = "先完成第一张描线确认或密环分析；预计像素/mm 是第二张拍摄计划输入，尚非实测值。"; fineForecastImage.Source = null; forecastPlanHash = null; return; }
        var plan = twoDisc.Plan; string state = twoDisc.Status switch
        {
            "plan-review" => "核对并确认第二张计划", "awaiting-fine-photo" => "等待第二张实际生成记录与照片",
            "fine-annotation" => "第二张标注 / 独立精调", "fine-fit-review" => "核对第二张候选", _ => "可检查两盘一致性并导出"
        };
        if (forecastPlanHash != plan.ContentSha256)
        {
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRectangle((Brush)FindResource("ViewportBackgroundBrush"), null, new Rect(-64, -64, 128, 128));
                dc.DrawEllipse((Brush)FindResource("ForecastDiscBrush"), new Pen((Brush)FindResource("ForecastOutlineBrush"), .3), new Point(0, 0), 60, 60);
                dc.DrawEllipse((Brush)FindResource("ViewportBackgroundBrush"), null, new Point(0, 0), 7.5, 7.5);
                foreach (var c in plan.Forecast.Curves)
                {
                    if (c.Curve.Zip(c.Curve.Skip(1)).Any(p => Math.Abs(p.First.AngleRadians - p.Second.AngleRadians) > Math.PI / 8)) continue;
                    foreach (double offset in new[] { 0d, Math.PI / 2, Math.PI, 3 * Math.PI / 2 })
                    {
                        var path = new StreamGeometry();
                        using (var context = path.Open())
                        {
                            var first = c.Curve[0]; context.BeginFigure(new Point(first.RadiusMm * Math.Cos(first.AngleRadians + offset), -first.RadiusMm * Math.Sin(first.AngleRadians + offset)), false, false);
                            foreach (var p in c.Curve.Skip(1)) context.LineTo(new Point(p.RadiusMm * Math.Cos(p.AngleRadians + offset), -p.RadiusMm * Math.Sin(p.AngleRadians + offset)), true, false);
                        }
                        path.Freeze(); dc.DrawGeometry(null, new Pen((Brush)FindResource("ForecastCurveBrush"), .45), path);
                    }
                }
            }
            drawing.Freeze(); fineForecastImage.Source = new DrawingImage(drawing); forecastPlanHash = plan.ContentSha256;
        }
        workflowDetail.Text = $"{(twoDisc.EvidenceMode == "simulation" ? "软件模拟 · " : "实际记录流程 · ")}{state} · 当前{(ReferenceEquals(task, twoDisc.Fine) ? "第二张 G1" : "第一张 G0")}\n"
            + $"G1 ri {plan.Generated.InnerRadiusMm:F6} / ro {plan.Generated.OuterRadiusMm:F6} mm · 候选差 {plan.Forecast.MaximumCandidateDifferenceMm:F3} mm · 预计线宽 {plan.Forecast.MinimumLineWidthPixels:F1} px · 实刻待验证。";
        if (plan.Forecast.Status != "plan-review") workflowDetail.Text += " " + CalibrationGuidance.Describe([CalibrationGuidance.ForCode(plan.Forecast.Status)]);
        bool current = RecordJson.Hash(twoDisc.Coarse.Session) == plan.Coarse.SessionSha256
            && (plan.Coarse.Kind == "trace" ? twoDisc.Coarse.Fit is not null && RecordJson.Hash(twoDisc.Coarse.Fit) == plan.Coarse.ResultSha256
                : twoDisc.Coarse.Dense is not null && RecordJson.Hash(twoDisc.Coarse.Dense) == plan.Coarse.ResultSha256);
        if (!current)
        {
            workflowDetail.Text += " 第一张已改变，请重新创建计划。";
            confirmFine.IsEnabled = targetExport.IsEnabled = importFine.IsEnabled = saveTwoDisc.IsEnabled = finalExport.IsEnabled = false;
        }
    }
}

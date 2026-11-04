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
        ToolTip = "预计第二张照片的有效像素/mm，用于拍摄清晰度检查。" };
    private readonly TextBlock workflowDetail = new() { Name = "TwoDiscStatus", Margin = new(8, 2, 8, 5), TextWrapping = TextWrapping.Wrap };
    private Button copyFineParameters = null!, copyFinalParameters = null!, updateFineEstimate = null!, replaceFine = null!, importFine = null!, replaceFineRecord = null!, removeFine = null!;
    private readonly List<TextBox> fineParameterBoxes = [], finalParameterBoxes = [];
    private string? forecastPlanHash;
    private (FineTargetPlan Plan, SnapshotBundle Snapshot)? pendingFineRecord;
    private SnapshotBundle? FineSnapshot => twoDisc?.Fine?.Snapshot ?? pendingFineRecord?.Snapshot;

    private TwoDiscTask PrepareWorkflow(double pixels)
    {
        var next = twoDisc?.UsesManualParameters == true
            ? TwoDiscTask.CreateManual(twoDisc.Plan.Disc, twoDisc.Plan.Generated, pixels, FirstPhoto)
            : TwoDiscTask.Create(FirstPhoto!, pixels);
        if (next.Plan.Forecast.Status == "plan-review") next.ConfirmPlan();
        return next;
    }

    private void EnsureFinePreparation()
    {
        if (!CoarseReady && twoDisc?.UsesManualParameters != true) return;
        if (!PlanCurrent)
        {
            if (twoDisc?.Fine is not null) return;
            double pixels = twoDisc?.Plan.ExpectedPhotoPixelsPerMm ?? 8;
            twoDisc = PrepareWorkflow(pixels); finePixels.Text = pixels.ToString("G17", CultureInfo.InvariantCulture); dirty = true;
        }
        else if (!twoDisc!.Plan.IsConfirmed && twoDisc.Plan.Forecast.Status == "plan-review")
        { twoDisc.ConfirmPlan(); dirty = true; }
    }

    private void UpdateFineEstimate()
    {
        if (calculation is not null || !MaskDraftReady() || (!CoarseReady && twoDisc?.UsesManualParameters != true) || twoDisc?.Fine is not null) return;
        double pixels = double.Parse(finePixels.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
        twoDisc = PrepareWorkflow(pixels); dirty = true; Refresh(); SetStatus("拍摄清晰度检查已更新，粗调参数保持本次测量结果。");
    }

    private void ReplaceFinePreparation()
    {
        if (calculation is not null || !MaskDraftReady() || !CoarseReady || twoDisc?.Fine is null || PlanCurrent) return;
        if (MessageBox.Show(this, "第一张粗调结果已经改变。重新准备会解除旧第二张照片及结果的关联，请先保留旧资料。继续？",
            "重新准备第二张", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        twoDisc = PrepareWorkflow(twoDisc.Plan.ExpectedPhotoPixelsPerMm); dirty = true; Refresh();
    }

    private static (string Label, string Value)[] FineParameterValues(GeometryParameters p)
    {
        string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        return [("内半径 (mm)", N(p.InnerRadiusMm)), ("外半径 (mm)", N(p.OuterRadiusMm)),
            ("扇区数", p.TotalSectors.ToString(CultureInfo.InvariantCulture)), ("一阶轨距", N(p.PitchLinear)),
            ("二阶轨距", N(p.PitchQuadratic)), ("三阶轨距", N(p.PitchCubic)), ("位长 (nm)", N(p.ChannelBitLengthNm)),
            ("螺旋方向", p.Direction == SpiralDirection.Cw ? "顺时针 (CW)" : "逆时针 (CCW)")];
    }

    private void CopyFineParameters()
    {
        if (calculation is not null || !PlanCurrent || !twoDisc!.Plan.IsConfirmed || !MaskDraftReady()) return;
        CopyParameters(twoDisc.Plan.Generated);
        SetStatus("参数已复制。到 DiscImageStudio 的工程设置填入，再到“标定”页核对内外半径并生成镜像。");
    }
    private void CopyFinalParameters()
    {
        if (calculation is not null || !FinalReady || !MaskDraftReady()) return;
        CopyParameters(twoDisc!.Fine!.Fit!.Selected.Parameters);
        SetStatus("补偿参数已复制。请在 DiscImageStudio 的工程设置中应用。");
    }
    private static void CopyParameters(GeometryParameters parameters) =>
        Clipboard.SetText(string.Join(Environment.NewLine, FineParameterValues(parameters).Select(p => $"{p.Label}: {p.Value}")));

    private static void DisplayParameters(List<TextBox> boxes, GeometryParameters? parameters)
    {
        var values = parameters is null ? [] : FineParameterValues(parameters);
        for (int i = 0; i < boxes.Count; i++) boxes[i].Text = values.Length == 0 ? "" : values[i].Value;
    }
    private void ImportFinePhoto(bool chooseGeneration = false)
    {
        if (twoDisc is null || calculation is not null) return;
        if (!PlanCurrent || !twoDisc.Plan.IsConfirmed) throw new ArgumentException("请先返回第 4 步检查粗调参数和第二张拍摄要求。");
        if (draftMask.Count > 0) throw new ArgumentException("请先结束遮罩草稿。");
        var snapshot = FineSnapshot;
        if (chooseGeneration || snapshot is null)
        {
            var generation = new OpenFileDialog { Filter = "第二张生成记录|generation.json;*.json", Title = "选择第二张 ISO 同目录的 .calibration.json（或 .iso.json）" };
            if (generation.ShowDialog(this) != true) return;
            snapshot = SelectGeneration(generation.FileName, fine: true);
            if (snapshot is null) return;
        }
        var photo = new OpenFileDialog { Filter = "第二张记录面照片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff", Title = "选择对应第二张的原始记录面照片" };
        if (photo.ShowDialog(this) != true) return;
        var next = new PhotoTask(snapshot, PhotoCodec.Import(photo.FileName));
        if (!MayChangeFinePhoto()) return;
        try { SetFinePhoto(next); }
        catch (CalibrationInputException e) when (e.Code == "fine-generation-mismatch")
        {
            MessageBox.Show(this, "请确认提交的.json或图片正确\n若再次发生请确定上一页的参数与实际使用参数相符",
                "无法完成操作", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetStatus("第二张生成记录和照片已核对。点击下一步，独立校正第二张照片。");
    }
    private bool MayChangeFinePhoto() => MayChangePhoto(twoDisc?.Fine, "更换或移除第二张照片会清除第二张的标注和测量。继续？");
    private void RemoveFinePhoto()
    {
        if (calculation is not null || twoDisc?.Fine is null || !MaskDraftReady() || !MayChangeFinePhoto()) return;
        SetFinePhoto(null);
        SetStatus("第二张照片已移除，生成记录已保留。请选择照片。");
    }
    private void SetFinePhoto(PhotoTask? next)
    {
        var workflow = twoDisc!;
        if (next is null)
        {
            pendingFineRecord = (workflow.Plan, workflow.Fine!.Snapshot);
            workflow.RemoveFine();
        }
        else
        {
            workflow.BindFine(next, replaceExisting: true);
            pendingFineRecord = null;
        }
        CurrentStep = CalibrationStep.FineImport;
        Load(next ?? workflow.Coarse, true, false);
    }
    private void ShowDisc(bool fine)
    {
        if (twoDisc is null || calculation is not null) return;
        if (draftMask.Count > 0) throw new ArgumentException("请先结束或放弃遮罩草稿。");
        var next = fine ? twoDisc.Fine : twoDisc.Coarse;
        if (next is null) { SetStatus("第二张尚未导入。请在原软件填入粗调参数，生成辅助标定盘，再提交生成记录与照片。"); return; }
        if (ReferenceEquals(task, next)) return;
        bool changed = dirty; Load(next, changed, false); SetStatus(fine ? "当前第二张：使用本张实际生成参数和独立照片校正。" : "当前第一张：修改粗调结果后，第二张需要使用更新后的参数。");
    }
    private void SaveTwoDisc()
    {
        if (twoDisc is null || calculation is not null) return;
        if (draftMask.Count > 0) throw new ArgumentException("请先结束遮罩草稿。");
        var dialog = new SaveFileDialog { Filter = "标定任务|*.dvdcal", DefaultExt = ".dvdcal", OverwritePrompt = false, FileName = "calibration-task-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        if (dialog.ShowDialog() != true) return;
        twoDisc.SaveNew(dialog.FileName); dirty = false; Refresh(); SetStatus("任务已保存。下次点“打开任务”可继续。");
    }
    internal async Task ReviewFinalAsync()
    {
        if (calculation is not null || CurrentStep != CalibrationStep.Finish || !StepReady(CurrentStep)
            || FinalCheckCurrent || !MaskDraftReady()) return;
        var workflow = twoDisc!;
        var coarse = workflow.Coarse?.Session;
        var coarseResult = CoarseResult;
        var fine = workflow.Fine!.Session;
        var fit = workflow.Fine.Fit;
        var plan = workflow.Plan.ContentSha256;
        calculation = new(); var token = calculation.Token; Refresh();
        SetStatus("正在检查补偿结果……");
        TwoDiscConsistency? consistency = null;
        double? lineWidth = null;
        string? issue = null;
        try
        {
            if (workflow.UsesManualParameters)
                lineWidth = await Task.Run(() => workflow.CheckFinePhotoLineWidth(token), token);
            else
                consistency = await Task.Run(() => workflow.CheckConsistency(token), token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            SetStatus("检查已取消。重新打开第 08 步可再次检查。");
            return;
        }
        catch (Exception e) when (Expected(e)) { issue = CalibrationGuidance.Describe(e); }
        finally
        {
            if (!token.IsCancellationRequested && (consistency is not null || lineWidth is not null || issue is not null))
            {
                checkedWorkflow = workflow; checkedCoarseSession = coarse; checkedCoarseResult = coarseResult;
                checkedFineSession = fine; checkedFineFit = fit; checkedPlan = plan;
                checkedConsistency = consistency; checkedFineWidth = lineWidth; finalCheckIssue = issue;
            }
            calculation.Dispose(); calculation = null; Refresh();
            if (FinalCheckCurrent) SetStatus(FinalReady ? "补偿参数已准备好。" : resultSummary.Text);
        }
    }
    public void LoadTwoDiscTask(TwoDiscTask next)
    {
        next.Validate(); twoDisc = next; pendingFineRecord = null; finePixels.Text = next.Plan.ExpectedPhotoPixelsPerMm.ToString("G17", CultureInfo.InvariantCulture);
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
            SetStatus("两盘软件演示：拟合并确认第二张曲线后，可在第 08 步查看补偿参数。");
        }
        catch (OperationCanceledException) { SetStatus("两盘演示创建已取消，原有任务保留。"); }
        catch (Exception e) when (Expected(e)) { SetStatus(CalibrationGuidance.Describe(e)); }
        finally { calculation.Dispose(); calculation = null; Refresh(); if (task?.Session.Rectification is not null && IsLoaded && rectified.Source is null) _ = RefreshPreviews(); }
    }
    private void RefreshTwoDisc()
    {
        if (pendingFineRecord is { } pending && !ReferenceEquals(pending.Plan, twoDisc?.Plan)) pendingFineRecord = null;
        bool busy = calculation is not null;
        copyFineParameters.IsEnabled = !busy && draftMask.Count == 0 && PlanCurrent && twoDisc?.Plan.IsConfirmed == true;
        updateFineEstimate.IsEnabled = !busy && draftMask.Count == 0 && (CoarseReady || twoDisc?.UsesManualParameters == true) && twoDisc?.Fine is null;
        replaceFine.Visibility = twoDisc?.Fine is not null && !PlanCurrent && CoarseReady ? Visibility.Visible : Visibility.Collapsed;
        replaceFine.IsEnabled = !busy && draftMask.Count == 0;
        finePixels.IsEnabled = !busy && twoDisc?.Fine is null;
        DisplayParameters(fineParameterBoxes, PlanCurrent ? twoDisc!.Plan.Generated : null);
        if (twoDisc is null) { workflowDetail.Text = "先完成第一张描线确认或密环分析；预计像素/mm 是第二张拍摄计划输入，尚非实测值。"; fineForecastImage.Source = null; forecastPlanHash = null; return; }
        var plan = twoDisc.Plan; string state = twoDisc.Status switch
        {
            "plan-review" => "第二张拍摄要求尚未通过检查", "awaiting-fine-photo" => "等待第二张实际生成记录与照片",
            "fine-annotation" => "第二张标注 / 独立精调", "fine-fit-review" => "核对第二张候选", _ => "可查看补偿参数"
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
        string source = twoDisc.EvidenceMode switch { "simulation" => "软件模拟", "manual-parameters" => "手动参数", _ => "第一张实测参数" };
        string currentDisc = ReferenceEquals(task, twoDisc.Fine) && task is not null ? " · 当前第二张 G1" : "";
        workflowDetail.Text = $"{source} · {state}{currentDisc}\n"
            + $"G1 ri {plan.Generated.InnerRadiusMm:F6} / ro {plan.Generated.OuterRadiusMm:F6} mm · 候选差 {plan.Forecast.MaximumCandidateDifferenceMm:F3} mm · 预计线宽 {plan.Forecast.MinimumLineWidthPixels:F1} px · 实刻待验证。";
        if (plan.Forecast.Status != "plan-review") workflowDetail.Text += " " + CalibrationGuidance.Describe([CalibrationGuidance.ForCode(plan.Forecast.Status)]);
        if (!PlanCurrent)
        {
            workflowDetail.Text += " 第一张已改变，请使用更新后的粗调参数重新制作第二张。";
        }
    }
}

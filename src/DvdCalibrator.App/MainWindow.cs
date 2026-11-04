using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;
using Microsoft.Win32;

namespace DvdCalibrator.App;

public sealed partial class MainWindow : Window
{
    private PhotoTask? task;
    private SnapshotBundle? pendingSnapshot;
    private readonly Canvas canvas = new() { Name = "SourcePixelCanvas", Background = Brushes.Transparent, Focusable = true };
    private readonly Image photoImage = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Image rectified = new() { Stretch = Stretch.Uniform };
    private readonly Image polar = new() { Stretch = Stretch.Uniform };
    private readonly Image fineForecastImage = new() { Name = "FineTargetForecast", Stretch = Stretch.Uniform };
    private readonly ScrollViewer scroll = new() { Name = "SourceScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox mode = new() { Width = 155, Margin = new(5), ItemsSource = new[] { "实体外缘", "真实中心孔", "反光遮罩", "主径向线描点", "对侧 180° 留出线" }, SelectedIndex = 0 };
    private readonly ComboBox shapeOrder = new() { Width = 145, Margin = new(3), ItemsSource = new[] { "保留轨距形状", "拟合一阶形状", "拟合二阶形状", "拟合三阶形状" }, SelectedIndex = 0 };
    private readonly ComboBox candidateBox = new() { MinWidth = 260, Margin = new(3), Name = "FitCandidateSelector" };
    private readonly TextBlock fitDetail = new() { Margin = new(8), TextWrapping = TextWrapping.Wrap };
    private Button traceFitButton = null!, autoTraceFitButton = null!, traceConfirmButton = null!, profileButton = null!;
    private Button denseButton = null!, denseExportButton = null!;
    private readonly TextBlock denseDetail = new() { Name = "DenseDiagnostic", Margin = new(8), TextWrapping = TextWrapping.Wrap };
    private bool updatingCandidates;
    private TraceFitResult? displayedResult;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(14, 8, 14, 8) };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new(14), FontSize = 13 };
    private readonly TextBlock counts = new() { Margin = new(8), VerticalAlignment = VerticalAlignment.Center };
    private Button fitButton = null!, confirmButton = null!, saveButton = null!, cancelButton = null!;
    private readonly Slider zoomSlider = new() { Name = "SourceZoom", Minimum = .08, Maximum = 5, Value = 1, Width = 130, Margin = new(10), VerticalAlignment = VerticalAlignment.Center };
    private readonly List<PixelPoint> draftMask = [];
    private int selected = -1;
    private bool dragging, dirty;
    private Point? panStart;
    private double panX, panY;
    private CancellationTokenSource? calculation;

    public MainWindow()
    {
        Title = "DVD Calibrator · 两盘任务与照片标注"; Width = 1380; Height = 1000; MinWidth = 1050; MinHeight = 800;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        FontFamily = new("Microsoft YaHei UI"); FontSize = 13;
        Content = BuildWizard();
        mode.SelectionChanged += (_, _) =>
        {
            if (draftMask.Count > 0 && mode.SelectedIndex != 2)
            {
                mode.SelectedIndex = 2;
                SetStatus("遮罩草稿尚未结束，请点击“结束遮罩”，或按 Esc 放弃草稿后再切换模式。");
                return;
            }
            selected = -1; Draw(); RefreshWizard();
        };
        shapeOrder.SelectionChanged += (_, _) => { if (!updatingCandidates && task?.Fit is not null) { task.ClearFit(); dirty = true; Refresh(); Draw(); } };
        candidateBox.SelectionChanged += (_, _) =>
        {
            if (updatingCandidates || task?.Fit is null || candidateBox.SelectedIndex < 0) return;
            task.AttachFit(task.Fit.Select(task.Fit.Result.Candidates[candidateBox.SelectedIndex].Id)); dirty = true; Refresh(); Draw();
        };
        zoomSlider.ValueChanged += (_, _) => { canvas.LayoutTransform = new ScaleTransform(zoomSlider.Value, zoomSlider.Value); Draw(); Refresh(); };
        canvas.MouseLeftButtonDown += CanvasMouseDown; canvas.MouseMove += CanvasMouseMove;
        canvas.MouseLeftButtonUp += (_, _) => { dragging = false; canvas.ReleaseMouseCapture(); };
        canvas.MouseRightButtonDown += (_, e) => { panStart = e.GetPosition(scroll); panX = scroll.HorizontalOffset; panY = scroll.VerticalOffset; canvas.CaptureMouse(); e.Handled = true; };
        canvas.MouseRightButtonUp += (_, _) => { panStart = null; canvas.ReleaseMouseCapture(); };
        canvas.PreviewMouseWheel += (_, e) => { zoomSlider.Value = Math.Clamp(zoomSlider.Value * (e.Delta > 0 ? 1.15 : 1 / 1.15), .08, 5); e.Handled = true; };
        canvas.KeyDown += (_, e) => { if (e.Key == Key.Delete) DeleteSelected(); if (e.Key == Key.Escape) DiscardMaskDraft(); };
        Closing += (_, e) =>
        {
            if (calculation is not null)
            {
                e.Cancel = true;
                SetStatus("计算尚未结束。请先点击“取消计算”，等待取消完成，再保存或关闭窗口。");
            }
            else if (!MayReplace()) e.Cancel = true;
        };
        Loaded += (_, _) =>
        {
            FitView();
            if (CurrentStep == CalibrationStep.Finish) _ = ReviewFinalAsync();
            else if (IsPhotoPage && task?.Session.Rectification is not null && rectified.Source is null) _ = RefreshPreviews();
        };
        SetStatus("按本页步骤准备标定盘。已有进度可点右上角“打开任务”。"); Refresh();
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button(); SetButtonLabel(button, text);
        button.Click += (_, _) => { try { action(); } catch (Exception e) when (Expected(e)) { MessageBox.Show(CalibrationGuidance.Describe(e), "无法完成操作", MessageBoxButton.OK, MessageBoxImage.Information); } };
        return button;
    }
    private static void SetButtonLabel(Button button, string text)
    {
        button.Content = text;
        System.Windows.Automation.AutomationProperties.SetName(button, text);
    }
    private static bool Expected(Exception e) => e is ArgumentException or IOException or UnauthorizedAccessException
        or System.Text.Json.JsonException or InvalidOperationException or NotSupportedException or KeyNotFoundException
        or FormatException or OverflowException or System.Runtime.InteropServices.COMException;
    private void SetStatus(string text) => status.Text = text;
    private bool MayReplace() => !dirty || MessageBox.Show(this, "当前修改尚未保存。放弃这些修改并继续？", "未保存的照片任务",
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    private bool MaskDraftReady()
    {
        if (draftMask.Count == 0) return true;
        SetStatus("遮罩草稿尚未结束。请点击“结束遮罩”，或按 Esc 放弃草稿后再计算、确认或导出。");
        return false;
    }
    private void CancelCalculation()
    {
        if (calculation is null) return;
        calculation.Cancel(); SetStatus("正在取消计算，请等待完成；原有观测和已完成的结果会保留。");
    }

    private void OpenSnapshot()
    {
        if (calculation is not null) return;
        var dialog = new OpenFileDialog { Filter = "第一张生成记录|*.iso.json;generation.json;*.json", Title = "选择第一张 ISO 同目录的 .iso.json" };
        if (dialog.ShowDialog() != true) return;
        var next = SelectGeneration(dialog.FileName, fine: false);
        if (next is null) return;
        if (!MayReplace()) return;
        ResetFirstInput(next);
        SetStatus("第一张生成记录已选择，请选择这张盘的记录面照片。");
    }
    private void ImportPhoto()
    {
        if (calculation is not null || !MaskDraftReady()) return;
        var snapshot = pendingSnapshot;
        if (snapshot is null) { SetStatus("请先打开对应的冻结生成快照。"); return; }
        var dialog = new OpenFileDialog { Filter = "照片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff", Title = "选择第一张盘的记录面照片" };
        if (dialog.ShowDialog(this) != true) return;
        var next = new PhotoTask(snapshot, PhotoCodec.Import(dialog.FileName));
        if (!MayChangeFirstPhoto()) return;
        CurrentStep = CalibrationStep.Prepare;
        Load(next, true, false);
        SetStatus("第一张资料已准备好。点击下一步，开始标外缘和中心孔。");
    }
    private void RemoveFirstPhoto()
    {
        if (calculation is not null || FirstPhoto is null || !MaskDraftReady() || !MayChangeFirstPhoto()) return;
        ResetFirstInput(pendingSnapshot);
        SetStatus("第一张照片已移除，生成记录已保留。请选择照片。");
    }
    private bool MayChangeFirstPhoto() => MayChangePhoto(FirstPhoto,
        "更换或移除第一张照片会清除第一张的标注和测量，以及依赖它的第二张进度。继续？",
        twoDisc?.Fine is not null);
    private bool MayChangePhoto(PhotoTask? current, string impact, bool hasDependentPhoto = false)
    {
        if (!hasDependentPhoto && (current is null || (current.Session.OuterBoundary.IsEmpty && current.Session.HoleBoundary.IsEmpty
            && current.Session.Masks.IsEmpty && current.Session.Traces.IsEmpty))) return true;
        return MessageBox.Show(this, impact, "更新照片", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
    private void ResetFirstInput(SnapshotBundle? snapshot)
    {
        task = null; twoDisc = null; pendingSnapshot = snapshot; dirty = false; draftMask.Clear(); selected = -1;
        CurrentStep = CalibrationStep.Prepare; photoImage.Source = null; canvas.Width = canvas.Height = 0;
        ClearPreviews(); Draw(); Refresh();
    }
    public void LoadDemo()
    {
        if (calculation is not null || !MayReplace()) return;
        Load(SyntheticPhoto.Create(), true); SetStatus("合成演示已载入：轮廓为已知投影点，点击“计算联合矫正”。此示例不是实盘照片。");
    }
    public void LoadTraceDemo()
    {
        if (calculation is not null || !MayReplace()) return;
        Load(SyntheticPhoto.CreateTraceDemo(), true);
        SetStatus("拟合演示：已知投影轮廓和两条径向线均由完整模型生成，点击“拟合描线”查看候选。不是实盘验证。");
    }
    public void LoadDenseDemo()
    {
        if (calculation is not null || !MayReplace()) return;
        Load(SyntheticPhoto.CreateDenseDemo(), true);
        SetStatus("密环演示含半盘变频、低对比缺失带与局部描线。点击“分析密环 / 混合”，检查片段、留出和下一张预测；这是软件合成样本。");
    }
    public void LoadPhotoTask(PhotoTask next) => Load(next, false);
    private void Load(PhotoTask? next, bool changed, bool selectStep = true)
    {
        if (twoDisc is not null && !ReferenceEquals(next, twoDisc.Coarse) && !ReferenceEquals(next, twoDisc.Fine)) twoDisc = null;
        if (twoDisc is null) finePixels.Text = "8";
        task = next; pendingSnapshot = twoDisc is null ? next?.Snapshot : twoDisc.Coarse?.Snapshot;
        dirty = changed; selected = -1; draftMask.Clear();
        updatingCandidates = true; shapeOrder.SelectedIndex = next?.Fit?.Result.Settings.ShapeOrder ?? 0; updatingCandidates = false;
        photoImage.Source = next is null ? null : PhotoCodec.Bitmap(next.Photo.Image);
        canvas.Width = photoImage.Width = next?.Photo.Image.Width ?? 0;
        canvas.Height = photoImage.Height = next?.Photo.Image.Height ?? 0;
        if (selectStep) SelectResumeStep();
        if (CurrentStep == CalibrationStep.Plan) EnsureFinePreparation();
        ClearPreviews(); Refresh(); ((FrameworkElement)Content).UpdateLayout(); FitView(); Draw();
        if (IsPhotoPage && !IsFitPage)
            SetStatus("沿实体外缘和真实孔缘各点击 8–16 点，覆盖整圈。滚轮缩放、右键拖动画面；拖动标记可修正。");
        else SetStatus("当前步骤：" + StepNames[(int)CurrentStep - 1] + "。");
        if (selectStep && CurrentStep == CalibrationStep.Finish && IsLoaded) _ = ReviewFinalAsync();
        if (IsPhotoPage && StepReady(CurrentStep) && next?.Session.Rectification is not null && IsLoaded) _ = RefreshPreviews();
    }
    private void SaveTask(PhotoTask? photo)
    {
        if (photo is null || calculation is not null) return;
        if (draftMask.Count > 0) { SetStatus("请先结束遮罩，或按 Esc 放弃当前遮罩草稿。"); return; }
        var dialog = new SaveFileDialog { Filter = "照片任务包|*.dvdphoto", DefaultExt = ".dvdphoto", OverwritePrompt = false, FileName = "photo-task-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        if (dialog.ShowDialog() != true) return;
        photo.SaveNew(dialog.FileName); dirty = twoDisc is not null; Refresh(); SetStatus("单盘任务包已保存，包含原图、生成记录与标注。");
    }
    private void FitView()
    {
        if (task is null) return;
        zoomSlider.Value = Math.Clamp(Math.Min(Math.Max(200, scroll.ActualWidth - 30) / task.Photo.Image.Width,
            Math.Max(200, scroll.ActualHeight - 30) / task.Photo.Image.Height), .08, 5);
        scroll.ScrollToHorizontalOffset(0); scroll.ScrollToVerticalOffset(0); Draw();
    }
    private ImmutableArray<PixelPoint> CurrentPoints() => task is null ? [] : mode.SelectedIndex switch
    { 0 => task.Session.OuterBoundary, 1 => task.Session.HoleBoundary, 2 => draftMask.ToImmutableArray(),
        _ => task.Session.Traces.FirstOrDefault(t => t.Id == (mode.SelectedIndex == 4 ? "validation-180" : "line-1"))?.Points ?? [] };
    private void SetPoints(ImmutableArray<PixelPoint> points)
    {
        if (task is null) return;
        if (points.Length > (mode.SelectedIndex >= 3 ? 4000 : 256)) { SetStatus("当前标注点数已达到限制。"); return; }
        var s = task.Session;
        switch (mode.SelectedIndex)
        {
            case 0: task.Session = s.WithBoundaries(points, s.HoleBoundary); ClearPreviews(); break;
            case 1: task.Session = s.WithBoundaries(s.OuterBoundary, points); ClearPreviews(); break;
            case 2: draftMask.Clear(); draftMask.AddRange(points); break;
            default:
                string id = mode.SelectedIndex == 4 ? "validation-180" : "line-1";
                var others = s.Traces.Where(t => t.Id != id).ToImmutableArray();
                task.Session = s.WithTraces(points.IsEmpty ? others : others.Add(new(id, points))); break;
        }
        dirty = true; Refresh(); Draw();
    }
    private void CanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (task is null || calculation is not null) return;
        canvas.Focus(); var p = e.GetPosition(canvas);
        if (p.X < 0 || p.Y < 0 || p.X > task.Photo.Image.Width - 1 || p.Y > task.Photo.Image.Height - 1) return;
        if (mode.SelectedIndex >= 3 && !task.Session.IsConfirmed) { SetStatus("请先计算并确认照片矫正，再描同一条径向线。"); return; }
        var points = CurrentPoints(); selected = -1;
        double radius = 10 / zoomSlider.Value, nearest = radius * radius;
        for (int i = 0; i < points.Length; i++)
        {
            double dx = points[i].X - p.X, dy = points[i].Y - p.Y, distance = dx * dx + dy * dy;
            if (distance < nearest) { selected = i; nearest = distance; }
        }
        if (selected < 0) { SetPoints(points.Add(new(p.X, p.Y))); selected = points.Length; }
        dragging = true; canvas.CaptureMouse(); Draw(); e.Handled = true;
    }
    private void CanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (panStart is Point start && e.RightButton == MouseButtonState.Pressed)
        { var p = e.GetPosition(scroll); scroll.ScrollToHorizontalOffset(panX - p.X + start.X); scroll.ScrollToVerticalOffset(panY - p.Y + start.Y); return; }
        if (!dragging || task is null || calculation is not null || e.LeftButton != MouseButtonState.Pressed) return;
        var current = CurrentPoints(); if (selected < 0 || selected >= current.Length) return;
        var point = e.GetPosition(canvas);
        SetPoints(current.SetItem(selected, new(Math.Clamp(point.X, 0, task.Photo.Image.Width - 1), Math.Clamp(point.Y, 0, task.Photo.Image.Height - 1))));
    }
    private void DeleteSelected()
    {
        if (calculation is not null) return;
        var p = CurrentPoints(); if (selected >= 0 && selected < p.Length) { int at = selected; selected = -1; SetPoints(p.RemoveAt(at)); }
    }
    private void UndoPoint()
    {
        if (calculation is not null) return;
        var p = CurrentPoints(); if (p.Length > 0) { SetPoints(p.RemoveAt(p.Length - 1)); selected = -1; }
    }
    private void ClearCurrent()
    {
        if (task is null || calculation is not null) return;
        if (mode.SelectedIndex == 2) { task.Session = task.Session.WithMasks([]); draftMask.Clear(); dirty = true; ClearPreviews(); if (task.Session.Rectification is not null) _ = RefreshPreviews(); }
        else SetPoints([]);
        selected = -1; Refresh(); Draw();
    }
    private void FinishMask()
    {
        if (task is null || calculation is not null || mode.SelectedIndex != 2) return;
        if (draftMask.Count < 3) { SetStatus("遮罩至少需要 3 个顶点，沿反光区域点击后再结束。"); return; }
        var next = task.Session.WithMasks(task.Session.Masks.Add(new(draftMask.ToImmutableArray())));
        next.Validate(); task.Session = next; draftMask.Clear(); selected = -1; dirty = true; Refresh(); Draw();
        ClearPreviews(); RefreshWizard();
        if (task.Session.Rectification is not null) _ = RefreshPreviews();
    }
    private void Draw()
    {
        while (canvas.Children.Count > 1) canvas.Children.RemoveAt(canvas.Children.Count - 1);
        if (task is null) return;
        double scale = zoomSlider.Value;
        void Line(IEnumerable<PixelPoint> points, Brush brush, bool closed, bool fill = false)
        {
            var path = new Polyline { Stroke = brush, StrokeThickness = 1.5 / scale, IsHitTestVisible = false };
            var list = points.ToList(); foreach (var p in list) path.Points.Add(new(p.X, p.Y));
            if (closed && list.Count > 0) path.Points.Add(new(list[0].X, list[0].Y));
            if (fill) path.Fill = new SolidColorBrush(Color.FromArgb(65, 239, 68, 68));
            canvas.Children.Add(path);
        }
        var s = task.Session;
        foreach (var mask in s.Masks) Line(mask.Vertices, Brushes.IndianRed, true, true);
        Line(s.OuterBoundary, Brushes.DodgerBlue, true); Line(s.HoleBoundary, Brushes.LimeGreen, true);
        foreach (var trace in s.Traces) Line(trace.Points, Brushes.Gold, false);
        if (task.Dense is { } dense)
        {
            var transform = s.GetTransform(); var candidate = dense.Candidates.FirstOrDefault(c => c.Supported);
            var shift = dense.Wfr?.Centers.FirstOrDefault(c => c.CandidateId == candidate?.Id);
            if (candidate is not null)
                foreach (double offset in new[] { 0d, Math.PI })
                    Line((dense.Wfr is null ? candidate.Curve : WfrAnalyzer.DisplayCurve(candidate)).Select(p => transform.DiscToSource(new(p.RadiusMm * Math.Cos(p.AngleRadians + offset) - (shift?.XMm ?? 0), p.RadiusMm * Math.Sin(p.AngleRadians + offset) - (shift?.YMm ?? 0)))), Brushes.MediumPurple, false);
            foreach (var fragment in dense.Wfr is null ? dense.Observations.Fragments : [])
                Line(fragment.Points.Select(p => transform.DiscToSource(new(p.RadiusMm * Math.Cos(p.PhaseRadians), p.RadiusMm * Math.Sin(p.PhaseRadians)))), Brushes.DeepSkyBlue, false);
        }
        if (task.Fit is { } review)
        {
            var transform = s.GetTransform();
            foreach (var trace in s.Traces)
            {
                double offset = trace.Id == "validation-180" ? Math.PI : 0;
                Line(review.Selected.Curve.Select(p => transform.DiscToSource(new(p.RadiusMm * Math.Cos(p.AngleRadians + offset), p.RadiusMm * Math.Sin(p.AngleRadians + offset)))), Brushes.DarkOrange, false);
            }
        }
        if (draftMask.Count > 0) Line(draftMask, Brushes.IndianRed, false);
        if (s.Rectification is not null)
        {
            var transform = s.GetTransform();
            foreach (double radius in new[] { 60d, 7.5 })
                Line(Enumerable.Range(0, 129).Select(i => transform.DiscToSource(new(radius * Math.Cos(i * Math.PI / 64), radius * Math.Sin(i * Math.PI / 64)))), Brushes.Orange, false);
        }
        void Dots(ImmutableArray<PixelPoint> points, Brush color, bool active, Func<int, Brush>? colorAt = null)
        {
            for (int i = 0; i < points.Length; i++)
            {
                double size = 8 / scale; var dot = new Ellipse { Width = size, Height = size, Fill = active && i == selected ? Brushes.White : colorAt?.Invoke(i) ?? color,
                    Stroke = Brushes.Black, StrokeThickness = .7 / scale, IsHitTestVisible = false };
                Canvas.SetLeft(dot, points[i].X - size / 2); Canvas.SetTop(dot, points[i].Y - size / 2); canvas.Children.Add(dot);
            }
        }
        Dots(s.OuterBoundary, Brushes.DodgerBlue, mode.SelectedIndex == 0); Dots(s.HoleBoundary, Brushes.LimeGreen, mode.SelectedIndex == 1);
        Dots(draftMask.ToImmutableArray(), Brushes.IndianRed, mode.SelectedIndex == 2);
        foreach (var trace in s.Traces)
        {
            bool active = trace.Id == (mode.SelectedIndex == 4 ? "validation-180" : "line-1");
            Brush PointColor(int i)
            {
                var residual = task.Fit?.Selected.Residuals.FirstOrDefault(r => r.TraceId == trace.Id && r.SourceIndex == i);
                return residual is null ? (trace.Id == "validation-180" ? Brushes.SeaGreen : Brushes.Gold)
                    : Math.Abs(residual.ErrorMm) > task.Fit!.Result.Settings.MaximumRmsMm ? Brushes.Crimson
                    : residual.IsHoldout ? Brushes.SeaGreen : Brushes.Gold;
            }
            Dots(trace.Points, Brushes.Gold, mode.SelectedIndex >= 3 && active, PointColor);
        }
    }
    private async Task Compute()
    {
        if (task is null || calculation is not null || !MaskDraftReady()) return;
        var input = task; var session = input.Session; calculation = new(); var token = calculation.Token;
        Refresh(); SetStatus("正在联合拟合实体外缘和中心孔，可取消……");
        try
        {
            var result = await Task.Run(() =>
            {
                var next = session.Rectify(token);
                return (Session: next, Images: RenderPreviewImages(input, next, token));
            }, token);
            token.ThrowIfCancellationRequested();
            input.Session = result.Session; ApplyPreviews(result.Images); dirty = true; Draw();
            SetStatus("矫正计算完成。请核对正视预览及橙色轮廓；满意后点击“确认矫正”。");
        }
        catch (OperationCanceledException) { SetStatus("矫正计算已取消，原图、标注和原有结果保留。"); }
        catch (Exception e) when (Expected(e)) { SetStatus(CalibrationGuidance.Describe(e)); }
        finally { calculation.Dispose(); calculation = null; Refresh(); }
    }
    private async Task RefreshPreviews()
    {
        if (task?.Session.Rectification is null || calculation is not null) return;
        calculation = new(); Refresh();
        try { await RenderPreviews(); }
        catch (OperationCanceledException) { SetStatus("预览已取消。"); }
        catch (Exception e) when (Expected(e)) { ClearPreviews(); SetStatus(CalibrationGuidance.Describe(e)); }
        finally { calculation.Dispose(); calculation = null; Refresh(); }
    }
    private async Task RenderPreviews()
    {
        if (task is null) return;
        var input = task; var session = input.Session;
        var token = calculation?.Token ?? default;
        var images = await Task.Run(() => RenderPreviewImages(input, session, token), token);
        token.ThrowIfCancellationRequested(); ApplyPreviews(images);
    }
    private static (RgbImage Rectified, RgbImage Polar) RenderPreviewImages(PhotoTask input, PhotoSession session, CancellationToken token)
    {
        var source = input.Photo.Image; var geometry = input.Snapshot.Geometry;
        return (PhotoRaster.Render(source, session, 640, 640, false, token: token),
            PhotoRaster.Render(source, session, 960, 400, true, geometry.InnerRadiusMm, geometry.OuterRadiusMm, token));
    }
    private void ApplyPreviews((RgbImage Rectified, RgbImage Polar) images)
    { rectified.Source = PhotoCodec.Bitmap(images.Rectified); polar.Source = PhotoCodec.Bitmap(images.Polar); }
    private void Confirm()
    {
        if (task is null || calculation is not null || !MaskDraftReady()) return;
        task.Session = task.Session.Confirm(); dirty = true; Refresh(); SetStatus("照片矫正已确认，可切换“径向线描点”，沿同一条边界从内向外取点。");
    }
    public async Task ComputeTraceFit(bool automatic = false)
    {
        if (task is null || calculation is not null || !MaskDraftReady()) return;
        if (automatic && (CurrentStep != CalibrationStep.FineFit || !StepReady(CurrentStep))) return;
        var input = task; var settings = TraceFitSettings.Default with { ShapeOrder = shapeOrder.SelectedIndex };
        if (automatic && !input.Session.Traces.IsEmpty && MessageBox.Show(this, "重新自动描线会替换当前描点。继续？", "自动描线",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        calculation = new(); var token = calculation.Token; Refresh();
        SetStatus(automatic ? "正在识别对侧十字臂并拟合曲线……" : "正在拟合曲线并检查留出点……");
        try
        {
            var (observations, review) = await Task.Run(() => automatic
                ? input.FitCrossAutomatically(settings, token) : (input.Session, input.FitTrace(settings, token)), token);
            token.ThrowIfCancellationRequested();
            if (automatic) { input.Session = observations; selected = -1; mode.SelectedIndex = 3; }
            input.AttachFit(review); dirty = true; Draw();
            string message = review.Selected.EligibleForReview
                ? "拟合完成。橙线为完整模型预测；请检查点间走向与留出区域，确认没有漏圈或换臂后再确认候选。"
                : CalibrationGuidance.Describe(CalibrationGuidance.ForTrace(review));
            if (automatic) message += " 请自行删除偏差较大的点，修改后点“拟合描线”。";
            SetStatus(message);
        }
        catch (OperationCanceledException) { SetStatus("描线拟合已取消，照片、观测和原有候选保留。"); }
        catch (Exception e) when (Expected(e)) { SetStatus(CalibrationGuidance.Describe(e)); }
        finally { calculation.Dispose(); calculation = null; Refresh(); }
    }
    private void ConfirmTraceFit()
    {
        if (task?.Fit is null || calculation is not null || !MaskDraftReady()) return;
        task.AttachFit(task.Fit.Confirm(task.Snapshot.Geometry, task.Snapshot.Hash, task.Session));
        dirty = true; Refresh(); SetStatus("当前候选曲线已确认，可以导出完整补偿配置与诊断报告；实刻验证状态仍为 pending。");
    }
    public async Task ComputeDense()
    {
        if (task is null || calculation is not null || !MaskDraftReady()) return;
        var input = task; calculation = new(); var token = calculation.Token; Refresh(); SetStatus("正在从原图提取多角度变频片段并拟合；缺失区保留为空，奇数角度及径向末段用于留出检查，可取消……");
        try
        {
            var result = await Task.Run(() => input.AnalyzeDense(token: token), token);
            token.ThrowIfCancellationRequested();
            input.AttachDense(result); dirty = true; Draw();
            var issues = CalibrationGuidance.ForDense(result);
            SetStatus(issues.IsEmpty ? "密环分析完成。紫线为完整模型，蓝线为可观测片段；请导出诊断核对所有候选和下一张预测。" : CalibrationGuidance.Describe(issues));
        }
        catch (OperationCanceledException) { SetStatus("密环分析已取消，照片、观测和原有分析保留。"); }
        catch (Exception e) when (Expected(e)) { SetStatus(CalibrationGuidance.Describe(e)); }
        finally { calculation.Dispose(); calculation = null; Refresh(); }
    }
    private void ExportDense()
    {
        if (task?.Dense is null || calculation is not null || !MaskDraftReady()) return;
        var dialog = new OpenFolderDialog { Title = "选择父目录，将新建密环诊断目录" };
        if (dialog.ShowDialog() != true) return;
        string path = System.IO.Path.Combine(dialog.FolderName, "dense-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        DenseExport.Write(path, task.Dense, task.AnalyticSnapshot(), task.Session, task.Photo.Image);
        SetStatus("已导出完整候选、局部频率 CSV、重建图、下一张预测和中文诊断报告：" + path);
    }
    private void ExportProfile()
    {
        if (task?.Fit is null || calculation is not null || !MaskDraftReady()) return;
        var dialog = new OpenFolderDialog { Title = "选择父目录，将在其中新建标定导出目录" };
        if (dialog.ShowDialog() != true) return;
        string path = System.IO.Path.Combine(dialog.FolderName, "calibration-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        CalibrationExport.Write(path, task.Fit, task.Session);
        SetStatus("已导出：" + path + (CalibrationExport.ProjectCompatible(task.Fit.Selected.Parameters) ? "；目标工程已通过实际 JSON 加载接口回读。" : "；目标工程无法表达当前位长/方向，报告中保留完整应用条件。"));
    }
    private void RefreshFit()
    {
        var review = task?.Fit;
        updatingCandidates = true;
        if (!ReferenceEquals(displayedResult, review?.Result))
        {
            displayedResult = review?.Result;
            candidateBox.ItemsSource = review?.Result.Candidates.Select((c, i) => $"候选 {i + 1}  训练 {c.TrainingRmsMm:F3} / 留出 {c.HoldoutRmsMm:F3} mm").ToArray();
        }
        candidateBox.SelectedIndex = review is null ? -1 : review.Result.Candidates.IndexOf(review.Selected);
        updatingCandidates = false;
        candidateBox.IsEnabled = review is not null && calculation is null; shapeOrder.IsEnabled = calculation is null;
        traceFitButton.IsEnabled = task?.Session.IsConfirmed == true && calculation is null && draftMask.Count == 0;
        traceConfirmButton.IsEnabled = review is not null && review.Selected.EligibleForReview && !review.IsConfirmed
            && review.Result.QualityStatus != "candidate-ambiguous" && calculation is null && draftMask.Count == 0;
        profileButton.IsEnabled = review?.IsConfirmed == true && calculation is null && draftMask.Count == 0;
        string State(string code) => code switch
        { "fit-review" => "待曲线核对", "model-mismatch" => "观测与模型尚不一致", "candidate-ambiguous" => "候选仍有歧义", _ => "搜索范围受限" };
        fitDetail.Text = review is null ? "主线至少 24 点、覆盖 60% 环带；末段 25% 留出。\n可补充明确对侧 180° 的独立检查线。"
            : $"{State(review.Result.QualityStatus)}  ·  {(review.IsConfirmed ? "候选已确认" : "请核对点间曲线")}\n训练 {review.Selected.TrainingRmsMm:F4} / 留出 {review.Selected.HoldoutRmsMm:F4} mm  ·  最大 {review.Selected.MaxErrorMm:F4} mm\nri {review.Selected.Parameters.InnerRadiusMm:F6} / ro {review.Selected.Parameters.OuterRadiusMm:F6} mm\nc1 {review.Selected.Parameters.PitchLinear:G5}  c2 {review.Selected.Parameters.PitchQuadratic:G5}  c3 {review.Selected.Parameters.PitchCubic:G5}\n搜索 ±{review.Result.SearchedRadiusRangeMm:G3} mm  ·  形状阶数 {review.Selected.ShapeOrder}  ·  参数相关 {review.Selected.ParameterCorrelation:F4}"
                + (CalibrationGuidance.ForTrace(review) is { Length: > 0 } issues ? "\n" + CalibrationGuidance.Describe(issues) : "\n固定生成位长的等效补偿；实刻验证 pending。");
    }
    private void ClearPreviews() { rectified.Source = null; polar.Source = null; }
    private void Refresh()
    {
        bool busy = calculation is not null;
        fitButton.IsEnabled = task is not null && !busy && draftMask.Count == 0;
        saveButton.IsEnabled = (task is not null || twoDisc is not null && PlanCurrent) && !busy && draftMask.Count == 0;
        confirmButton.IsEnabled = task?.Session.Rectification is not null && rectified.Source is not null && !busy && !task.Session.IsConfirmed && draftMask.Count == 0;
        cancelButton.IsEnabled = busy;
        mode.IsEnabled = !busy;
        RefreshFit();
        RefreshTwoDisc();
        denseButton.IsEnabled = task?.Session.IsConfirmed == true && !busy && draftMask.Count == 0;
        denseExportButton.IsEnabled = task?.Dense is not null && !busy && draftMask.Count == 0;
        var dense = task?.Dense; var best = dense?.Candidates.FirstOrDefault(c => c.Supported);
        denseDetail.Text = dense is null ? "密环入口：已知解析半盘靶，可联合局部主边界描线。" :
            $"{DenseState(dense.QualityStatus)} · 片段 {dense.Observations.Fragments.Length} · 有效径向 {dense.Observations.UsableRadialFraction:P0}\n"
            + (best is null ? CalibrationGuidance.Describe(CalibrationGuidance.ForDense(dense)) : $"ri {best.Parameters.InnerRadiusMm:F6} / ro {best.Parameters.OuterRadiusMm:F6} mm\n{(dense.Wfr is null ? "图像训练/留出" : "WFR 法向误差训练/留出 (mm)")} {best.TrainingImageError:F3} / {best.HoldoutImageError:F3} · 相位留出 {best.HoldoutPhaseRmsRadians:F3} rad\n{DenseState(dense.NextDisc?.Status ?? "photo-insufficient")}；实刻待验证。");
        if (task is null) { counts.Text = "未导入照片"; detail.Text = "先准备生成记录和记录面原图。"; RefreshWizard(); return; }
        var s = task.Session;
        counts.Text = $"外缘 {s.OuterBoundary.Length} 点  ·  孔缘 {s.HoleBoundary.Length} 点  ·  遮罩 {s.Masks.Length} 处  ·  描线 {s.Traces.Sum(t => t.Points.Length)} 点"
            + (draftMask.Count > 0 ? $"  ·  遮罩草稿 {draftMask.Count} 点（未应用）" : "");
        detail.Text = $"原图 {s.Photo.OriginalWidth} × {s.Photo.OriginalHeight}  →  定向后 {s.Photo.OrientedWidth} × {s.Photo.OrientedHeight}\nEXIF 方向 {s.Photo.AppliedExifOrientation}  ·  缩放 {zoomSlider.Value:P0}\n观察面约定：记录面\n快照 {s.SnapshotId}\n"
            + (s.Rectification is null ? "待计算联合矫正" : $"外缘 RMS {s.Rectification.OuterRmsPixels:F3} px  ·  孔缘 RMS {s.Rectification.HoleRmsPixels:F3} px\n" + (s.IsConfirmed ? "矫正已确认" : "待核对确认"))
            + "\n\n遮罩在原图上绘制多边形后点击“结束遮罩”。\n极坐标左→右：0°→360°（逆时针）；上→下：生成内半径→外半径。\n照片矫正不会消除镜头畸变。";
        RefreshWizard();
    }
    private static string DenseState(string status) => status switch
    {
        "dense-review" => "密环候选待核对", "photo-insufficient" => "照片证据不足", "model-mismatch" => "观测与模型不一致",
        "candidate-ambiguous" => "候选仍有歧义", "next-disc-review" => "下一张预测待核对", "next-disc-undersampled" => "下一张预测分辨率不足", _ => status
    };
    private void ExportPreviews()
    {
        if (!MaskDraftReady()) return;
        if (task?.Session.Rectification is null || calculation is not null) { SetStatus("请先计算联合矫正。"); return; }
        var dialog = new SaveFileDialog { Filter = "正视预览 PNG|*.png", FileName = "rectified.png", OverwritePrompt = false };
        if (dialog.ShowDialog() != true) return;
        string path = dialog.FileName, polarPath = System.IO.Path.ChangeExtension(path, null) + "-polar.png";
        if (File.Exists(path) || File.Exists(polarPath)) throw new IOException("预览文件已存在，请使用新文件名。");
        var s = task.Session; var g = task.Snapshot.Geometry;
        using (var stream = new FileStream(path, FileMode.CreateNew))
            stream.Write(PhotoCodec.Encode(PhotoRaster.Render(task.Photo.Image, s, 960, 960, false)));
        using (var stream = new FileStream(polarPath, FileMode.CreateNew))
            stream.Write(PhotoCodec.Encode(PhotoRaster.Render(task.Photo.Image, s, 1440, 600, true, g.InnerRadiusMm, g.OuterRadiusMm)));
        SetStatus("正视预览与极坐标图已导出，原始照片保持原样。");
    }
}

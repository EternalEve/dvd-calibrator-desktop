using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DvdCalibrator.App;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;

internal static class WizardChecks
{
    public static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>(); var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var item))
        {
            if (!seen.Add(item)) continue; yield return item;
            if (item is Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) pending.Push(VisualTreeHelper.GetChild(item, i));
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) pending.Push(child);
        }
    }
    public static Button Find(MainWindow window, string label) => Nodes(window).OfType<Button>().Single(b => Equals(b.Content, label));
    public static void Click(MainWindow window, string label) => Find(window, label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    public static void ReviewFinal(MainWindow window) => DesktopStateChecks.WithDispatcher(() => DesktopStateChecks.Pump(
        (Task)typeof(MainWindow).GetMethod("ReviewFinalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!));
    public static IEnumerable<TextBox> ParameterBoxes(MainWindow window, string name) =>
        Nodes(window).OfType<Grid>().Single(g => g.Name == name).Children.OfType<TextBox>();
    public static void Layout(MainWindow window, int width = 1380, int height = 1000)
    { var content = (FrameworkElement)window.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout(); }
    public static void Capture(MainWindow window, string path, int width = 1380, int height = 1000)
    {
        if (window.CurrentStep is CalibrationStep.CoarseFit or CalibrationStep.FineFit)
            DesktopStateChecks.WithDispatcher(() => DesktopStateChecks.Pump((Task)typeof(MainWindow).GetMethod("RefreshPreviews", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!));
        Layout(window, width, height); var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    public static void Register(Action<string, Action> test, string output)
    {
        test("wizard opens all eight pages of a blank task while gating actions", () =>
        {
            var window = new MainWindow(); Layout(window);
            Check(window.CurrentStep == CalibrationStep.Prepare, "blank task opened the wrong page");
            Check(Nodes(window).OfType<Button>().Count(b => b.Name.StartsWith("WizardStep", StringComparison.Ordinal) && b.IsEnabled) == 8,
                "blank task disabled a sidebar step");
            foreach (var step in Enum.GetValues<CalibrationStep>())
                Check(window.GoToStep(step) && window.CurrentStep == step, "locked blank page: " + step);
            Check(!Find(window, "复制补偿参数").IsEnabled && ParameterBoxes(window, "FinalParameters").All(t => t.Text.Length == 0),
                "blank task showed usable parameters");
            Check(Nodes(window).OfType<TextBlock>().Any(t => t.Text.Contains("完成后得到", StringComparison.Ordinal)), "step outcome missing");
            Check(Nodes(window).OfType<Expander>().Any(e => Equals(e.Header, "本步教程与检查要点")), "tutorial missing");
            Check(!Nodes(window).OfType<TextBlock>().Any(t => t.Text is "先认识两个软件" or "继续任务，或先体验示例")
                && !Nodes(window).OfType<Button>().Any(b => b.Content is "照片标注示例" or "查看第二张的生成、刻录和拍照教程"
                    or "从零开始：制作第一张盘" or "查看怎样把补偿参数用回原项目" or "导出两盘最终配置" or "导出单盘精调配置"), "removed sections still appear");
            Check(window.GoToStep(CalibrationStep.Prepare), "cannot return to first page");
            Capture(window, Path.Combine(output, "wizard-01-prepare.png"));
            Capture(window, Path.Combine(output, "wizard-01-small.png"), 1050, 800);
        });
        test("wizard back navigation keeps unconfirmed original observations and blocks fitting", () =>
        {
            var task = SyntheticPhoto.Create(); string before = RecordJson.Hash(task.Session); var window = new MainWindow(); Layout(window); window.LoadPhotoTask(task);
            Check(window.CurrentStep == CalibrationStep.CoarseRectify && window.GoToStep(CalibrationStep.CoarseFit), "fitting page is locked");
            Check(Find(window, "前往校正第一张照片").Visibility == Visibility.Visible, "unconfirmed photo did not prompt for rectification");
            Check(window.GoToStep(CalibrationStep.Prepare) && window.GoToStep(CalibrationStep.CoarseRectify), "cannot return to annotations");
            Check(RecordJson.Hash(task.Session) == before && !task.Session.IsConfirmed, "navigation changed observations/confirmation");
            Capture(window, Path.Combine(output, "wizard-02-rectify.png"));
        });
        test("wizard prepares parameters automatically without exporting files or requiring plan buttons", () =>
        {
            var photo = PhotoTask.Load(Path.Combine(output, "trace-fit.dvdphoto")); var window = new MainWindow(); Layout(window); window.LoadPhotoTask(photo);
            var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Order().ToArray();
            Check(window.CurrentStep == CalibrationStep.CoarseFit && window.GoToStep(CalibrationStep.Plan), "confirmed coarse result cannot reach plan");
            var workflow = (TwoDiscTask)typeof(MainWindow).GetField("twoDisc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(workflow.Plan.IsConfirmed && workflow.Plan.Generated == photo.Fit!.Selected.Parameters, "automatic preparation lost coarse parameters");
            var boxes = ParameterBoxes(window, "CoarseParameters").Where(b => b.IsReadOnly && System.Windows.Automation.AutomationProperties.GetName(b) == "内半径 (mm)").ToArray();
            Check(boxes.Length == 1 && double.Parse(boxes[0].Text, System.Globalization.CultureInfo.InvariantCulture) == workflow.Plan.Generated.InnerRadiusMm, "displayed radius cannot roundtrip without precision loss");
            Check(Find(window, "复制参数").IsEnabled && !Nodes(window).OfType<Button>().Any(b => b.Content is string s && new[] { "创建第二张计划", "确认第二张计划", "导出第二张靶" }.Contains(s)), "normal page retained obsolete plan/export actions");
            Check(Directory.GetFiles(output, "*", SearchOption.AllDirectories).Order().SequenceEqual(files), "entering preparation wrote files");
            string planHash = workflow.Plan.ContentSha256;
            Check(window.GoToStep(CalibrationStep.CoarseFit) && window.GoToStep(CalibrationStep.Plan) && workflow.Plan.ContentSha256 == planHash, "revisiting preparation changed a bound plan");
            Capture(window, Path.Combine(output, "wizard-04-parameters.png"));
            Capture(window, Path.Combine(output, "wizard-04-parameters-small.png"), 1050, 800);
            string saved = Path.Combine(output, "auto-prepared.dvdcal"); workflow.SaveNew(saved);
            var resumed = new MainWindow(); Layout(resumed); resumed.LoadTwoDiscTask(TwoDiscTask.Load(saved));
            Check(resumed.CurrentStep == CalibrationStep.Plan && resumed.GoToStep(CalibrationStep.FineImport), "saved automatic preparation did not resume");
            Check(window.GoToStep(CalibrationStep.FineImport), "automatic preparation blocked fine import");
            Capture(window, Path.Combine(output, "wizard-05-await-photo.png"));
            Check(window.GoToStep(CalibrationStep.FineRectify) && Find(window, "前往导入第二张照片").Visibility == Visibility.Visible,
                "missing fine photo did not prompt for import");
        });
        test("wizard restores draft plans and preserves resolution checks without manual confirmation", () =>
        {
            var photo = PhotoTask.Load(Path.Combine(output, "trace-fit.dvdphoto"));
            var draft = TwoDiscTask.Create(photo); var window = new MainWindow(); Layout(window); window.LoadTwoDiscTask(draft);
            Check(draft.Plan.IsConfirmed && Find(window, "复制参数").IsEnabled, "legacy draft required a removed confirmation button");
            var pixels = (TextBox)typeof(MainWindow).GetField("finePixels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            pixels.Text = "1"; Click(window, "更新拍摄估计");
            Check(!Find(window, "复制参数").IsEnabled && window.GoToStep(CalibrationStep.FineImport) && !Find(window, "导入第二张照片").IsEnabled,
                "insufficient resolution bypassed import checks");
            Check(window.GoToStep(CalibrationStep.Plan), "cannot correct the estimate");
            pixels.Text = "8"; Click(window, "更新拍摄估计");
            Check(Find(window, "复制参数").IsEnabled && window.GoToStep(CalibrationStep.FineImport), "corrected photo estimate did not unlock import");
        });
        test("wizard shows exact final parameters without writing files and preserves both discs", () =>
        {
            var task = TwoDiscTask.Load(Path.Combine(output, "two-disc.dvdcal")); var window = new MainWindow(); Layout(window); window.LoadTwoDiscTask(task);
            string first = RecordJson.Hash(task.Coarse!.Session), fine = RecordJson.Hash(task.Fine!.Session), plan = task.Plan.ContentSha256;
            Check(window.CurrentStep == CalibrationStep.Finish && !Find(window, "复制补偿参数").IsEnabled, "restored result bypassed final check");
            var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Order().ToArray();
            ReviewFinal(window);
            Check(Find(window, "复制补偿参数").IsEnabled, "valid final check did not enable copying");
            var boxes = ParameterBoxes(window, "FinalParameters").ToArray();
            var parameters = task.Fine.Fit!.Selected.Parameters;
            Check(boxes.Length == 8 && boxes.All(b => b.IsReadOnly)
                && double.Parse(boxes[0].Text, System.Globalization.CultureInfo.InvariantCulture) == parameters.InnerRadiusMm
                && double.Parse(boxes[1].Text, System.Globalization.CultureInfo.InvariantCulture) == parameters.OuterRadiusMm,
                "displayed final parameters lost precision");
            Check(Directory.GetFiles(output, "*", SearchOption.AllDirectories).Order().SequenceEqual(files), "result review exported files");
            foreach (var step in new[] { CalibrationStep.CoarseRectify, CalibrationStep.CoarseFit, CalibrationStep.Plan, CalibrationStep.FineImport, CalibrationStep.FineRectify, CalibrationStep.FineFit, CalibrationStep.Finish })
            {
                Check(window.GoToStep(step), "completed step was not revisitable: " + step);
                Capture(window, Path.Combine(output, $"wizard-{(int)step:00}-restored.png"));
            }
            Check(RecordJson.Hash(task.Coarse.Session) == first && RecordJson.Hash(task.Fine.Session) == fine && task.Plan.ContentSha256 == plan, "back navigation changed bound inputs");
            Check(Find(window, "复制补偿参数").IsEnabled, "navigation invalidated an unchanged consistency check");
            var restored = new MainWindow(); Layout(restored); restored.LoadTwoDiscTask(TwoDiscTask.Load(Path.Combine(output, "two-disc.dvdcal")));
            Check(!Find(restored, "复制补偿参数").IsEnabled, "temporary check status leaked across task restore");
            ReviewFinal(restored);
            Check(Find(restored, "复制补偿参数").IsEnabled, "reopened result did not become usable");
        });
        test("editing coarse observations preserves fine data while disabling stale operations", () =>
        {
            var task = TwoDiscTask.Load(Path.Combine(output, "two-disc.dvdcal")); var window = new MainWindow(); Layout(window); window.LoadTwoDiscTask(task);
            ReviewFinal(window); Check(window.GoToStep(CalibrationStep.CoarseRectify), "cannot edit first disc");
            var originalSession = task.Coarse!.Session;
            var points = task.Coarse.Session.OuterBoundary; var changed = points.SetItem(0, points[0] with { X = points[0].X + .2 });
            typeof(MainWindow).GetMethod("SetPoints", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [changed]);
            Check(window.GoToStep(CalibrationStep.Plan) && window.GoToStep(CalibrationStep.FineFit)
                && Find(window, "前往制作第二张标定盘").Visibility == Visibility.Visible && window.GoToStep(CalibrationStep.Finish), "stale evidence blocked navigation");
            Check(!Find(window, "复制补偿参数").IsEnabled && ParameterBoxes(window, "FinalParameters").All(t => t.Text.Length == 0)
                && task.Fine?.Fit?.IsConfirmed == true, "stale plan exposed parameters or erased independent fine fit");
            var fine = task.Fine; task.Coarse.Session = originalSession.WithMasks(originalSession.Masks.Add(new([new(10, 10), new(30, 10), new(30, 30)])));
            task.Coarse.AttachDense(task.Coarse.AnalyzeDense());
            Check(window.GoToStep(CalibrationStep.Plan) && ReferenceEquals(task.Fine, fine)
                && ReferenceEquals(typeof(MainWindow).GetField("twoDisc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window), task), "automatic preparation erased the previous fine disc");
            Check(!Find(window, "复制参数").IsEnabled && window.GoToStep(CalibrationStep.FineImport) && !Find(window, "更换第二张照片").IsEnabled
                && Find(window, "移除第二张照片").IsEnabled, "stale fine disc was silently rebound or could not be removed");
        });
        test("wizard permits swapping or removing submitted photos without stale results or a locked import", () =>
        {
            var workflow = TwoDiscTask.Load(Path.Combine(output, "two-disc.dvdcal"));
            var first = workflow.Coarse!; var fine = workflow.Fine!; var plan = workflow.Plan;
            var window = new MainWindow(); Layout(window); window.LoadTwoDiscTask(workflow);
            ReviewFinal(window);
            Check(window.GoToStep(CalibrationStep.FineImport) && Find(window, "更换第二张照片").IsEnabled, "completed fine photo locked import");
            var submitFine = typeof(MainWindow).GetMethod("SetFinePhoto", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                submitFine.Invoke(window, [new PhotoTask(fine.Snapshot, first.Photo)]);
                throw new Exception("first photo was accepted as the second");
            }
            catch (TargetInvocationException e) when (e.InnerException is CalibrationInputException)
            {
                Check(ReferenceEquals(workflow.Fine, fine) && Find(window, "复制补偿参数").IsEnabled, "rejected submission damaged existing results");
            }
            var replacement = new PhotoTask(fine.Snapshot, fine.Photo);
            submitFine.Invoke(window, [replacement]);
            Check(ReferenceEquals(workflow.Fine, replacement) && ReferenceEquals(workflow.Plan, plan)
                && ReferenceEquals(workflow.Coarse, first), "fine replacement changed coarse inputs or plan");
            Check(window.GoToStep(CalibrationStep.FineRectify) && window.GoToStep(CalibrationStep.FineFit)
                && Find(window, "前往校正第二张照片").Visibility == Visibility.Visible
                && !Find(window, "复制补偿参数").IsEnabled, "old measurements survived fine replacement");
            Check(window.GoToStep(CalibrationStep.FineImport), "cannot return to remove fine photo");
            Click(window, "移除第二张照片");
            var retainedFine = typeof(MainWindow).GetProperty("FineSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check(workflow.Fine is null && ReferenceEquals(retainedFine.GetValue(window), fine.Snapshot)
                && Find(window, "选择第二张照片").IsEnabled && window.GoToStep(CalibrationStep.FineRectify)
                && Find(window, "前往导入第二张照片").Visibility == Visibility.Visible, "fine removal lost its record or blocked resubmission");
            submitFine.Invoke(window, [replacement]);
            Check(window.GoToStep(CalibrationStep.FineRectify) && window.GoToStep(CalibrationStep.Prepare)
                && Find(window, "更换第一张照片").IsEnabled, "cannot correct first photo after resubmitting second");
            var pendingFirst = typeof(MainWindow).GetField("pendingSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check(ReferenceEquals(pendingFirst.GetValue(window), first.Snapshot), "visiting fine disc changed the first photo's record");

            window.LoadPhotoTask(new PhotoTask(first.Snapshot, first.Photo));
            Check(window.GoToStep(CalibrationStep.Prepare), "cannot return to first photo input");
            Click(window, "移除第一张照片");
            Check(ReferenceEquals(pendingFirst.GetValue(window), first.Snapshot) && Find(window, "选择第一张照片").IsEnabled
                && window.GoToStep(CalibrationStep.CoarseRectify) && Find(window, "前往准备第一张标定盘").Visibility == Visibility.Visible
                && window.GoToStep(CalibrationStep.FineImport) && !Find(window, "导入第二张照片").IsEnabled, "first removal lost its record or retained derived operations");
        });
        test("wizard navigation cannot leave a running fit or an unfinished mask", () =>
        {
            var photo = PhotoTask.Load(Path.Combine(output, "trace-fit.dvdphoto")); var window = new MainWindow(); Layout(window); window.LoadPhotoTask(photo);
            var fit = photo.Fit;
            DesktopStateChecks.WithDispatcher(() =>
            {
                var pending = window.ComputeTraceFit();
                Check(!window.GoToStep(CalibrationStep.Plan) && window.CurrentStep == CalibrationStep.CoarseFit, "navigation escaped a running computation");
                Click(window, "取消计算"); DesktopStateChecks.Pump(pending);
            });
            Check(ReferenceEquals(photo.Fit, fit), "navigation/cancel lost confirmed fit");
            var mode = Nodes(window).OfType<ComboBox>().Single(c => c.Items.Contains("反光遮罩")); mode.SelectedIndex = 2;
            typeof(MainWindow).GetMethod("SetPoints", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                [ImmutableArray.Create(new PixelPoint(10, 10), new PixelPoint(30, 10), new PixelPoint(30, 30))]);
            Check(!window.GoToStep(CalibrationStep.Plan) && window.CurrentStep == CalibrationStep.CoarseFit, "navigation discarded unfinished mask");
            Check(ReferenceEquals(photo.Fit, fit) && photo.Session.Masks.IsEmpty, "unfinished draft altered saved observations");
            Click(window, "放弃遮罩草稿");
            Check(window.GoToStep(CalibrationStep.Plan) && ReferenceEquals(photo.Fit, fit), "explicitly discarding draft did not restore navigation");
        });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

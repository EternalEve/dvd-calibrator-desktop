using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;
using Microsoft.Win32;

namespace DvdCalibrator.App;

public enum CalibrationStep { Prepare = 1, CoarseRectify, CoarseFit, Plan, FineImport, FineRectify, FineFit, Finish }

public sealed partial class MainWindow
{
    public CalibrationStep CurrentStep { get; private set; } = CalibrationStep.Prepare;
    private static readonly string[] StepNames = ["准备第一张标定盘", "校正第一张照片", "测量第一张曲线", "制作第二张标定盘", "导入第二张照片", "校正第二张照片", "测量第二张曲线", "应用补偿参数"];
    private readonly Button[] stepButtons = new Button[8];
    private readonly TextBlock pageTitle = new() { Name = "WizardPageTitle", FontSize = 26, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock pagePurpose = Copy("", 14), pageOutcome = Copy(""), pageGuide = Copy(""), pageActions = Copy(""), pageLocation = Copy("", 13, true);
    private readonly TextBlock nextReason = Copy(""), progressText = Copy(""), evidenceBadge = Copy("");
    private readonly TextBlock prepareSummary = Copy(""), fineSummary = Copy(""), resultSummary = Copy(""), analysisSummary = Copy(""), planSummary = Copy("");
    private readonly TextBlock workspaceHint = Copy("", 15), finalCompatibility = Copy("");
    private readonly TextBlock autoTraceHint = Copy("请自行删除偏差较大的点。切换到对应描线，选中点后按 Delete，修改后点“拟合描线”。");
    private readonly TextBlock planPlaceholder = Copy("完成粗调后，这里显示第二张的预计图案。");
    private readonly List<(Image Image, TextBlock Placeholder)> previewPlaceholders = [];
    private FrameworkElement preparePage = null!, workspacePage = null!, workspaceEmpty = null!, planPage = null!, importPage = null!, resultPage = null!;
    private FrameworkElement rectifyActions = null!, analysisActions = null!, boundaryModes = null!, traceModes = null!;
    private Button previousStep = null!, nextStep = null!, importFirst = null!, removeFirst = null!, finishMaskButton = null!, discardMaskButton = null!;
    private Button editFineParameters = null!, useMeasuredParameters = null!, openRequiredStep = null!;
    private readonly List<Button> editModeButtons = [];
    private TwoDiscTask? checkedWorkflow;
    private PhotoSession? checkedCoarseSession, checkedFineSession;
    private object? checkedCoarseResult;
    private TraceFitReview? checkedFineFit;
    private string? checkedPlan;
    private TwoDiscConsistency? checkedConsistency;
    private double? checkedFineWidth;
    private string? finalCheckIssue;
    private PhotoTask? FirstPhoto => twoDisc is null ? task : twoDisc.Coarse;
    private bool IsPhotoPage => CurrentStep is CalibrationStep.CoarseRectify or CalibrationStep.CoarseFit or CalibrationStep.FineRectify or CalibrationStep.FineFit;
    private bool IsFitPage => CurrentStep is CalibrationStep.CoarseFit or CalibrationStep.FineFit;

    private FrameworkElement BuildWizard()
    {
        var root = new Grid(); root.SetResourceReference(Panel.BackgroundProperty, "AppBackgroundBrush");
        root.ColumnDefinitions.Add(new() { Width = new(218) }); root.ColumnDefinitions.Add(new());
        var sidebar = new DockPanel { Margin = new(12, 16, 10, 16) }; root.Children.Add(sidebar);
        var brand = new StackPanel { Margin = new(4, 0, 0, 18) };
        brand.Children.Add(Copy("DVD CALIBRATOR", 16, true)); brand.Children.Add(Copy("双盘标定向导", 22));
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var sideFooter = new StackPanel { Margin = new(4, 12, 4, 0) };
        sideFooter.Children.Add(progressText); sideFooter.Children.Add(Copy("可自由查看各步骤。\n保存进度后，下次可继续。"));
        DockPanel.SetDock(sideFooter, Dock.Bottom); sidebar.Children.Add(sideFooter);
        var steps = new StackPanel();
        for (int i = 0; i < 8; i++)
        {
            var step = (CalibrationStep)(i + 1); var button = Button($"{i + 1:00}  {StepNames[i]}", () => GoToStep(step));
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Margin = new(0, 0, 0, 7); button.Padding = new(12, 14, 8, 14);
            button.Name = "WizardStep" + (i + 1); ToolTipService.SetShowOnDisabled(button, true);
            stepButtons[i] = button; steps.Children.Add(button);
        }
        sidebar.Children.Add(new ScrollViewer { Content = steps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var main = new DockPanel { Margin = new(12, 16, 20, 16) }; Grid.SetColumn(main, 1); root.Children.Add(main);
        var global = new DockPanel { LastChildFill = true, Margin = new(0, 0, 0, 14) };
        var files = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        files.Children.Add(Button("新手教程", OpenBeginnerGuide));
        files.Children.Add(Button("新任务", NewTask)); files.Children.Add(Button("打开任务", OpenAnyTask));
        saveButton = Button("保存进度", SaveProgress); files.Children.Add(saveButton);
        cancelButton = Button("取消计算", CancelCalculation); files.Children.Add(cancelButton);
        DockPanel.SetDock(files, Dock.Right); global.Children.Add(files); global.Children.Add(evidenceBadge);
        DockPanel.SetDock(global, Dock.Top); main.Children.Add(global);
        var introduction = new StackPanel { Margin = new(0, 0, 0, 12) };
        introduction.Children.Add(pageTitle); introduction.Children.Add(pagePurpose);
        var lesson = new StackPanel(); lesson.Children.Add(pageLocation); lesson.Children.Add(pageActions); lesson.Children.Add(pageOutcome);
        lesson.Children.Add(new Expander { Header = "本步教程与检查要点", Content = new ScrollViewer { Content = pageGuide, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Margin = new(0, 5, 0, 0) });
        introduction.Children.Add(Card(lesson, "WorkflowBackgroundBrush", 12));
        DockPanel.SetDock(introduction, Dock.Top); main.Children.Add(introduction);

        var bottom = new StackPanel { Margin = new(0, 10, 0, 0) };
        var navigation = new DockPanel();
        previousStep = Button("上一步", () => GoToStep(CurrentStep - 1)); DockPanel.SetDock(previousStep, Dock.Left); navigation.Children.Add(previousStep);
        nextStep = Button("下一步", () => GoToStep(CurrentStep + 1)); DockPanel.SetDock(nextStep, Dock.Right); navigation.Children.Add(nextStep);
        nextReason.Margin = new(12, 6, 12, 6); navigation.Children.Add(nextReason); bottom.Children.Add(navigation);
        bottom.Children.Add(Card(status, "StatusBackgroundBrush", 0));
        DockPanel.SetDock(bottom, Dock.Bottom); main.Children.Add(bottom);

        var pages = new Grid { Name = "WizardPages" }; main.Children.Add(pages);
        preparePage = BuildPreparePage(); workspacePage = BuildPhotoWorkspace(); planPage = BuildPlanPage(); importPage = BuildImportPage(); resultPage = BuildResultPage();
        var missing = new StackPanel();
        missing.Children.Add(workspaceHint);
        openRequiredStep = Button("打开所需步骤", () => GoToStep(RequiredWorkspaceStep()));
        openRequiredStep.HorizontalAlignment = HorizontalAlignment.Left; missing.Children.Add(openRequiredStep);
        workspaceEmpty = Card(missing); workspaceEmpty.VerticalAlignment = VerticalAlignment.Top;
        foreach (var page in new[] { preparePage, workspacePage, workspaceEmpty, planPage, importPage, resultPage }) pages.Children.Add(page);
        return root;
    }

    private FrameworkElement BuildPreparePage()
    {
        var body = new StackPanel();
        var existing = Button("使用已有参数", EditFineParameters); existing.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(existing);
        var firstTime = new StackPanel(); firstTime.Children.Add(Copy("还没有第一张盘？从这里开始", 18, true));
        firstTime.Children.Add(Copy("1. 保存黑白标定图，在 DiscImageStudio 用默认 DVD 参数居中铺满绘图区，生成镜像并刻录。\n2. 拍摄盘片记录面，保留完整外缘和中心孔。\n3. 在本页提交同名的 .iso.json 和实拍照片。（.iso.json 与第 1 条导出的 ISO 在同一目录）", 14));
        firstTime.Children.Add(Button("保存黑白标定图", ExportCoarseSource));
        body.Children.Add(Card(firstTime, "WorkflowBackgroundBrush"));
        body.Children.Add(Copy("已经刻录、拍照并整理好记录？在这里导入", 19, true));
        body.Children.Add(Copy("先选第一张的 .iso.json，再选同一张盘的记录面原始照片。照片要包含完整外缘和中心孔。", 14));
        var inputs = new WrapPanel { Margin = new(0, 10, 0, 10) };
        inputs.Children.Add(Button("选择生成记录", OpenSnapshot));
        importFirst = Button("选择第一张照片", ImportPhoto); inputs.Children.Add(importFirst);
        removeFirst = Button("移除第一张照片", RemoveFirstPhoto); inputs.Children.Add(removeFirst);
        body.Children.Add(inputs);
        body.Children.Add(Card(prepareSummary));
        return Scrolling(body);
    }

    private FrameworkElement BuildPhotoWorkspace()
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new(14) }); grid.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star), MinWidth = 280 });
        var left = new DockPanel(); left.SetResourceReference(Panel.BackgroundProperty, "PanelBackgroundBrush"); grid.Children.Add(left);
        var editing = new StackPanel { Margin = new(8) };
        var boundaries = new WrapPanel(); boundaries.Children.Add(ModeButton("标外缘", 0)); boundaries.Children.Add(ModeButton("标中心孔", 1));
        boundaryModes = boundaries; editing.Children.Add(boundaries);
        var traces = new WrapPanel(); traces.Children.Add(ModeButton("描主线", 3)); traces.Children.Add(ModeButton("描对侧检查线", 4)); traces.Children.Add(ModeButton("标反光遮罩", 2));
        finishMaskButton = Button("结束遮罩", FinishMask); discardMaskButton = Button("放弃遮罩草稿", DiscardMaskDraft);
        traces.Children.Add(finishMaskButton); traces.Children.Add(discardMaskButton); traceModes = traces; editing.Children.Add(traces);
        // Retain the source-coordinate mode selector for existing state logic and accessibility tooling.
        mode.Visibility = Visibility.Collapsed; editing.Children.Add(mode);
        var tools = new WrapPanel(); tools.Children.Add(Button("删除选中点", DeleteSelected)); tools.Children.Add(Button("撤销最后一点", UndoPoint));
        tools.Children.Add(Button("适合窗口", FitView)); tools.Children.Add(zoomSlider); editing.Children.Add(tools);
        DockPanel.SetDock(editing, Dock.Top); left.Children.Add(editing);
        counts.TextWrapping = TextWrapping.Wrap; DockPanel.SetDock(counts, Dock.Bottom); left.Children.Add(counts);
        scroll.SetResourceReference(Control.BackgroundProperty, "ViewportBackgroundBrush"); scroll.Content = canvas; left.Children.Add(scroll); canvas.Children.Add(photoImage);

        var right = new StackPanel { Margin = new(0, 0, 4, 0) }; var rightScroll = Scrolling(right);
        Grid.SetColumn(rightScroll, 2); grid.Children.Add(rightScroll);
        var correction = new StackPanel(); correction.Children.Add(Copy("核对照片校正", 18, true));
        correction.Children.Add(Copy("在左侧原照片上标点。外缘是盘片最外侧的实体边，不是有图案区域的边；中心孔是中间真正穿透的小孔。两圈各标 8–16 点，分散到四周。"));
        var correctionButtons = new WrapPanel(); fitButton = Button("计算联合矫正", async () => await Compute()); correctionButtons.Children.Add(fitButton);
        confirmButton = Button("确认矫正", Confirm); correctionButtons.Children.Add(confirmButton); correction.Children.Add(correctionButtons);
        rectifyActions = correction; right.Children.Add(correction);

        var analysis = new StackPanel(); analysis.Children.Add(Copy("得到并核对标定结果", 18, true)); analysisSummary.Name = "WizardAnalysisSummary"; analysis.Children.Add(analysisSummary);
        var analyze = new WrapPanel(); denseButton = Button("分析密环 / 混合", async () => await ComputeDense()); analyze.Children.Add(denseButton);
        autoTraceFitButton = Button("自动描线并拟合", async () => await ComputeTraceFit(automatic: true)); analyze.Children.Add(autoTraceFitButton);
        traceFitButton = Button("拟合描线", async () => await ComputeTraceFit()); analyze.Children.Add(traceFitButton); analysis.Children.Add(analyze);
        analysis.Children.Add(autoTraceHint);
        analysis.Children.Add(candidateBox); traceConfirmButton = Button("确认候选曲线", ConfirmTraceFit); analysis.Children.Add(traceConfirmButton);
        var diagnostics = new StackPanel(); diagnostics.Children.Add(shapeOrder); diagnostics.Children.Add(fitDetail); diagnostics.Children.Add(denseDetail);
        var exports = new WrapPanel(); denseExportButton = Button("导出密环诊断", ExportDense); exports.Children.Add(denseExportButton);
        profileButton = Button("导出补偿配置", ExportProfile); exports.Children.Add(profileButton); diagnostics.Children.Add(exports);
        analysis.Children.Add(new Expander { Header = "高级参数、诊断与单盘导出", Content = diagnostics, Margin = new(0, 8, 0, 8) });
        analysisActions = analysis; right.Children.Add(analysis);

        var tabs = new TabControl { Height = 260, Margin = new(0, 10, 0, 8) };
        tabs.Items.Add(new TabItem { Header = "正视预览", Content = Preview(rectified) }); tabs.Items.Add(new TabItem { Header = "极坐标预览", Content = Preview(polar) }); right.Children.Add(tabs);
        var extra = new StackPanel(); extra.Children.Add(detail);
        var editExtras = new WrapPanel(); editExtras.Children.Add(Button("清空当前标注", ClearCurrent)); editExtras.Children.Add(Button("导出预览", ExportPreviews));
        editExtras.Children.Add(Button("仅保存当前照片", () => SaveTask(task))); extra.Children.Add(editExtras);
        right.Children.Add(new Expander { Header = "照片信息与更多操作", Content = extra });
        return grid;
    }

    private FrameworkElement BuildPlanPage()
    {
        var body = new StackPanel();
        body.Children.Add(Copy("把选用的参数填入 DiscImageStudio", 20, true));
        body.Children.Add(Copy("在“工程设置”填写下列参数，再到“标定”页使用自带的辅助标定图案。可选中单个数值复制。", 14));
        var actions = new WrapPanel();
        copyFineParameters = Button("复制参数", CopyFineParameters); actions.Children.Add(copyFineParameters);
        editFineParameters = Button("编辑 / 使用已有参数", EditFineParameters); actions.Children.Add(editFineParameters);
        useMeasuredParameters = Button("使用本次粗调结果", UseMeasuredParameters); actions.Children.Add(useMeasuredParameters);
        body.Children.Add(actions);
        body.Children.Add(BuildParameterTable(fineParameterBoxes, "CoarseParameters"));
        body.Children.Add(Card(planSummary));
        replaceFine = Button("重新准备第二张", ReplaceFinePreparation); replaceFine.HorizontalAlignment = HorizontalAlignment.Left; body.Children.Add(replaceFine);
        body.Children.Add(Copy("去 DiscImageStudio 制作第二张", 18, true));
        body.Children.Add(Copy("① “工程设置”：填入上述参数并确定。\n" + FineDiscGenerationSteps, 14));
        var settings = new StackPanel(); settings.Children.Add(workflowDetail); settings.Children.Add(Copy("预计照片有效像素 / mm")); settings.Children.Add(finePixels);
        updateFineEstimate = Button("更新拍摄估计", UpdateFineEstimate); updateFineEstimate.HorizontalAlignment = HorizontalAlignment.Left; settings.Children.Add(updateFineEstimate);
        settings.Children.Add(Copy("默认 8，是拍摄前的估计。实际清晰度将在导入照片后检查。"));
        var prediction = new Grid(); prediction.Children.Add(fineForecastImage);
        fineForecastImage.Height = 280;
        planPlaceholder.Margin = new(20); planPlaceholder.HorizontalAlignment = HorizontalAlignment.Center; planPlaceholder.VerticalAlignment = VerticalAlignment.Center;
        planPlaceholder.TextAlignment = TextAlignment.Center; prediction.Children.Add(planPlaceholder);
        settings.Children.Add(Card(prediction, "ViewportBackgroundBrush", 12));
        body.Children.Add(new Expander { Header = "预计图案与拍摄清晰度（可选）", Content = settings, Margin = new(0, 10, 0, 10) });
        return Scrolling(body);
    }

    private FrameworkElement BuildImportPage()
    {
        var body = new StackPanel(); body.Children.Add(Copy("把制作好的第二张交回本工具", 20, true));
        body.Children.Add(Copy("首次导入时，从第二张的 ISO 输出目录选择同名 .calibration.json，再选择第二张记录面照片。\n若对此文件有疑问，请回看上一页中“去 DiscImageStudio 制作第二张”。", 15));
        var inputs = new WrapPanel();
        importFine = Button("导入第二张照片", () => ImportFinePhoto()); inputs.Children.Add(importFine);
        replaceFineRecord = Button("更换第二张生成记录", () => ImportFinePhoto(chooseGeneration: true)); inputs.Children.Add(replaceFineRecord);
        removeFine = Button("移除第二张照片", RemoveFinePhoto); inputs.Children.Add(removeFine);
        body.Children.Add(inputs);
        body.Children.Add(Card(fineSummary));
        return Scrolling(body);
    }

    private FrameworkElement BuildResultPage()
    {
        var body = new StackPanel();
        body.Children.Add(Copy("把补偿参数填入 DiscImageStudio", 20, true));
        body.Children.Add(Copy("在“工程设置”填入下列参数并确定，再到“设计”页导入要刻录的图片。可选中单个数值复制。", 14));
        copyFinalParameters = Button("复制补偿参数", CopyFinalParameters); copyFinalParameters.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(copyFinalParameters);
        body.Children.Add(BuildParameterTable(finalParameterBoxes, "FinalParameters"));
        body.Children.Add(Card(resultSummary));
        body.Children.Add(finalCompatibility);
        return Scrolling(body);
    }

    private static Grid BuildParameterTable(List<TextBox> boxes, string id)
    {
        var grid = new Grid { Name = id, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new(360) });
        foreach (var (label, _) in FineParameterValues(ParameterDefaults))
        {
            int row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var name = Copy(label, 14); name.Margin = new(4, 8, 24, 8); Grid.SetRow(name, row); grid.Children.Add(name);
            var value = new TextBox { IsReadOnly = true, Margin = new(0, 3, 0, 3), Padding = new(10, 6, 10, 6), FontSize = 14 };
            System.Windows.Automation.AutomationProperties.SetName(value, label);
            Grid.SetRow(value, row); Grid.SetColumn(value, 1); grid.Children.Add(value); boxes.Add(value);
        }
        return grid;
    }

    private Button ModeButton(string text, int index)
    {
        var button = Button(text, () => { mode.SelectedIndex = index; RefreshWizard(); }); button.Tag = index; editModeButtons.Add(button); return button;
    }
    private static TextBlock Copy(string text, double size = 13, bool bold = false) => new()
    { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 7) };
    private static Border Card(UIElement content, string background = "PanelBackgroundBrush", double padding = 16)
    {
        var card = new Border { Child = content, CornerRadius = new(7), Padding = new(padding), Margin = new(0, 6, 0, 8) };
        card.SetResourceReference(Border.BackgroundProperty, background); return card;
    }
    private static ScrollViewer Scrolling(UIElement content) => new()
    { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private Grid Preview(Image image)
    {
        var grid = new Grid(); grid.Children.Add(image);
        var placeholder = Copy("计算照片矫正后，这里会显示预览。\n请先标外缘与中心孔，再点击“计算联合矫正”。");
        placeholder.Margin = new(18); placeholder.HorizontalAlignment = HorizontalAlignment.Center; placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.TextAlignment = TextAlignment.Center; placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        grid.Children.Add(placeholder); previewPlaceholders.Add((image, placeholder)); return grid;
    }

    private bool CoarseReady => FirstPhoto?.Fit is { IsConfirmed: true } f && f.Selected.EligibleForReview && f.Result.QualityStatus != "candidate-ambiguous"
        || FirstPhoto?.Dense is { } d && d.QualityStatus != "candidate-ambiguous" && d.Candidates.Any(c => c.Supported);
    private bool PlanCurrent => twoDisc is { UsesManualParameters: true }
        || twoDisc?.Coarse is { } coarse && twoDisc.Plan.Coarse is { } evidence
        && RecordJson.Hash(coarse.Session) == evidence.SessionSha256
        && (evidence.Kind == "trace" ? coarse.Fit is not null && RecordJson.Hash(coarse.Fit) == evidence.ResultSha256
            : coarse.Dense is not null && RecordJson.Hash(coarse.Dense) == evidence.ResultSha256);
    private object? CoarseResult => twoDisc?.Plan.Coarse?.Kind == "trace" ? twoDisc?.Coarse?.Fit : twoDisc?.Coarse?.Dense;
    private bool FinalCheckCurrent => checkedWorkflow is not null && ReferenceEquals(checkedWorkflow, twoDisc) && checkedPlan == twoDisc?.Plan.ContentSha256
        && ReferenceEquals(checkedCoarseSession, twoDisc?.Coarse?.Session) && ReferenceEquals(checkedFineSession, twoDisc?.Fine?.Session)
        && ReferenceEquals(checkedFineFit, twoDisc?.Fine?.Fit) && ReferenceEquals(checkedCoarseResult, CoarseResult) && PlanCurrent;
    private bool SupportsDense
    {
        get { if (task is null) return false; try { return task.AnalyticSnapshot().Pattern.Kind == "half-disc"; } catch (ArgumentException) { return false; } }
    }
    internal bool FinalReady => FinalCheckCurrent && finalCheckIssue is null
        && (checkedConsistency?.Status == "final-review" || checkedFineWidth >= TwoDiscTask.MinimumFineLineWidthPixels);
    private bool StepReady(CalibrationStep step) => step switch
    {
        CalibrationStep.Prepare => true,
        CalibrationStep.CoarseRectify => FirstPhoto is not null,
        CalibrationStep.CoarseFit => FirstPhoto?.Session.IsConfirmed == true,
        CalibrationStep.Plan => CoarseReady || twoDisc?.UsesManualParameters == true,
        CalibrationStep.FineImport => PlanCurrent && twoDisc?.Plan.IsConfirmed == true,
        CalibrationStep.FineRectify => PlanCurrent && twoDisc?.Fine is not null,
        CalibrationStep.FineFit => PlanCurrent && twoDisc?.Fine?.Session.IsConfirmed == true,
        CalibrationStep.Finish => PlanCurrent && twoDisc?.Fine?.Fit?.IsConfirmed == true,
        _ => false
    };
    private bool StepComplete(int index) => index switch
    {
        1 => FirstPhoto is not null, 2 => FirstPhoto?.Session.IsConfirmed == true, 3 => CoarseReady,
        4 => PlanCurrent && twoDisc?.Fine is not null, 5 => PlanCurrent && twoDisc?.Fine is not null,
        6 => PlanCurrent && twoDisc?.Fine?.Session.IsConfirmed == true, 7 => PlanCurrent && twoDisc?.Fine?.Fit?.IsConfirmed == true,
        8 => FinalReady, _ => false
    };
    private string BlockReason(CalibrationStep step)
    {
        if (step >= CalibrationStep.FineImport && twoDisc is not null && !PlanCurrent)
            return "第一张结果已改变。请返回第 4 步，使用更新后的粗调参数准备第二张。";
        return step switch {
        CalibrationStep.CoarseRectify => "先选择第一张的生成记录和照片。",
            CalibrationStep.CoarseFit => FirstPhoto is null ? "先在第 1 步导入第一张照片。" : "请在第 2 步确认第一张的照片矫正。",
            CalibrationStep.Plan => "在第 3 步完成粗调，或在本页使用已有参数。",
        CalibrationStep.FineImport => twoDisc is not null && PlanCurrent ? CalibrationGuidance.Describe([CalibrationGuidance.ForCode(twoDisc.Plan.Forecast.Status)]) : "请先到第 4 步查看粗调参数和第二张拍摄要求。",
        CalibrationStep.FineRectify => "请先导入与计划匹配的第二张生成记录和照片。",
            CalibrationStep.FineFit => twoDisc?.Fine is null ? "先在第 5 步导入第二张照片。" : "请在第 6 步确认第二张的照片矫正。",
            CalibrationStep.Finish => twoDisc?.Fine is null ? "先在第 5 步导入辅助标定盘照片。" : "请在第 7 步拟合并确认精调曲线。", _ => ""
        };
    }
    private CalibrationStep RequiredWorkspaceStep() => CurrentStep switch
    {
        CalibrationStep.CoarseRectify => CalibrationStep.Prepare,
        CalibrationStep.CoarseFit => FirstPhoto is null ? CalibrationStep.Prepare : CalibrationStep.CoarseRectify,
        _ when twoDisc is not null && !PlanCurrent => CalibrationStep.Plan,
        CalibrationStep.FineFit when twoDisc?.Fine is not null => CalibrationStep.FineRectify,
        _ => CalibrationStep.FineImport
    };

    public bool GoToStep(CalibrationStep step)
    {
        if ((int)step is < 1 or > 8) return false;
        if (calculation is not null) { SetStatus("请等待计算完成，或取消计算后再切换步骤。"); return false; }
        if (!MaskDraftReady()) return false;
        CurrentStep = step;
        string? preparationIssue = null;
        if (step is CalibrationStep.Plan or CalibrationStep.FineImport)
        {
            try { EnsureFinePreparation(); }
            catch (Exception e) when (Expected(e)) { preparationIssue = CalibrationGuidance.Describe(e); }
        }
        if (twoDisc is not null && step is CalibrationStep.CoarseRectify or CalibrationStep.CoarseFit or CalibrationStep.Plan) ShowDisc(false);
        else if (twoDisc?.Fine is not null && step is CalibrationStep.FineRectify or CalibrationStep.FineFit or CalibrationStep.Finish) ShowDisc(true);
        if (IsPhotoPage) mode.SelectedIndex = IsFitPage ? 3 : 0;
        Refresh(); if (IsPhotoPage) { ((FrameworkElement)Content).UpdateLayout(); FitView(); }
        SetStatus(preparationIssue ?? (StepReady(step) ? "当前步骤：" + StepNames[(int)step - 1] : BlockReason(step)));
        if (step == CalibrationStep.Finish && IsLoaded) _ = ReviewFinalAsync();
        if (IsPhotoPage && StepReady(step) && task?.Session.Rectification is not null && rectified.Source is null && IsLoaded && calculation is null) _ = RefreshPreviews();
        return true;
    }
    private void SelectResumeStep()
    {
        CurrentStep = twoDisc is not null ? twoDisc.Fine is { } fine
            ? fine.Fit?.IsConfirmed == true ? CalibrationStep.Finish : fine.Session.IsConfirmed ? CalibrationStep.FineFit : CalibrationStep.FineRectify
            : CalibrationStep.Plan
            : task is null ? CalibrationStep.Prepare : task.Session.IsConfirmed ? CalibrationStep.CoarseFit : CalibrationStep.CoarseRectify;
        if (IsPhotoPage) mode.SelectedIndex = IsFitPage ? 3 : 0;
    }

    private void RefreshWizard()
    {
        bool busy = calculation is not null, draft = draftMask.Count > 0;
        if (checkedWorkflow is not null && !ReferenceEquals(checkedWorkflow, twoDisc))
        {
            checkedWorkflow = null; checkedCoarseSession = checkedFineSession = null; checkedCoarseResult = null;
            checkedFineFit = null; checkedPlan = null; checkedConsistency = null; checkedFineWidth = null; finalCheckIssue = null;
        }
        preparePage.Visibility = CurrentStep == CalibrationStep.Prepare ? Visibility.Visible : Visibility.Collapsed;
        bool pageReady = StepReady(CurrentStep);
        workspacePage.Visibility = IsPhotoPage && pageReady ? Visibility.Visible : Visibility.Collapsed;
        workspaceEmpty.Visibility = IsPhotoPage && !pageReady ? Visibility.Visible : Visibility.Collapsed;
        if (workspaceEmpty.Visibility == Visibility.Visible)
        {
            workspaceHint.Text = BlockReason(CurrentStep);
            SetButtonLabel(openRequiredStep, "前往" + StepNames[(int)RequiredWorkspaceStep() - 1]);
        }
        planPage.Visibility = CurrentStep == CalibrationStep.Plan ? Visibility.Visible : Visibility.Collapsed;
        importPage.Visibility = CurrentStep == CalibrationStep.FineImport ? Visibility.Visible : Visibility.Collapsed;
        resultPage.Visibility = CurrentStep == CalibrationStep.Finish ? Visibility.Visible : Visibility.Collapsed;
        rectifyActions.Visibility = boundaryModes.Visibility = IsFitPage ? Visibility.Collapsed : Visibility.Visible;
        analysisActions.Visibility = traceModes.Visibility = IsFitPage ? Visibility.Visible : Visibility.Collapsed;
        pageTitle.Text = $"{(int)CurrentStep:00}  {StepNames[(int)CurrentStep - 1]}";
        var tutorial = Tutorial(CurrentStep, SupportsDense); pagePurpose.Text = tutorial.Purpose; pageOutcome.Text = "完成后得到：" + tutorial.Outcome;
        pageGuide.Text = tutorial.Guide; pageActions.Text = tutorial.Actions; pageLocation.Text = "在哪里操作：" + tutorial.Location;
        bool manual = twoDisc?.UsesManualParameters == true;
        if (manual && CurrentStep == CalibrationStep.Finish)
        {
            pagePurpose.Text = "使用辅助标定盘的实测补偿参数。";
            pageActions.Text = "确认第 7 步的精调曲线。本页检查照片有效线宽，随后可复制参数。";
            pageOutcome.Text = "完成后得到：本张盘的补偿参数。";
            pageGuide.Text = "在 DiscImageStudio 的“工程设置”应用本页参数，再到“设计”页添加图片。";
        }
        int firstStep = manual ? (int)CalibrationStep.Plan : (int)CalibrationStep.Prepare;
        int total = 9 - firstStep;
        int completed = Enumerable.Range(firstStep, total).Count(StepComplete);
        progressText.Text = $"已完成 {completed} / {total} 步";
        for (int i = 0; i < 8; i++)
        {
            var button = stepButtons[i]; var step = (CalibrationStep)(i + 1); bool done = StepComplete(i + 1);
            button.Content = $"{(done ? "✓" : (i + 1).ToString("00"))}  {StepNames[i]}";
            button.IsEnabled = !busy && !draft;
            button.ToolTip = StepNames[i];
            button.SetResourceReference(Control.BackgroundProperty, step == CurrentStep ? "WorkflowBackgroundBrush" : "AppBackgroundBrush");
            button.SetResourceReference(Control.BorderBrushProperty, step == CurrentStep ? "FocusBrush" : "SurfaceBorderBrush");
        }
        previousStep.IsEnabled = !busy && !draft && CurrentStep > CalibrationStep.Prepare;
        nextStep.Visibility = CurrentStep == CalibrationStep.Finish ? Visibility.Collapsed : Visibility.Visible;
        nextStep.IsEnabled = !busy && !draft && CurrentStep < CalibrationStep.Finish;
        nextStep.Content = (int)CurrentStep < 8 ? "下一步：" + StepNames[(int)CurrentStep] : "已完成";
        nextReason.Text = busy ? "计算中，完成或取消后可继续。" : draft ? "先结束遮罩，或在画布按 Esc 放弃草稿。"
            : !pageReady ? BlockReason(CurrentStep) : CurrentStep == CalibrationStep.Finish
                ? FinalReady ? "可将本页参数填入 DiscImageStudio。" : "检查通过后可复制参数。"
                : StepReady(CurrentStep + 1) ? "可继续。" : BlockReason(CurrentStep + 1);
        cancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        finishMaskButton.Visibility = discardMaskButton.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;
        finishMaskButton.IsEnabled = discardMaskButton.IsEnabled = !busy;
        var first = FirstPhoto;
        var second = twoDisc?.Fine;
        SetButtonLabel(importFirst, first is null ? "选择第一张照片" : "更换第一张照片");
        importFirst.IsEnabled = pendingSnapshot is not null && !busy && !draft;
        removeFirst.Visibility = first is null ? Visibility.Collapsed : Visibility.Visible;
        removeFirst.IsEnabled = !busy && !draft;
        bool synthetic = twoDisc?.EvidenceMode == "simulation" || IsSynthetic(FirstPhoto?.Snapshot) || IsSynthetic(task?.Snapshot ?? pendingSnapshot);
        evidenceBadge.Text = synthetic ? "软件示例 · 不代表真实刻录验证" : manual ? "手动参数 · 单盘精调" : "标定任务";
        if (dirty) evidenceBadge.Text += "  · 未保存修改";
        prepareSummary.Text = first is not null ? $"✓ 生成记录已关联\n✓ 记录面照片已导入：{first.Photo.Image.Width} × {first.Photo.Image.Height} 像素\n点击下方“下一步：校正第一张照片”。"
            : pendingSnapshot is not null ? "✓ 生成记录已选择\n现在点“选择第一张照片”，选择这张实盘的记录面照片。" : "尚未导入资料。“选择第一张照片”会在选好生成记录后启用。";
        var fineSnapshot = FineSnapshot;
        fineSummary.Text = twoDisc is not null && !PlanCurrent ? BlockReason(CalibrationStep.FineImport)
            : second is not null ? $"✓ 第二张已关联生成记录和照片：{second.Photo.Image.Width} × {second.Photo.Image.Height} 像素\n下一步独立校正第二张。"
            : fineSnapshot is not null ? "✓ 第二张生成记录已选择。请重新选择记录面照片。"
            : !pageReady && CurrentStep == CalibrationStep.FineImport ? BlockReason(CurrentStep)
            : "第二张尚未导入。请先完成标靶生成、刻录与拍照。";
        SetButtonLabel(importFine, second is not null ? "更换第二张照片" : fineSnapshot is not null ? "选择第二张照片" : "导入第二张照片");
        importFine.IsEnabled = !busy && !draft && PlanCurrent && twoDisc?.Plan.IsConfirmed == true;
        replaceFineRecord.Visibility = fineSnapshot is null ? Visibility.Collapsed : Visibility.Visible;
        replaceFineRecord.IsEnabled = importFine.IsEnabled;
        removeFine.Visibility = second is null ? Visibility.Collapsed : Visibility.Visible;
        removeFine.IsEnabled = !busy && !draft;
        fineForecastImage.Visibility = PlanCurrent ? Visibility.Visible : Visibility.Collapsed;
        planPlaceholder.Visibility = PlanCurrent ? Visibility.Collapsed : Visibility.Visible;
        editFineParameters.IsEnabled = !busy && !draft;
        useMeasuredParameters.IsEnabled = !busy && !draft;
        useMeasuredParameters.Visibility = manual && CoarseReady ? Visibility.Visible : Visibility.Collapsed;
        planSummary.Text = twoDisc is null ? "完成第一张粗调后，这里会显示测量结果。也可直接使用已有参数。" : !PlanCurrent ? "第一张结果已更新，旧第二张资料仍保留。若要重做，请点击“重新准备第二张”。"
            : twoDisc.Plan.Forecast.Status != "plan-review" ? CalibrationGuidance.Describe([CalibrationGuidance.ForCode(twoDisc.Plan.Forecast.Status)])
            : manual ? "✓ 已采用手动参数。请用这组参数制作辅助标定盘。" : "✓ 已采用本次粗调参数。请用这组参数制作辅助标定盘。";
        if (PlanCurrent && (twoDisc!.Plan.Generated.Direction != SpiralDirection.Cw || twoDisc.Plan.Generated.ChannelBitLengthNm != 133.3))
            planSummary.Text += "\n本次方向或位长与原软件辅助标定入口的顺时针、133.3 nm 不同，需要使用支持本次完整参数的生成入口。";
        denseButton.Visibility = SupportsDense && CurrentStep == CalibrationStep.CoarseFit ? Visibility.Visible : Visibility.Collapsed;
        autoTraceFitButton.Visibility = autoTraceHint.Visibility = CurrentStep == CalibrationStep.FineFit ? Visibility.Visible : Visibility.Collapsed;
        autoTraceFitButton.IsEnabled = !busy && !draft && CurrentStep == CalibrationStep.FineFit && pageReady;
        candidateBox.Visibility = traceConfirmButton.Visibility = task?.Fit is null ? Visibility.Collapsed : Visibility.Visible;
        analysisSummary.Text = AnalysisText();
        bool ready = FinalReady;
        var finalParameters = ready ? twoDisc!.Fine!.Fit!.Selected.Parameters : null;
        DisplayParameters(finalParameterBoxes, finalParameters);
        copyFinalParameters.IsEnabled = !busy && !draft && ready;
        finalCompatibility.Text = finalParameters is not null && !CalibrationExport.ProjectCompatible(finalParameters)
            ? "请使用支持本页位长和方向的生成入口。" : "";
        finalCompatibility.Visibility = finalCompatibility.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        resultSummary.Text = !StepReady(CalibrationStep.Finish) ? BlockReason(CalibrationStep.Finish)
            : !FinalCheckCurrent ? busy ? "正在检查补偿结果……" : "等待检查补偿结果。"
            : finalCheckIssue ?? (manual
                ? ready ? $"✓ 单盘精调检查通过。照片有效线宽 {checkedFineWidth:F2} px。"
                    : CalibrationGuidance.Describe([CalibrationGuidance.ForCode("fine-photo-resolution-insufficient")])
                : ready ? $"✓ 两盘一致性检查通过。盘间形状差 RMS {checkedConsistency!.CoarseShapeRmsMm:F4} mm，照片有效线宽 {checkedConsistency.FineLineWidthPixels:F2} px。"
                    : CalibrationGuidance.Describe([CalibrationGuidance.ForCode(checkedConsistency!.Status)]));
        foreach (var (image, placeholder) in previewPlaceholders) placeholder.Visibility = image.Source is null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in editModeButtons) { button.IsEnabled = !busy; button.SetResourceReference(Control.BorderBrushProperty, Equals(button.Tag, mode.SelectedIndex) ? "FocusBrush" : "SurfaceBorderBrush"); }
        Title = "DVD Calibrator · " + StepNames[(int)CurrentStep - 1] + (dirty ? " *" : "");
    }

    private string AnalysisText()
    {
        if (task?.Fit is { } fit)
        {
            if (fit.IsConfirmed) return "✓ 描线候选已确认。请核对原图上的完整橙色曲线，再继续。";
            var issues = CalibrationGuidance.ForTrace(fit);
            return issues.IsEmpty ? "拟合完成。请检查完整橙线和对侧留出区域，没有漏圈或换臂后点击“确认候选曲线”。" : CalibrationGuidance.Describe(issues);
        }
        if (task?.Dense is { } dense) return dense.QualityStatus == "candidate-ambiguous" || !dense.Candidates.Any(c => c.Supported)
            ? CalibrationGuidance.Describe(CalibrationGuidance.ForDense(dense)) : dense.Wfr is null
                ? "✓ 已得到受支持的密环结果。核对紫色完整曲线与有效观测片段，满意后可继续制作第二张。"
                : $"✓ 已识别局部条纹，有效径向覆盖 {dense.Observations.UsableRadialFraction:P0}。核对紫色预测线是否沿着清楚区域的条纹边界，再继续制作第二张。";
        if (CurrentStep == CalibrationStep.FineFit)
            return task?.Session.Traces.IsEmpty == false ? "检查描点后，点“拟合描线”。"
                : "点“自动描线并拟合”，识别照片中的对侧十字臂。也可手动描线。";
        return SupportsDense && CurrentStep == CalibrationStep.CoarseFit ? "这是黑白半盘靶，点“分析密环 / 混合”进行粗调识别；检查完整预测后继续。"
            : "先点“描主线”。在左侧照片中，沿一条十字臂的细线中央，从靠近中心的一端向外依次点至少 24 下，覆盖记录环带径向宽度的 60% 以上。描完点“拟合描线”。";
    }
    private static bool IsSynthetic(SnapshotBundle? snapshot)
    { if (snapshot is null) return false; using var doc = RecordJson.ParseDocument(System.Text.Encoding.UTF8.GetString(snapshot.Files["generation.json"]).TrimStart('\uFEFF')); return doc.RootElement.GetProperty("recordType").GetString() is "generation-snapshot" or "reference-target-simulation"; }

    private void OpenAnyTask()
    {
        if (calculation is not null) return;
        var dialog = new OpenFileDialog { Filter = "标定任务|*.dvdcal;*.dvdphoto", Title = "继续已有任务" };
        if (dialog.ShowDialog(this) != true || !MayReplace()) return;
        if (Path.GetExtension(dialog.FileName).Equals(".dvdcal", StringComparison.OrdinalIgnoreCase)) LoadTwoDiscTask(TwoDiscTask.Load(dialog.FileName));
        else Load(PhotoTask.Load(dialog.FileName), false);
        SetStatus("任务已校验恢复，已定位到可以继续的步骤。请核对本页教程与当前结果。");
    }
    private void SaveProgress() { if (twoDisc is not null && PlanCurrent) SaveTwoDisc(); else SaveTask(FirstPhoto); }
    private void DiscardMaskDraft()
    {
        if (calculation is not null) return;
        draftMask.Clear(); selected = -1; Refresh(); Draw(); SetStatus("未完成遮罩草稿已放弃，已提交的遮罩和观测会保留。");
    }
    private void NewTask()
    {
        if (calculation is not null || !MayReplace()) return;
        ResetFirstInput(null);
        SetStatus("新任务已准备好。先选择第一张的生成记录和对应照片。");
    }
}

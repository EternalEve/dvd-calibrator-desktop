using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;
using Microsoft.Win32;

namespace DvdCalibrator.App;

public sealed partial class MainWindow
{
    private sealed record ParameterPreset(string Name, DiscSpec Disc, GeometryParameters Parameters);
    private static string PresetFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DvdCalibrator", "parameter-presets");
    private static readonly GeometryParameters ParameterDefaults = new(24, 58, 2295104, 133.3, 0, 0, 0,
        SpiralDirection.Cw, GeometryParameters.Convention, 0);

    private void EditFineParameters()
    {
        if (calculation is not null || !MaskDraftReady()) return;
        var dialog = new Window { Title = "编辑 / 使用已有参数", Width = 610, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (IsVisible) dialog.Owner = this;
        dialog.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var body = new StackPanel { Margin = new(20) }; dialog.Content = body;
        var presets = new ComboBox { DisplayMemberPath = nameof(ParameterPreset.Name), Margin = new(0, 4, 0, 10) };
        var name = new TextBox { MaxLength = 80, Margin = new(0, 4, 0, 4) };
        var media = new ComboBox { ItemsSource = new[] { "DVD-R", "DVD+R", DiscSpec.UnspecifiedDvd.MediaType }, SelectedIndex = 0 };
        var fields = new List<TextBox>();
        DiscSpec? loadedDisc = null;

        void Fill(GeometryParameters parameters, DiscSpec? disc = null)
        {
            var values = FineParameterValues(parameters);
            for (int i = 0; i < fields.Count; i++) fields[i].Text = values[i].Value;
            if (disc is not null) { loadedDisc = disc; media.SelectedItem = disc.MediaType; }
        }
        GeometryParameters ReadFields() => ParseFineParameterValues(fields.Select(f => f.Text).ToArray());
        DiscSpec ReadDisc()
        {
            string kind = (string)media.SelectedItem;
            return loadedDisc?.MediaType == kind ? loadedDisc : new(kind, 120, 15, 1, null);
        }
        void LoadText(string input)
        {
            var (parameters, disc) = ReadFineParameterText(input);
            Fill(parameters, disc);
        }
        void ReloadPresets()
        {
            presets.ItemsSource = ReadParameterPresets(PresetFolder);
        }

        body.Children.Add(Copy("选择预设，或填写用于制作辅助标定盘的参数。"));
        body.Children.Add(presets);
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedItem is not ParameterPreset preset) return;
            Fill(preset.Parameters, preset.Disc); name.Text = preset.Name;
        };
        ReloadPresets();
        var inputs = new WrapPanel();
        inputs.Children.Add(Button("粘贴整组参数", () => LoadText(Clipboard.GetText())));
        inputs.Children.Add(Button("打开参数文件", () =>
        {
            var open = new OpenFileDialog { Filter = "标定参数|*.json;*.txt", Title = "选择参数配置、精调计划或粗调参数文本" };
            if (open.ShowDialog(dialog) != true) return;
            if (new FileInfo(open.FileName).Length > RecordJson.MaxBytes) throw new ArgumentException("参数文件超过 4 MB。");
            LoadText(File.ReadAllText(open.FileName));
        }));
        body.Children.Add(inputs);
        var grid = new Grid { Margin = new(0, 8, 0, 10) };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new());
        void AddRow(string label, Control control)
        {
            int row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var caption = Copy(label); caption.Margin = new(0, 6, 20, 6);
            control.Margin = new(0, 2, 0, 2);
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            Grid.SetRow(caption, row); Grid.SetRow(control, row); Grid.SetColumn(control, 1);
            grid.Children.Add(caption); grid.Children.Add(control);
        }
        foreach (var (label, _) in FineParameterValues(ParameterDefaults))
        {
            var field = new TextBox { Padding = new(7, 4, 7, 4) };
            AddRow(label, field); fields.Add(field);
        }
        AddRow("盘片类型", media); body.Children.Add(grid);
        var initial = twoDisc?.Plan;
        Fill(initial?.Generated ?? FirstPhoto?.Snapshot.Geometry ?? ParameterDefaults,
            initial?.Disc ?? (FirstPhoto is { } first ? TwoDiscTask.Disc(first.Snapshot) : null));
        body.Children.Add(Copy("预设名称（可选）"));
        body.Children.Add(name);
        var notice = Copy("");
        var actions = new WrapPanel();
        actions.Children.Add(Button("保存为预设", () =>
        {
            string label = name.Text.Trim();
            if (label.Length == 0) throw new ArgumentException("请输入预设名称。");
            SaveParameterPreset(PresetFolder, label, ReadDisc(), ReadFields());
            ReloadPresets();
            notice.Text = $"已保存预设“{label}”。";
        }));
        actions.Children.Add(Button("应用参数", () =>
        {
            if (ApplyFineParameters(ReadFields(), ReadDisc())) dialog.DialogResult = true;
        }));
        actions.Children.Add(new Button { Content = "取消", IsCancel = true });
        body.Children.Add(actions);
        body.Children.Add(notice);
        dialog.ShowDialog();
    }

    private static ParameterPreset[] ReadParameterPresets(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "*.json").Select(RecordJson.Read<ParameterPreset>).OrderBy(p => p.Name).ToArray()
        : [];

    private static void SaveParameterPreset(string folder, string name, DiscSpec disc, GeometryParameters parameters)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, RecordJson.Hash(name) + ".json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { RecordJson.WriteNew(temporary, new ParameterPreset(name, disc, parameters)); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private bool ApplyFineParameters(GeometryParameters parameters, DiscSpec disc)
    {
        if (PlanCurrent && twoDisc!.Plan.Generated == parameters && twoDisc.Plan.Disc == disc)
            return GoToStep(CalibrationStep.Plan);
        var next = TwoDiscTask.CreateManual(disc, parameters, twoDisc?.Plan.ExpectedPhotoPixelsPerMm ?? 8, FirstPhoto);
        if (next.Plan.Forecast.Status == "plan-review") next.ConfirmPlan();
        if (!MayChangeParameterSource()) return false;
        LoadParameterWorkflow(next);
        return true;
    }

    private void UseMeasuredParameters()
    {
        if (calculation is not null || !MaskDraftReady() || !CoarseReady) return;
        var next = TwoDiscTask.Create(FirstPhoto!, twoDisc?.Plan.ExpectedPhotoPixelsPerMm ?? 8);
        if (next.Plan.Forecast.Status == "plan-review") next.ConfirmPlan();
        if (MayChangeParameterSource()) LoadParameterWorkflow(next);
    }

    private bool MayChangeParameterSource() => twoDisc?.Fine is null || MessageBox.Show(this,
        "切换参数会解除当前辅助标定盘的照片和测量关联。继续？", "应用参数",
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void LoadParameterWorkflow(TwoDiscTask next)
    {
        twoDisc = next;
        CurrentStep = CalibrationStep.Plan;
        finePixels.Text = next.Plan.ExpectedPhotoPixelsPerMm.ToString("R", CultureInfo.InvariantCulture);
        Load(next.Coarse, true, false);
        SetStatus(next.UsesManualParameters ? "已应用这组参数。可保存进度，或用它制作辅助标定盘。" : "已恢复本次粗调参数。");
    }

    private static (GeometryParameters Parameters, DiscSpec? Disc) ReadFineParameterText(string input)
    {
        input = input.Trim().TrimStart('\uFEFF');
        if (input.StartsWith('{'))
        {
            using var document = RecordJson.ParseDocument(input);
            switch (document.RootElement.GetProperty("recordType").GetString())
            {
                case "compensation-profile":
                    var profile = RecordJson.Parse<CompensationProfile>(input);
                    if (profile.SchemaVersion != 1) throw new ArgumentException("不支持该补偿配置版本。");
                    ValidateParameterValues(profile.Parameters);
                    return (profile.Parameters, null);
                case "fine-target-plan":
                    var plan = RecordJson.Parse<FineTargetPlan>(input);
                    plan.Validate();
                    return (plan.Generated, plan.Disc);
                default:
                    throw new ArgumentException("请选择补偿配置或精调计划。");
            }
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var labels = FineParameterValues(ParameterDefaults).Select(p => p.Label).ToArray();
        foreach (string line in input.Split('\n'))
        {
            int colon = line.IndexOfAny([':', '：']);
            if (colon < 0) continue;
            string label = line[..colon].Trim();
            if (label == "方向") label = "螺旋方向";
            if (labels.Contains(label) && !values.TryAdd(label, line[(colon + 1)..].Trim()))
                throw new ArgumentException($"参数“{label}”出现多次。");
        }
        if (values.Count != labels.Length) throw new ArgumentException("请粘贴“复制参数”得到的完整八项参数。");
        return (ParseFineParameterValues(labels.Select(label => values[label]).ToArray()), null);
    }

    private static GeometryParameters ParseFineParameterValues(string[] values)
    {
        var labels = FineParameterValues(ParameterDefaults);
        double Number(int i) => double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : throw new ArgumentException($"请为“{labels[i].Label}”填写数字，小数点用“.”。");
        if (!long.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sectors))
            throw new ArgumentException("请为“扇区数”填写整数。");
        var direction = values[7].Trim().ToUpperInvariant() switch
        {
            "CW" or "顺时针" or "顺时针 (CW)" => SpiralDirection.Cw,
            "CCW" or "逆时针" or "逆时针 (CCW)" => SpiralDirection.Ccw,
            _ => throw new ArgumentException("螺旋方向请选择顺时针（CW）或逆时针（CCW）。")
        };
        var parameters = new GeometryParameters(Number(0), Number(1), sectors, Number(6), Number(3), Number(4),
            Number(5), direction, GeometryParameters.Convention, 0);
        ValidateParameterValues(parameters);
        return parameters;
    }

    private static void ValidateParameterValues(GeometryParameters parameters)
    {
        parameters.Validate();
        if (parameters.TotalSectors % 16 != 0) throw new ArgumentException("扇区数必须是 16 的倍数。");
    }
}

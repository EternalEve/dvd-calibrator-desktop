using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using DvdCalibrator.App;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;

internal static class ManualWorkflowChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException or IOException) { return; }
        throw new Exception("Invalid submitted material was accepted.");
    }
    private static PhotoTask BindPhoto(SnapshotBundle snapshot, PhotoTask model)
    {
        var session = PhotoSession.Create(snapshot.Id, snapshot.Hash, model.Photo.Metadata)
            .WithBoundaries(model.Session.OuterBoundary, model.Session.HoleBoundary).Rectify().Confirm()
            .WithMasks(model.Session.Masks).WithTraces(model.Session.Traces);
        return new(snapshot, model.Photo, session);
    }

    public static void Register(Action<string, Action> test, string output, string root)
    {
        string folder = Path.Combine(output, "manual-workflow"); Directory.CreateDirectory(folder);
        string firstRecord = Path.Combine(folder, "first.iso.json"), source = Path.Combine(folder, "half.png"),
            fineRecord = Path.Combine(folder, "fine.calibration.json");
        var legacyDisc = new DiscSpec("DVD+R", 120, 15, 1, null);
        PhotoTask? coarse = null; TwoDiscTask? workflow = null;
        test("first-disc ISO sidecar alone feeds real half-disc analysis with the built-in pattern", () =>
        {
            var model = SyntheticPhoto.CreateDenseDemo(new(24, 58, 2295104, 133.3, 0, 0, 0, SpiralDirection.Cw, GeometryParameters.Convention, 0));
            var g = model.Snapshot.Geometry;
            var record = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "fixtures/upstream-fast-fill.iso.json")))!;
            record["fillSectors"] = g.TotalSectors; record["totalSectors"] = g.TotalSectors;
            record["blockCount"] = g.TotalSectors / 16; record["iso"]!["imageSectors"] = g.TotalSectors;
            var mapping = record["imageMapping"]!; mapping["totalSectors"] = g.TotalSectors; mapping["channelBitLengthNm"] = g.ChannelBitLengthNm;
            mapping["pitchLinear"] = g.PitchLinear; mapping["pitchQuadratic"] = g.PitchQuadratic; mapping["pitchCubic"] = g.PitchCubic;
            mapping["tone"] = new JsonObject { ["ditherStrength"] = .75, ["levels"] = 2, ["brightness"] = 0, ["srgbLinearization"] = false };
            record["sourceImage"] = Path.Combine(folder, "missing-cache", "design-composite-dvd-export.png");
            File.WriteAllText(firstRecord, record.ToJsonString());
            var window = new MainWindow(); WizardChecks.Layout(window);
            var import = typeof(MainWindow).GetMethod("SelectGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var imported = (SnapshotBundle)import.Invoke(window, [firstRecord, false])!;
            Check(imported.Geometry == g && imported.Pattern.Kind == "half-disc", "generation mapping lost");
            Check(imported.Pattern.Id == GenerationDeclarations.BuiltInHalfDisc && TwoDiscTask.Disc(imported) == DiscSpec.UnspecifiedDvd,
                "built-in target or unspecified media was not recorded");
            coarse = BindPhoto(imported, model);
            // Recognition must work with the photo alone, without pre-supplied trace points.
            coarse.Session = coarse.Session.WithTraces([]);
            coarse.AttachDense(coarse.AnalyzeDense());
            var best = coarse.Dense!.Candidates.FirstOrDefault(c => c.Supported);
            Check(best is not null && Math.Abs(best.Parameters.InnerRadiusMm - 24.003) < .0001
                && Math.Abs(best.Parameters.OuterRadiusMm - 58.006) < .0001, "photo-only coarse radii were not recovered");
            Check(coarse.Dense.SnapshotSha256 == imported.Hash, "analysis is not bound to submitted bytes");
            string saved = Path.Combine(folder, "coarse.dvdphoto"); coarse.SaveNew(saved);
            coarse = PhotoTask.Load(saved); Check(coarse.Dense is not null, "real half-disc analysis did not restore");
            DenseExport.Write(Path.Combine(folder, "dense"), coarse.Dense!, coarse.AnalyticSnapshot(), coarse.Session,
                coarse.Photo.Image);
            var restored = SnapshotBundle.Load(Path.Combine(folder, "dense/generation.json"));
            Check(restored.Hash == coarse.Snapshot.Hash, "diagnostics dropped external attachments");
            window.LoadPhotoTask(coarse);
            Check(window.GoToStep(DvdCalibrator.App.CalibrationStep.Plan), "actual coarse result cannot reach parameter preparation");
            workflow = (TwoDiscTask)typeof(DvdCalibrator.App.MainWindow).GetField("twoDisc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            Check(workflow.Plan.IsConfirmed && WizardChecks.Find(window, "复制参数").IsEnabled, "manual handoff still requires separate plan confirmation or export");
        });
        test("upstream auxiliary calibration record drives independent fine fit and complete manual two-disc export", () =>
        {
            Check(workflow is not null, "coarse import prerequisite failed");
            var record = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "fixtures/upstream-calibration.calibration.json")))!;
            var p = record["target"]!["parameters"]!; var g = workflow!.Plan.Generated;
            p["innerRadiusMm"] = g.InnerRadiusMm; p["outerRadiusMm"] = g.OuterRadiusMm;
            p["pitchLinear"] = g.PitchLinear; p["pitchQuadratic"] = g.PitchQuadratic; p["pitchCubic"] = g.PitchCubic;
            File.WriteAllText(fineRecord, record.ToJsonString());
            var snapshot = SnapshotBundle.ImportCalibrationRecord(fineRecord, workflow.Plan.Disc);
            var actual = g with { InnerRadiusMm = 24.003, OuterRadiusMm = 58.006 };
            var fine = BindPhoto(snapshot, SyntheticPhoto.CreateReferencePhoto(workflow.Plan, actual));
            workflow.BindFine(fine); var fit = fine.FitTrace();
            fine.AttachFit(fit.Confirm(snapshot.Geometry, snapshot.Hash, fine.Session));
            Check(Math.Abs(fine.Fit!.Selected.Parameters.OuterRadiusMm - actual.OuterRadiusMm) < 3e-6, "fine fit used wrong generation");
            Check(workflow.CheckConsistency().Status == "final-review", "manual workflow failed consistency");
            workflow.ExportFinal(Path.Combine(folder, "final"));
            string saved = Path.Combine(folder, "two-disc.dvdcal"); workflow.SaveNew(saved);
            var loaded = TwoDiscTask.Load(saved); loaded.Validate();
            Check(loaded.EvidenceMode == "external-record-required" && loaded.Fine!.Fit!.IsConfirmed && loaded.Coarse?.Dense is not null,
                "submitted record identities or confirmation did not restore");
        });
        test("reusable parameters persist and complete single-disc fine calibration without coarse evidence", () =>
        {
            var old = TwoDiscTask.Load(Path.Combine(folder, "two-disc.dvdcal"));
            var parameters = old.Plan.Generated;
            var disc = old.Plan.Disc;
            const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
            var editor = typeof(MainWindow);
            var labels = ((string Label, string Value)[])editor.GetMethod("FineParameterValues", methods)!.Invoke(null, [parameters])!;
            string text = string.Join("\r\n", labels.Select(item => $"{item.Label}：{item.Value}"));
            var parse = editor.GetMethod("ReadFineParameterText", methods)!;
            var pasted = ((GeometryParameters Parameters, DiscSpec? Disc))parse.Invoke(null, [text])!;
            Check(pasted.Parameters == parameters && pasted.Disc is null, "copied parameter text lost precision");
            string planText = JsonSerializer.Serialize(old.Plan, RecordJson.Options);
            Check(((ValueTuple<GeometryParameters, DiscSpec?>)parse.Invoke(null, [planText])!).Item1 == parameters, "existing plan did not load");
            string presets = Path.Combine(folder, "presets");
            var save = editor.GetMethod("SaveParameterPreset", methods)!;
            save.Invoke(null, [presets, "精调 / 原参数", disc, parameters]);
            save.Invoke(null, [presets, "备用", disc, parameters with { OuterRadiusMm = 57 }]);
            save.Invoke(null, [presets, "精调 / 原参数", disc, parameters]);
            var restored = (Array)editor.GetMethod("ReadParameterPresets", methods)!.Invoke(null, [presets])!;
            Check(restored.Length == 2 && Directory.GetFiles(presets).Length == 2
                && restored.Cast<object>().Any(p => Equals(p.GetType().GetProperty("Parameters")!.GetValue(p), parameters)), "named presets did not restore");

            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            var window = new MainWindow(); WizardChecks.Layout(window);
            Check((bool)editor.GetMethod("ApplyFineParameters", instance)!.Invoke(window, [pasted.Parameters, disc])!
                && window.CurrentStep == CalibrationStep.Plan && WizardChecks.Find(window, "保存进度").IsEnabled, "manual entry did not start at preparation");
            var task = (TwoDiscTask)editor.GetField("twoDisc", instance)!.GetValue(window)!;
            Check(task.Coarse is null && task.Plan.Coarse is null && task.Plan.IsConfirmed, "manual plan requires or fabricates coarse evidence");
            string path = Path.Combine(folder, "manual-parameters.dvdcal"); task.SaveNew(path);
            var resumed = TwoDiscTask.Load(path); window.LoadTwoDiscTask(resumed);
            Check(window.GoToStep(CalibrationStep.FineImport) && WizardChecks.Find(window, "导入第二张照片").IsEnabled, "reopened parameter-only task could not import");
            var wrong = TwoDiscTask.CreateManual(disc, parameters with { OuterRadiusMm = 57 });
            wrong.ConfirmPlan(); Reject(() => wrong.BindFine(old.Fine!));
            Reject(() => resumed.CheckConsistency());
            editor.GetMethod("SetFinePhoto", instance)!.Invoke(window, [old.Fine!]);
            Check(window.GoToStep(CalibrationStep.CoarseFit) && WizardChecks.Find(window, "前往准备第一张标定盘").Visibility == Visibility.Visible
                && window.GoToStep(CalibrationStep.Finish) && !WizardChecks.Find(window, "复制补偿参数").IsEnabled, "manual result bypassed its photo check");
            WizardChecks.ReviewFinal(window);
            Check(WizardChecks.Find(window, "复制补偿参数").IsEnabled
                && WizardChecks.ParameterBoxes(window, "FinalParameters").All(b => b.Text.Length > 0), "manual result could not be copied");
            string final = Path.Combine(folder, "single-fine");
            resumed.ExportFinal(final);
            string profile = Path.Combine(final, "calibration-profile.json");
            Check(File.Exists(profile) && !File.Exists(Path.Combine(final, "two-disc-consistency.json")), "manual export claimed two-disc consistency");
            var profileData = (ValueTuple<GeometryParameters, DiscSpec?>)parse.Invoke(null, [File.ReadAllText(profile)])!;
            Check(profileData.Item1 == resumed.Fine!.Fit!.Selected.Parameters, "fine output could not be reused");
            string complete = Path.Combine(folder, "manual-complete.dvdcal");
            resumed.SaveNew(complete);
            window.LoadTwoDiscTask(TwoDiscTask.Load(complete));
            WizardChecks.ReviewFinal(window);
            Check(window.CurrentStep == CalibrationStep.Finish && WizardChecks.Find(window, "复制补偿参数").IsEnabled, "completed manual task did not resume");
            WizardChecks.Capture(window, Path.Combine(folder, "single-fine-wizard.png"));

            var measured = old.Coarse!;
            window.LoadPhotoTask(measured);
            Check(window.GoToStep(CalibrationStep.Plan), "cannot view measured parameters");
            Check((bool)editor.GetMethod("ApplyFineParameters", instance)!.Invoke(window, [parameters with { OuterRadiusMm = 57 }, disc])!,
                "could not edit measured parameters");
            var edited = (TwoDiscTask)editor.GetField("twoDisc", instance)!.GetValue(window)!;
            Check(edited.UsesManualParameters && ReferenceEquals(edited.Coarse, measured) && edited.Plan.Generated.OuterRadiusMm == 57,
                "editing lost the original measurement");
            WizardChecks.Click(window, "使用本次粗调结果");
            var back = (TwoDiscTask)editor.GetField("twoDisc", instance)!.GetValue(window)!;
            Check(!back.UsesManualParameters && back.Plan.Generated == parameters, "measured parameters could not be restored");
        });
        test("fine page automatically fits a submitted reference photo and allows deleting and refitting points", () =>
        {
            var task = TwoDiscTask.Load(Path.Combine(folder, "two-disc.dvdcal"));
            var fine = task.Fine!;
            fine.Session = fine.Session.WithTraces([]);
            var window = new MainWindow(); WizardChecks.Layout(window); window.LoadTwoDiscTask(task);
            Check(window.GoToStep(CalibrationStep.FineFit) && WizardChecks.Find(window, "自动描线并拟合").IsEnabled, "automatic entry unavailable");
            DesktopStateChecks.WithDispatcher(() =>
            {
                var pending = window.ComputeTraceFit(automatic: true);
                WizardChecks.Click(window, "取消计算");
                DesktopStateChecks.Pump(pending);
                Check(fine.Session.Traces.IsEmpty && fine.Fit is null, "cancelled detection committed points");
                DesktopStateChecks.Pump(window.ComputeTraceFit(automatic: true));
            });
            Check(fine.Fit?.Selected.EligibleForReview == true && fine.Session.Traces.Length == 2
                && fine.Session.Traces.All(t => t.Points.Length >= 24), "automatic observations did not fit the target");
            int initialCount = fine.Session.Traces.Single(t => t.Id == "line-1").Points.Length;
            typeof(MainWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, initialCount / 2);
            WizardChecks.Click(window, "删除选中点");
            Check(fine.Fit is null && fine.Session.Traces.Single(t => t.Id == "line-1").Points.Length == initialCount - 1, "deletion left a stale fit");
            DesktopStateChecks.WithDispatcher(() => DesktopStateChecks.Pump(window.ComputeTraceFit()));
            Check(fine.Fit?.Selected.EligibleForReview == true, "could not refit edited observations");
            Check(WizardChecks.Nodes(window).OfType<System.Windows.Controls.TextBlock>().Any(t => t.Visibility == Visibility.Visible
                && t.Text.StartsWith("请自行删除偏差较大的点", StringComparison.Ordinal)), "fine page is missing the point removal hint");
            WizardChecks.Capture(window, Path.Combine(folder, "automatic-fine-wizard.png"));
        });
        test("automatic cross detection rejects an empty photo", () =>
        {
            var task = TwoDiscTask.Load(Path.Combine(folder, "two-disc.dvdcal")).Fine!;
            var pixels = new byte[task.Photo.Image.Width * task.Photo.Image.Height * 3];
            Array.Fill(pixels, (byte)160);
            var blank = PhotoCodec.ImportBytes(PhotoCodec.Encode(new RgbImage(task.Photo.Image.Width, task.Photo.Image.Height, pixels)));
            var session = PhotoSession.Create(task.Snapshot.Id, task.Snapshot.Hash, blank.Metadata)
                .WithBoundaries(task.Session.OuterBoundary, task.Session.HoleBoundary).Rectify().Confirm();
            try { AutoCrossTrace.Extract(blank.Image, session, task.Snapshot.Geometry); }
            catch (CalibrationInputException e) when (e.Code == "auto-trace-insufficient") { return; }
            throw new Exception("Empty photo yielded an automatic trace.");
        });
        test("wrong half-disc source changed tone changed fine parameters and unsupported target versions reject", () =>
        {
            string wrongSource = Path.Combine(folder, "cross.png"); File.WriteAllBytes(wrongSource, ReferenceTarget.PngBytes());
            Reject(() => SnapshotBundle.ImportIsoRecord(firstRecord, wrongSource, legacyDisc));
            File.WriteAllBytes(source, SnapshotBundle.HalfDiscPng());
            var legacy = SnapshotBundle.ImportIsoRecord(firstRecord, source, legacyDisc);
            Check(new SnapshotBundle(legacy.Files).Pattern.Id == "submitted-half-disc" && TwoDiscTask.Disc(legacy) == legacyDisc,
                "previously imported half-disc sources or declared media could not be restored");
            var record = JsonNode.Parse(File.ReadAllText(firstRecord))!; record["imageMapping"]!["tone"]!["brightness"] = 12;
            string bad = Path.Combine(folder, "bad.iso.json"); File.WriteAllText(bad, record.ToJsonString());
            Reject(() => SnapshotBundle.ImportHalfDiscRecord(File.ReadAllBytes(bad)));
            record = JsonNode.Parse(File.ReadAllText(fineRecord))!; record["target"]!["parameters"]!["innerRadiusMm"] = 25;
            string changed = Path.Combine(folder, "changed.calibration.json"); File.WriteAllText(changed, record.ToJsonString());
            var disc = workflow!.Plan.Disc;
            var snapshot = SnapshotBundle.ImportCalibrationRecord(changed, disc);
            var calibration = RecordJson.Parse<CalibrationGeneration>(System.Text.Encoding.UTF8.GetString(snapshot.Files["generation.json"]));
            Reject(() => workflow!.Plan.VerifyCalibrationGeneration(calibration, name => snapshot.Files[name]));
            record["target"]!["schemaVersion"] = 5; File.WriteAllText(changed, record.ToJsonString());
            Reject(() => SnapshotBundle.ImportCalibrationRecord(changed, disc));
            record["target"]!["schemaVersion"] = 6; record["target"]!["parameters"]!["kind"] = "BluRay"; File.WriteAllText(changed, record.ToJsonString());
            Reject(() => SnapshotBundle.ImportCalibrationRecord(changed, disc));
        });
        test("submitted record attachments remain checked on reload and actual half-disc UI enables recognition", () =>
        {
            var window = new DvdCalibrator.App.MainWindow(); WizardChecks.Layout(window); window.LoadPhotoTask(coarse!);
            Check(window.GoToStep(DvdCalibrator.App.CalibrationStep.CoarseFit), "actual coarse photo cannot reach analysis");
            WizardChecks.Layout(window);
            Check(WizardChecks.Find(window, "分析密环 / 混合").IsEnabled, "actual half-disc recognition disabled");
            WizardChecks.Capture(window, Path.Combine(folder, "manual-coarse.png"));
            var files = coarse!.Snapshot.Files.ToDictionary(p => p.Key, p => p.Value.ToArray());
            var changed = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(files["source.json"]))!;
            changed["imageMapping"]!["innerRadiusMm"] = 24.01;
            files["source.json"] = System.Text.Encoding.UTF8.GetBytes(changed.ToJsonString());
            Reject(() => new SnapshotBundle(files));
        });
    }
}

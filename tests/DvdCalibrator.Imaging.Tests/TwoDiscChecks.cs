using System.IO;
using System.IO.Compression;
using System.Collections.Immutable;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;

internal static class TwoDiscChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or IOException) { return; } throw new Exception("Invalid two-disc action accepted."); }
    public static void Register(Action<string, Action> test, string output)
    {
        TwoDiscTask? workflow = null; string package = Path.Combine(output, "two-disc.dvdcal");
        test("pinned upstream PNG decodes to every embedded gray pixel without redraw or rescale", () =>
        {
            var photo = PhotoCodec.ImportBytes(ReferenceTarget.PngBytes()); var gray = ReferenceTarget.GrayPixels();
            Check(photo.Image.Width == 2400 && photo.Image.Height == 2400 && photo.Metadata.AppliedExifOrientation == 1, "reference metadata differs");
            var pixels = photo.Image.Pixels;
            for (int i = 0; i < gray.Length; i++)
                if (pixels[i * 3] != gray[i] || pixels[i * 3 + 1] != gray[i] || pixels[i * 3 + 2] != gray[i]) throw new Exception("decoded reference gray differs");
        });
        test("complete simulated two-disc workflow fits G1 independently and exports final full parameters", () =>
        {
            workflow = SyntheticPhoto.CreateTwoDiscDemo();
            Check(workflow.Plan.IsConfirmed && workflow.Plan.Generated != workflow.Coarse!.Snapshot.Geometry, "G1 still equals G0");
            var fine = workflow.Fine!; var review = fine.FitTrace();
            Check(review.Selected.EligibleForReview && review.Result.Generated == workflow.Plan.Generated, "fine fit did not use own generation");
            Check(Math.Abs(review.Selected.Parameters.InnerRadiusMm - 24.003) < 3e-6 && Math.Abs(review.Selected.Parameters.OuterRadiusMm - 58.006) < 3e-6, "fine oracle radii not recovered");
            fine.AttachFit(review.Confirm(fine.Snapshot.Geometry, fine.Snapshot.Hash, fine.Session));
            var consistency = workflow.CheckConsistency(); Check(consistency.Status == "final-review", "normal simulated discs inconsistent: " + JsonSerializer.Serialize(consistency));
            workflow.SaveNew(package); workflow.ExportFinal(Path.Combine(output, "two-disc-final"));
            var profile = RecordJson.Read<CompensationProfile>(Path.Combine(output, "two-disc-final/calibration-profile.json"));
            Check(profile.Parameters == fine.Fit!.Selected.Parameters && profile.SnapshotSha256 == fine.Snapshot.Hash && profile.PhysicalValidation == "pending", "final profile lost second-disc identity");
            var project = ProjectJsonStore.Load(Path.Combine(output, "two-disc-final/candidate.discproj"));
            Check(Path.GetDirectoryName(project.Outputs.OutputPath) == Path.Combine(output, "two-disc-final"), "project points into temporary staging folder");
            FineTargetExport.Write(Path.Combine(output, "two-disc-target"), workflow.Plan);
            File.WriteAllBytes(Path.Combine(output, "two-disc-fine.png"), fine.Photo.OrientedPng);
            File.WriteAllBytes(Path.Combine(output, "fine-input-generation.json"), fine.Snapshot.Files["generation.json"]);
            RecordJson.WriteNew(Path.Combine(output, "fine-input-session.json"), fine.Session);
        });
        test("two-disc package relocates with separate original photos generation records and confirmations", () =>
        {
            Check(workflow is not null && File.Exists(package), "workflow prerequisite failed");
            string relocated = Path.Combine(output, "relocated-two-disc.dvdcal"); File.Copy(package, relocated);
            var loaded = TwoDiscTask.Load(relocated); loaded.Validate();
            Check(loaded.Plan.ContentSha256 == workflow!.Plan.ContentSha256 && loaded.Fine?.Fit?.IsConfirmed == true && loaded.Status == "final-review", "workflow confirmation changed");
            Check(loaded.Coarse!.Photo.OriginalBytes.SequenceEqual(workflow.Coarse!.Photo.OriginalBytes) && loaded.Fine!.Photo.OriginalBytes.SequenceEqual(workflow.Fine!.Photo.OriginalBytes), "original photos changed");
            Reject(() => loaded.SaveNew(relocated));
        });
        test("G0 wrong plan duplicated original photo and simulation in actual workflow cannot bind second disc", () =>
        {
            var saved = TwoDiscTask.Load(package);
            var coarse = saved.Coarse!;
            var fresh = TwoDiscTask.CreateSimulation(coarse); Reject(() => fresh.BindFine(saved.Fine!)); fresh.ConfirmPlan();
            Reject(() => fresh.BindFine(coarse));
            Reject(() => fresh.BindFine(saved.Fine!)); // Same G1 but a different plan hash.
            var samePhoto = new PhotoTask(saved.Fine!.Snapshot, coarse.Photo); Reject(() => saved.BindFine(samePhoto));
            var actual = TwoDiscTask.Create(coarse); actual.ConfirmPlan();
            var simulated = SyntheticPhoto.CreateReferencePhoto(actual.Plan, saved.Fine.Fit!.Selected.Parameters);
            Reject(() => actual.BindFine(simulated));
        });
        test("changing coarse or fine evidence invalidates plan or final confirmation", () =>
        {
            var loaded = TwoDiscTask.Load(package); loaded.Coarse!.Session = loaded.Coarse.Session.WithMasks([new([new(1, 1), new(10, 1), new(10, 10)])]);
            Reject(loaded.Validate); Reject(() => loaded.SaveNew(Path.Combine(output, "stale-plan.dvdcal")));
            loaded = TwoDiscTask.Load(package); var oldFit = loaded.Fine!.Fit!;
            loaded.Fine.Session = loaded.Fine.Session.WithTraces([loaded.Fine.Session.Traces[0] with { Points = loaded.Fine.Session.Traces[0].Points.RemoveAt(1) }]);
            Check(loaded.Fine.Fit is null, "fine input edit retained fit"); Reject(() => loaded.Fine.AttachFit(oldFit));
            Reject(() => loaded.ExportFinal(Path.Combine(output, "stale-final")));
        });
        test("nested photo tampering and unknown two-disc ZIP entries reject before extraction", () =>
        {
            string path = Path.Combine(output, "tampered-two-disc.dvdcal"); File.Copy(package, path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) { zip.GetEntry("fine.dvdphoto")!.Delete(); using var data = zip.CreateEntry("fine.dvdphoto").Open(); data.Write("changed"u8); }
            Reject(() => TwoDiscTask.Load(path));
            path = Path.Combine(output, "unknown-two-disc.dvdcal"); File.Copy(package, path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) zip.CreateEntry("../escape.txt"); Reject(() => TwoDiscTask.Load(path));
        });
        test("disc-to-disc model change preserves independent fine fit and blocks a shared final claim", () =>
        {
            var coarse = TwoDiscTask.Load(package).Coarse!;
            var changed = TwoDiscTask.CreateSimulation(coarse); changed.ConfirmPlan();
            var actual = coarse.Snapshot.Geometry with { InnerRadiusMm = 24.0035, OuterRadiusMm = 58.0068 };
            var fine = SyntheticPhoto.CreateReferencePhoto(changed.Plan, actual); changed.BindFine(fine);
            var result = fine.FitTrace(); Check(result.Selected.EligibleForReview, "independent fine change not fitted");
            fine.AttachFit(result.Confirm(fine.Snapshot.Geometry, fine.Snapshot.Hash, fine.Session));
            Check(Math.Abs(fine.Fit!.Selected.Parameters.OuterRadiusMm - actual.OuterRadiusMm) < 3e-6, "fine result forced back to coarse model");
            Check(changed.CheckConsistency().Status == "disc-inconsistent", "disc difference not reported");
            Reject(() => changed.ExportFinal(Path.Combine(output, "inconsistent-two-disc-final")));
            var window = new DvdCalibrator.App.MainWindow(); WizardChecks.Layout(window); window.LoadTwoDiscTask(changed);
            WizardChecks.ReviewFinal(window);
            Check(!WizardChecks.Find(window, "复制补偿参数").IsEnabled && WizardChecks.ParameterBoxes(window, "FinalParameters").All(b => b.Text.Length == 0),
                "inconsistent workflow exposed final parameters");
        });
        test("wizard preserves linked G0 G1 simulation forecast and reviews final parameters", () =>
        {
            var window = new DvdCalibrator.App.MainWindow(); var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1450, 1100)); content.Arrange(new Rect(0, 0, 1450, 1100)); content.UpdateLayout();
            window.LoadTwoDiscTask(TwoDiscTask.Load(package)); content.Measure(new Size(1450, 1100)); content.Arrange(new Rect(0, 0, 1450, 1100)); content.UpdateLayout();
            IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
            }
            var objects = Descendants(content).ToArray();
            var status = WizardChecks.Nodes(content).OfType<TextBlock>().Single(t => t.Name == "TwoDiscStatus");
            Check(status.Text.Contains("软件模拟", StringComparison.Ordinal) && status.Text.Contains("第二张 G1", StringComparison.Ordinal), "workflow identity not visible");
            Check(window.GoToStep(DvdCalibrator.App.CalibrationStep.Plan), "completed plan page unavailable"); WizardChecks.Layout(window, 1450, 1100);
            WizardChecks.Nodes(window).OfType<Expander>().Single(e => Equals(e.Header, "预计图案与拍摄清晰度（可选）")).IsExpanded = true;
            WizardChecks.Layout(window, 1450, 1100);
            objects = Descendants(content).ToArray(); Check(objects.OfType<System.Windows.Controls.Image>().Any(i => i.Name == "FineTargetForecast" && i.Source is not null), "full forecast preview missing");
            Check(window.GoToStep(DvdCalibrator.App.CalibrationStep.Finish), "final page unavailable"); WizardChecks.Layout(window, 1450, 1100);
            Check(!WizardChecks.Find(window, "复制补偿参数").IsEnabled, "final parameter copy skipped consistency check");
            WizardChecks.ReviewFinal(window); objects = Descendants(content).ToArray();
            Check(objects.OfType<Button>().Any(b => b.Content is string s && s == "复制补偿参数" && b.IsEnabled), "final parameter copy not enabled");
            var bitmap = new RenderTargetBitmap(1450, 1100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, "two-disc-desktop.png")); encoder.Save(stream);
        });
        test("desktop failed holdout explains corrective action and keeps confirmation disabled", () =>
        {
            var loaded = TwoDiscTask.Load(package); var fine = loaded.Fine!; var h = fine.Session.GetTransform();
            var validation = fine.Session.Traces.First(t => t.Id == "validation-180");
            var changed = validation.Points.Select(p =>
            { var d = h.SourceToDisc(p); return h.DiscToSource(new(-d.Y, d.X)); }).ToImmutableArray();
            fine.Session = fine.Session.WithTraces(fine.Session.Traces.Select(t => t.Id == validation.Id ? t with { Points = changed } : t).ToImmutableArray());
            var review = fine.FitTrace(); Check(!review.Selected.EligibleForReview, "wrong validation arm passed"); fine.AttachFit(review);
            string failedPackage = Path.Combine(output, "failure-guidance.dvdcal"); loaded.SaveNew(failedPackage);
            loaded = TwoDiscTask.Load(failedPackage);
            Check(loaded.Fine?.Fit?.IsConfirmed == false, "restored failed candidate became confirmed");
            var window = new DvdCalibrator.App.MainWindow(); var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1450, 1100)); content.Arrange(new Rect(0, 0, 1450, 1100)); content.UpdateLayout();
            window.LoadTwoDiscTask(loaded); content.Measure(new Size(1450, 1100)); content.Arrange(new Rect(0, 0, 1450, 1100)); content.UpdateLayout();
            IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
            var objects = Descendants(content).ToArray();
            Check(objects.OfType<TextBlock>().Any(t => t.Text.Contains("不要删除留出点", StringComparison.Ordinal)), "actionable guidance missing from interface");
            Check(objects.OfType<Button>().Any(b => b.Content is string s && s == "确认候选曲线" && !b.IsEnabled), "failure became confirmable");
            var bitmap = new RenderTargetBitmap(1450, 1100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, "failure-guidance-desktop.png")); encoder.Save(stream);
        });
    }
}

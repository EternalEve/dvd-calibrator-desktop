using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DvdCalibrator.App;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not ["--task", var path, "--out", var output]) { Console.Error.WriteLine("--task PHOTO.dvdphoto --out NEW_FOLDER"); return 2; }
        var app = new App(); app.InitializeComponent(); var task = PhotoTask.Load(path);
        var analysis = task.AnalyzeDense(); task.AttachDense(analysis);
        TwoDiscTask? workflow = null;
        OutputFolder.Publish(output, folder =>
        {
            RecordJson.WriteNew(Path.Combine(folder, "analysis.json"), analysis);
            task.SaveNew(Path.Combine(folder, "repaired.dvdphoto"));
            var restored = PhotoTask.Load(Path.Combine(folder, "repaired.dvdphoto"));
            if (RecordJson.Hash(restored.Dense!) != RecordJson.Hash(analysis)) throw new Exception("Restored WFR result differs.");
            var window = new MainWindow(); var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1600, 1100)); content.Arrange(new Rect(0, 0, 1600, 1100)); window.LoadPhotoTask(restored);
            if (!window.GoToStep(CalibrationStep.CoarseFit)) throw new Exception("Cannot open real photo analysis.");
            var preview = (Image)typeof(MainWindow).GetField("rectified", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            preview.Source = PhotoCodec.Bitmap(PhotoRaster.Render(task.Photo.Image, task.Session, 640, 640, false));
            typeof(MainWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            content.Measure(new Size(1600, 1100)); content.Arrange(new Rect(0, 0, 1600, 1100)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1600, 1100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(folder, "coarse-review.png"))) encoder.Save(stream);
            if (analysis.Candidates.FirstOrDefault(c => c.Supported) is { } candidate)
            {
                var center = analysis.Wfr?.Centers.FirstOrDefault(c => c.CandidateId == candidate.Id);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawImage(PhotoCodec.Bitmap(task.Photo.Image), new Rect(0, 0, task.Photo.Image.Width, task.Photo.Image.Height));
                    foreach (double offset in new[] { 0d, Math.PI })
                    {
                        var shape = new StreamGeometry();
                        using (var geometry = shape.Open())
                        {
                            bool first = true;
                            foreach (var p in WfrAnalyzer.DisplayCurve(candidate))
                            {
                                var q = task.Session.GetTransform().DiscToSource(new(p.RadiusMm * Math.Cos(p.AngleRadians + offset) - (center?.XMm ?? 0), p.RadiusMm * Math.Sin(p.AngleRadians + offset) - (center?.YMm ?? 0)));
                                if (first) { geometry.BeginFigure(new(q.X, q.Y), false, false); first = false; } else geometry.LineTo(new(q.X, q.Y), true, false);
                            }
                        }
                        dc.DrawGeometry(null, new Pen(Brushes.Yellow, 1), shape);
                    }
                }
                var overlay = new RenderTargetBitmap(task.Photo.Image.Width, task.Photo.Image.Height, 96, 96, PixelFormats.Pbgra32); overlay.Render(visual);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(overlay));
                using var stream = File.Create(Path.Combine(folder, "source-overlay.png")); png.Save(stream);
            }
            if (analysis.Candidates.Any(c => c.Supported) && analysis.QualityStatus != "candidate-ambiguous")
            {
                if (!window.GoToStep(CalibrationStep.Plan)) throw new Exception("Real photo did not unlock the fine-plan page.");
                workflow = (TwoDiscTask)typeof(MainWindow).GetField("twoDisc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                if (!workflow.Plan.IsConfirmed) throw new Exception("Real-photo preparation was not automatic.");
                workflow.SaveNew(Path.Combine(folder, "coarse-plan.dvdcal"));
                content.Measure(new Size(1600, 1100)); content.Arrange(new Rect(0, 0, 1600, 1100)); content.UpdateLayout();
                var prepared = new RenderTargetBitmap(1600, 1100, 96, 96, PixelFormats.Pbgra32); prepared.Render(content);
                var screenshot = new PngBitmapEncoder(); screenshot.Frames.Add(BitmapFrame.Create(prepared));
                using (var stream = File.Create(Path.Combine(folder, "parameters.png"))) screenshot.Save(stream);
                if (!window.GoToStep(CalibrationStep.FineImport)) throw new Exception("Automatic preparation did not unlock the record/photo submission page.");
            }
        });
        Console.WriteLine(JsonSerializer.Serialize(new { analysis.AlgorithmVersion, analysis.QualityStatus, analysis.Observations.UsableRadialFraction,
            candidates = analysis.Candidates.Select(c => new { c.Id, c.Parameters, c.Supported, c.HoldoutImageError, c.RejectionReasons }),
            finePlan = workflow?.Plan.Forecast.Status, restored = true, output = Path.GetFullPath(output) }, RecordJson.Options));
        app.Shutdown();
        return 0;
    }
}

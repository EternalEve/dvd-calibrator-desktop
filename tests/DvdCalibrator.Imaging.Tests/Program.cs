using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;
using System.Windows;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        int passed = 0, total = 0;
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string output = Path.Combine(root, "artifacts", "photo-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        void Test(string name, Action action)
        {
            total++;
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); }
        }
        var demo = SyntheticPhoto.Create();
        var traceDemo = SyntheticPhoto.CreateTraceDemo();
        PhotoOrientationChecks.Register(Test, output);
        WfrChecks.Register(Test, output, root);
        Test("corrupt photos are rejected without creating a task", () =>
        {
            try { PhotoCodec.ImportBytes("not an image"u8.ToArray()); throw new Exception("Corrupt photo accepted"); }
            catch (Exception e) when (e is ArgumentException or FormatException or NotSupportedException or IOException) { }
        });
        Test("PNG decoder preserves exact RGB pixels and original bytes", () =>
        {
            var loaded = PhotoCodec.ImportBytes(demo.Photo.OriginalBytes);
            Check(loaded.Image.PixelsSha256 == demo.Photo.Image.PixelsSha256, "lossless PNG pixels");
            Check(loaded.OriginalBytes.SequenceEqual(demo.Photo.OriginalBytes), "source bytes modified");
        });
        Test("JPEG EXIF orientation is read and applied exactly once", () =>
        {
            var original = new RgbImage(32, 16, Enumerable.Range(0, 32 * 16 * 3).Select(i => (byte)(i % 239)).ToArray());
            foreach (int orientation in new[] { 2, 6, 8 })
            {
                var metadata = new BitmapMetadata("jpg"); metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
                var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
                encoder.Frames.Add(BitmapFrame.Create(PhotoCodec.Bitmap(original), null, metadata, null));
                using var stream = new MemoryStream(); encoder.Save(stream);
                var imported = PhotoCodec.ImportBytes(stream.ToArray());
                Check(imported.Metadata.AppliedExifOrientation == orientation, "EXIF not read");
                Check(imported.Image.Width == (orientation >= 5 ? 16 : 32), "wrong EXIF dimensions");
                var again = PhotoCodec.ImportBytes(imported.OrientedPng);
                Check(again.Metadata.AppliedExifOrientation == 1 && again.Image.PixelsSha256 == imported.Image.PixelsSha256, "EXIF applied twice");
            }
        });
        Test("photo package relocates with source, annotations and confirmation intact", () =>
        {
            demo.Session = demo.Session.Rectify().Confirm().WithMasks([new([new(100, 100), new(200, 100), new(200, 200)])])
                .WithTraces([new("line-1", [new(410, 290), new(480, 300)])]);
            string path = Path.Combine(output, "task.dvdphoto"); demo.SaveNew(path);
            string relocated = Path.Combine(output, "relocated.dvdphoto"); File.Move(path, relocated);
            var loaded = PhotoTask.Load(relocated);
            Check(loaded.Session.IsConfirmed && RecordJson.Hash(loaded.Session) == RecordJson.Hash(demo.Session), "session changed");
            Check(loaded.Photo.OriginalBytes.SequenceEqual(demo.Photo.OriginalBytes), "source changed");
            Reject(() => loaded.SaveNew(relocated));
        });
        Test("package original tamper and unknown archive paths are rejected without extraction", () =>
        {
            string path = Path.Combine(output, "tampered.dvdphoto"); demo.SaveNew(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("photo/original")!.Delete(); using var entry = zip.CreateEntry("photo/original").Open(); entry.Write("changed"u8);
            }
            Reject(() => PhotoTask.Load(path));
            path = Path.Combine(output, "unknown.dvdphoto"); demo.SaveNew(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) zip.CreateEntry("../escape.txt");
            Reject(() => PhotoTask.Load(path));
        });
        Test("external generation attachments remain verifiable inside photo package", () =>
        {
            string folder = Path.Combine(output, "external"); Directory.CreateDirectory(folder);
            var external = ExternalGenerationSnapshot.CreateInFolder(Path.Combine(root, "fixtures/upstream-fast-fill.iso.json"),
                Path.Combine(root, "fixtures/upstream-input.bmp"), new(new("DVD-R", 120, 15, 1, null), 16, "1.3.6", DiscImageRecord.UpstreamCommit, "user-declared"), folder);
            RecordJson.WriteNew(Path.Combine(folder, "generation.json"), external);
            var task = new PhotoTask(SnapshotBundle.Load(Path.Combine(folder, "generation.json")), demo.Photo);
            string path = Path.Combine(output, "external.dvdphoto"); task.SaveNew(path);
            var loaded = PhotoTask.Load(path); Check(loaded.Snapshot.Hash == external.ContentSha256, "external binding lost");
        });
        Test("rectified and polar PNG exports decode with expected dimensions", () =>
        {
            var s = demo.Session;
            File.WriteAllBytes(Path.Combine(output, "original.png"), demo.Photo.OrientedPng);
            foreach (bool polar in new[] { false, true })
            {
                var rendered = PhotoRaster.Render(demo.Photo.Image, s, 640, polar ? 320 : 640, polar);
                byte[] png = PhotoCodec.Encode(rendered);
                File.WriteAllBytes(Path.Combine(output, polar ? "polar.png" : "rectified.png"), png);
                var loaded = PhotoCodec.DecodePng(png);
                Check(loaded.Width == 640 && loaded.Height == (polar ? 320 : 640) && loaded.PixelsSha256 == rendered.PixelsSha256, "PNG export mismatch");
            }
            demo.SaveNew(Path.Combine(output, "demo.dvdphoto"));
            File.WriteAllText(Path.Combine(output, "photo-session.json"), JsonSerializer.Serialize(demo.Session, RecordJson.Options));
            File.WriteAllText(Path.Combine(output, "draft-session.json"), JsonSerializer.Serialize(SyntheticPhoto.Create().Session, RecordJson.Options));
        });
        Test("trace task fits, confirms, exports and restores with stale-result invalidation", () =>
        {
            var review = traceDemo.FitTrace();
            Check(review.Selected.EligibleForReview, "synthetic trace fit not eligible: " + string.Join(',', review.Selected.RejectionReasons));
            traceDemo.AttachFit(review.Confirm(traceDemo.Snapshot.Geometry, traceDemo.Snapshot.Hash, traceDemo.Session));
            string path = Path.Combine(output, "trace-fit.dvdphoto"); traceDemo.SaveNew(path);
            var loaded = PhotoTask.Load(path);
            Check(loaded.Fit?.IsConfirmed == true && RecordJson.Hash(loaded.Fit) == RecordJson.Hash(traceDemo.Fit), "fit review not restored");
            CalibrationExport.Write(Path.Combine(output, "profile"), loaded.Fit!, loaded.Session);
            loaded.Session = loaded.Session.WithMasks([new([new(10, 10), new(30, 10), new(30, 30)])]);
            Check(loaded.Fit is null, "editing a mask retained stale fit");
            Reject(() => loaded.AttachFit(review));
            loaded = PhotoTask.Load(path);
            loaded.Session = loaded.Session.WithTraces(loaded.Session.Traces.RemoveAt(0));
            Check(loaded.Fit is null, "editing a trace retained stale fit");
            File.WriteAllBytes(Path.Combine(output, "trace-photo.png"), traceDemo.Photo.OrientedPng);
            File.WriteAllBytes(Path.Combine(output, "fit-input-generation.json"), traceDemo.Snapshot.Files["generation.json"]);
            File.WriteAllText(Path.Combine(output, "fit-input-session.json"), JsonSerializer.Serialize(traceDemo.Session, RecordJson.Options));
        });
        Test("desktop layout renders fit controls and continuous overlay with stable source coordinates", () =>
        {
            // Layout/render test only: no OS window or input automation.
            var app = new DvdCalibrator.App.App(); app.InitializeComponent();
            var window = new DvdCalibrator.App.MainWindow();
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1380, 840)); content.Arrange(new Rect(0, 0, 1380, 840)); content.UpdateLayout();
            window.LoadPhotoTask(traceDemo); content.Measure(new Size(1380, 840)); content.Arrange(new Rect(0, 0, 1380, 840)); content.UpdateLayout();
            IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                    foreach (var grandchild in Descendants(child)) yield return grandchild;
                }
            }
            var canvas = Descendants(content).OfType<System.Windows.Controls.Canvas>().Single(c => c.Name == "SourcePixelCanvas");
            var slider = Descendants(content).OfType<System.Windows.Controls.Slider>().Single(c => c.Name == "SourceZoom");
            var viewer = Descendants(content).OfType<System.Windows.Controls.ScrollViewer>().Single(c => c.Name == "SourceScroll");
            var selector = Descendants(content).OfType<System.Windows.Controls.ComboBox>().Single(c => c.Name == "FitCandidateSelector");
            Check(selector.Items.Count > 0 && selector.SelectedIndex >= 0, "fitted candidate was not displayed");
            Check(canvas.Children.OfType<System.Windows.Shapes.Polyline>().Any(p => p.Stroke == System.Windows.Media.Brushes.DarkOrange), "continuous prediction missing from source overlay");
            foreach (double zoom in new[] { .5, 2d })
            {
                slider.Value = zoom; content.UpdateLayout(); viewer.ScrollToHorizontalOffset(37); viewer.ScrollToVerticalOffset(51); content.UpdateLayout();
                var point = new Point(137.25, 211.5); var screen = canvas.TranslatePoint(point, content); var recovered = content.TranslatePoint(screen, canvas);
                Check(Math.Abs(recovered.X - point.X) < 1e-8 && Math.Abs(recovered.Y - point.Y) < 1e-8, "zoom/pan changed original pixel coordinates");
            }
            slider.Value = 1; viewer.ScrollToHorizontalOffset(0); viewer.ScrollToVerticalOffset(0); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1380, 840, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, "desktop-layout.png")); encoder.Save(stream);
            Check(content.ActualWidth == 1380 && content.ActualHeight == 840, "layout dimensions");
        });
        PhotoTask? denseDemo = null;
        Test("dense mixed task restores original analysis exports diagnostics and invalidates edited input", () =>
        {
            denseDemo = SyntheticPhoto.CreateDenseDemo();
            var result = denseDemo.AnalyzeDense();
            Check(result.Candidates.Any(c => c.Supported), "dense demo not supported: " + string.Join(";", result.Candidates.Select(c => string.Join(',', c.RejectionReasons))));
            denseDemo.AttachDense(result);
            string package = Path.Combine(output, "dense-fit.dvdphoto"); denseDemo.SaveNew(package);
            var loaded = PhotoTask.Load(package);
            Check(loaded.Dense is not null && RecordJson.Hash(loaded.Dense) == RecordJson.Hash(result), "dense result changed on restore");
            DenseExport.Write(Path.Combine(output, "dense-report"), loaded.Dense!, loaded.AnalyticSnapshot(), loaded.Session, loaded.Photo.Image);
            Check(File.Exists(Path.Combine(output, "dense-report/next-disc.svg")), "next-disc report missing");
            Reject(() => DenseExport.Write(Path.Combine(output, "dense-report"), loaded.Dense!, loaded.AnalyticSnapshot(), loaded.Session, loaded.Photo.Image));
            loaded.Session = loaded.Session.WithMasks([new([new(10, 10), new(40, 10), new(40, 40)])]);
            Check(loaded.Dense is null, "dense result survived edited mask");
            Reject(() => loaded.AttachDense(result));
            File.WriteAllBytes(Path.Combine(output, "dense-photo.png"), denseDemo.Photo.OrientedPng);
        });
        Test("desktop renders dense diagnostics observed fragments and complete prediction", () =>
        {
            Check(denseDemo?.Dense is not null, "dense package prerequisite missing");
            var window = new DvdCalibrator.App.MainWindow();
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1380, 960)); content.Arrange(new Rect(0, 0, 1380, 960)); content.UpdateLayout();
            window.LoadPhotoTask(denseDemo!); content.Measure(new Size(1380, 960)); content.Arrange(new Rect(0, 0, 1380, 960)); content.UpdateLayout();
            IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                    foreach (var nested in Descendants(child)) yield return nested;
                }
            }
            var objects = Descendants(content).ToArray();
            var diagnostic = WizardChecks.Nodes(content).OfType<System.Windows.Controls.TextBlock>().Single(t => t.Name == "DenseDiagnostic");
            Check(diagnostic.Text.Contains("下一张预测待核对", StringComparison.Ordinal), "next-disc status not displayed");
            var canvas = objects.OfType<System.Windows.Controls.Canvas>().Single(c => c.Name == "SourcePixelCanvas");
            Check(canvas.Children.OfType<System.Windows.Shapes.Polyline>().Any(p => p.Stroke == Brushes.MediumPurple), "dense full-model overlay missing");
            Check(canvas.Children.OfType<System.Windows.Shapes.Polyline>().Count(p => p.Stroke == Brushes.DeepSkyBlue) >= 2, "observed gap was bridged");
            var bitmap = new RenderTargetBitmap(1380, 960, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, "dense-desktop.png")); encoder.Save(stream);
        });
        TwoDiscChecks.Register(Test, output);
        ManualWorkflowChecks.Register(Test, output, root);
        WizardChecks.Register(Test, output);
        DesktopStateChecks.Register(Test, traceDemo);
        Console.WriteLine($"{passed}/{total} Windows imaging checks passed. Artifacts: {output}");
        return passed == total ? 0 : 1;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException or IOException) { return; }
        throw new Exception("Invalid task accepted.");
    }
}

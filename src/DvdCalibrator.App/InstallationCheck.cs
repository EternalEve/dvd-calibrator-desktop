using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;

namespace DvdCalibrator.App;

// Explicit maintenance entry point: exercises packaged WPF/codecs/models without opening an OS window.
internal static class InstallationCheck
{
    public static void Run(string output)
    {
        string target = Path.GetFullPath(output);
        OutputFolder.Publish(target, folder =>
        {
            var trace = SyntheticPhoto.CreateTraceDemo(); var fit = trace.FitTrace();
            trace.AttachFit(fit.Confirm(trace.Snapshot.Geometry, trace.Snapshot.Hash, trace.Session));
            string photo = Path.Combine(folder, "trace.dvdphoto"); trace.SaveNew(photo);
            var restored = PhotoTask.Load(photo);
            Require(RecordJson.Hash(restored.Fit!) == RecordJson.Hash(trace.Fit!), "Single-disc fit changed on restore.");
            Require(restored.Photo.Image.PixelsSha256 == trace.Photo.Image.PixelsSha256, "Source pixels changed on restore.");

            var workflow = SyntheticPhoto.CreateTwoDiscDemo(); var fine = workflow.Fine!; var review = fine.FitTrace();
            fine.AttachFit(review.Confirm(fine.Snapshot.Geometry, fine.Snapshot.Hash, fine.Session));
            workflow.SaveNew(Path.Combine(folder, "two-disc.dvdcal"));
            var loaded = TwoDiscTask.Load(Path.Combine(folder, "two-disc.dvdcal"));
            Require(loaded.Plan.ContentSha256 == workflow.Plan.ContentSha256
                && RecordJson.Hash(loaded.Fine!.Fit!) == RecordJson.Hash(fine.Fit!), "Two-disc plan/fit changed on restore.");
            Require(loaded.CheckConsistency().Status == "final-review", "Two-disc consistency check failed.");

            var window = new MainWindow(); var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1380, 1000)); content.Arrange(new Rect(0, 0, 1380, 1000)); content.UpdateLayout();
            window.LoadTwoDiscTask(loaded);
            content.Measure(new Size(1380, 1000)); content.Arrange(new Rect(0, 0, 1380, 1000)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1380, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = new MemoryStream(); encoder.Save(image);
            byte[] png = image.ToArray(); var decoded = PhotoCodec.DecodePng(png);
            Require(decoded.Width == 1380 && decoded.Height == 1000, "WPF PNG roundtrip failed.");
            File.WriteAllBytes(Path.Combine(folder, "workspace.png"), png);
        });
        // Export after publication so engine-project references point at their permanent destination.
        var twoDisc = TwoDiscTask.Load(Path.Combine(target, "two-disc.dvdcal"));
        twoDisc.ExportFinal(Path.Combine(target, "final-export"));
        string basePath = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string runtimePath = Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory()).TrimEnd(Path.DirectorySeparatorChar);
        string wpfPath = typeof(Window).Assembly.Location;
        RecordJson.WriteNew(Path.Combine(target, "installation-check.json"), new
        {
            schemaVersion = 1, recordType = "installation-check", status = "passed", evidenceSource = "software",
            version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(),
            framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            applicationDirectory = basePath, runtimeDirectory = runtimePath, wpfAssembly = wpfPath,
            localCoreRuntime = string.Equals(basePath, runtimePath, StringComparison.OrdinalIgnoreCase),
            localWpfRuntime = string.Equals(Path.GetDirectoryName(wpfPath), basePath, StringComparison.OrdinalIgnoreCase),
            singleDiscRestore = "passed", twoDiscRestore = "passed", finalExport = "passed", offscreenWpfPng = "passed",
            nativeInteraction = "pending", physicalValidation = "pending"
        });
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

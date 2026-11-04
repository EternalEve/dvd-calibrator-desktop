using System.IO;
using System.Text.Json;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;

internal static class WfrChecks
{
    public static void Register(Action<string, Action> test, string output, string root)
    {
        test("managed WFR matches the original pyGPA complex filter ridge and phase oracle", () =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "fixtures/pygpa-wfr-oracle.json")));
            var data = doc.RootElement; int rows = data.GetProperty("rows").GetInt32(), cols = data.GetProperty("columns").GetInt32();
            var field = WindowedFourierRidges.Analyze(data.GetProperty("image").EnumerateArray().Select(x => x.GetDouble()).ToArray(), rows, cols,
                data.GetProperty("frequencies").EnumerateArray().Select(x => x.GetDouble()).ToArray());
            foreach (var expected in data.GetProperty("samples").EnumerateArray())
            {
                int i = expected.GetProperty("row").GetInt32() * cols + expected.GetProperty("column").GetInt32();
                double delta = field.Phase[i] - expected.GetProperty("phase").GetDouble();
                if (Math.Abs(field.Amplitude[i] - expected.GetProperty("amplitude").GetDouble()) > 1e-9
                    || Math.Abs(Math.Atan2(Math.Sin(delta), Math.Cos(delta))) > 1e-9
                    || field.Frequency[i] != expected.GetProperty("frequency").GetDouble()
                    || Math.Abs(field.Coherence[i] - expected.GetProperty("coherence").GetDouble()) > 1e-9)
                    throw new Exception("C# WFR no longer agrees with author implementation.");
            }
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { WindowedFourierRidges.Analyze(new double[64 * 32], 64, 32, [.0625], token: cts.Token); throw new Exception("WFR cancellation ignored"); }
            catch (OperationCanceledException) { }
        });
        test("local WFR recovers known half-disc geometry without accepting a featureless photo", () =>
        {
            var photo = SyntheticPhoto.CreateDenseDemo(new(23.985, 57.995, 2295104, 133.3, .02, -.03, .015, SpiralDirection.Cw, GeometryParameters.Convention, 0));
            photo.Session = photo.Session.WithTraces([]);
            var result = WfrAnalyzer.Analyze(photo.AnalyticSnapshot(), photo.Session, photo.Photo.Image);
            photo.AttachDense(result);
            var best = result.Candidates.FirstOrDefault(c => c.Supported);
            if (best is null || Math.Abs(best.Parameters.InnerRadiusMm - 24.003) > .0003 || Math.Abs(best.Parameters.OuterRadiusMm - 58.006) > .0003)
                throw new Exception("WFR did not recover synthetic oracle: " + JsonSerializer.Serialize(result.Candidates.Select(c => new { c.Parameters, c.Supported, c.RejectionReasons }), RecordJson.Options));
            var display = WfrAnalyzer.DisplayCurve(best).ToArray();
            if (display.Zip(display.Skip(1)).Any(p => Math.Abs(p.Second.AngleRadians - p.First.AngleRadians) > Math.PI / 32))
                throw new Exception("Dense display curve has visible polygon chords.");
            string path = Path.Combine(output, "wfr.dvdphoto"); photo.SaveNew(path); var restored = PhotoTask.Load(path);
            if (RecordJson.Hash(restored.Dense!) != RecordJson.Hash(result)) throw new Exception("WFR changed across restore");
            string diagnostic = Path.Combine(output, "wfr-diagnostic"); DenseExport.Write(diagnostic, result, photo.AnalyticSnapshot(), photo.Session, photo.Photo.Image);
            if (!File.Exists(Path.Combine(diagnostic, "wfr-observations.json")) || !File.ReadAllText(Path.Combine(diagnostic, "local-frequency.csv")).Contains("positive_carrier_cycles_per_mm"))
                throw new Exception("WFR diagnostic omitted local evidence or mislabeled the carrier.");
            try { photo.AttachDense(result with { Settings = result.Settings with { RadiusRangeMm = .3 } }); throw new Exception("Changed WFR settings accepted"); }
            catch (ArgumentException) { }
            var flat = new RgbImage(photo.Photo.Image.Width, photo.Photo.Image.Height, Enumerable.Repeat((byte)120, photo.Photo.Image.Width * photo.Photo.Image.Height * 3).ToArray());
            var imported = PhotoCodec.ImportBytes(PhotoCodec.Encode(flat));
            var session = PhotoSession.Create(photo.Snapshot.Id, photo.Snapshot.Hash, imported.Metadata)
                .WithBoundaries(photo.Session.OuterBoundary, photo.Session.HoleBoundary).Rectify().Confirm();
            var failed = WfrAnalyzer.Analyze(photo.AnalyticSnapshot(), session, imported.Image);
            if (failed.Candidates.Any(c => c.Supported)) throw new Exception("Featureless WFR produced a supported result.");
        });
        test("local WFR preserves CCW geometry and the supplied bit length", () =>
        {
            var photo = SyntheticPhoto.CreateDenseDemo(new(23.985, 57.995, 2295104, 133.3, .02, -.03, .015, SpiralDirection.Ccw, GeometryParameters.Convention, 0));
            photo.Session = photo.Session.WithTraces([]);
            var result = WfrAnalyzer.Analyze(photo.AnalyticSnapshot(), photo.Session, photo.Photo.Image);
            var best = result.Candidates.FirstOrDefault(c => c.Supported);
            if (best is null || best.Parameters.Direction != SpiralDirection.Ccw || best.Parameters.ChannelBitLengthNm != 133.3
                || Math.Abs(best.Parameters.InnerRadiusMm - 24.003) > .0003 || Math.Abs(best.Parameters.OuterRadiusMm - 58.006) > .0003)
                throw new Exception("WFR did not preserve the CCW geometry.");
        });
        test("local WFR rejects a hidden outer-region phase contradiction and insufficient angular coverage", () =>
        {
            var photo = SyntheticPhoto.CreateDenseDemo(new(23.985, 57.995, 2295104, 133.3, .02, -.03, .015, SpiralDirection.Cw, GeometryParameters.Convention, 0));
            photo.Session = photo.Session.WithTraces([]);
            var image = photo.Photo.Image; var transform = photo.Session.GetTransform(); var pixels = image.Pixels.ToArray();
            var g = photo.Snapshot.Geometry; var model = new ForwardModel(g, g with { InnerRadiusMm = 24.003, OuterRadiusMm = 58.006 });
            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
            {
                var p = transform.SourceToDisc(new(x, y)); double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                if (r <= 51 || r >= 57.8) continue;
                byte v = Math.Sin(Math.Atan2(p.Y, p.X) - model.AtRadius(r).PhaseRadians - .41 + 1.2) >= 0 ? (byte)195 : (byte)65;
                int at = (y * image.Width + x) * 3; pixels[at + 1] = v; pixels[at + 2] = (byte)(130 + (v - 130) * .6);
            }
            var imported = PhotoCodec.ImportBytes(PhotoCodec.Encode(new(image.Width, image.Height, pixels)));
            var session = PhotoSession.Create(photo.Snapshot.Id, photo.Snapshot.Hash, imported.Metadata)
                .WithBoundaries(photo.Session.OuterBoundary, photo.Session.HoleBoundary).Rectify().Confirm();
            var failed = WfrAnalyzer.Analyze(photo.AnalyticSnapshot(), session, imported.Image);
            if (failed.Candidates.Any(c => c.Supported) || !failed.Candidates.Any(c => c.RejectionReasons.Contains("wfr-holdout-failed")))
                throw new Exception("An outer-region contradiction was not rejected by independent validation.");
            var masked = photo.Session.WithMasks([new([new(0, 0), new(999, 0), new(999, 500), new(500, 500), new(500, 999), new(0, 999)])]);
            var obscured = WfrAnalyzer.Analyze(photo.AnalyticSnapshot(), masked, image);
            if (obscured.QualityStatus != "photo-insufficient" || obscured.Candidates.Any(c => c.Supported))
                throw new Exception("A single visible quadrant is insufficient for photo-center fitting.");
        });
    }
}

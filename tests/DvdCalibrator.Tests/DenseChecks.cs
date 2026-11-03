using System.Collections.Immutable;
using DvdCalibrator.Core;

internal static class DenseChecks
{
    internal static (GenerationSnapshot Snapshot, PhotoSession Session, RgbImage Image) Sample(bool gap = false,
        bool reversed = false, bool flat = false, bool inverted = false, bool mixed = false, bool wrongTrace = false,
        bool turning = false, bool highDensity = false)
    {
        var g = new GeometryParameters(24, 58, 2295104, 133.3, .02, -.03, .015,
            reversed ? SpiralDirection.Ccw : SpiralDirection.Cw, GeometryParameters.Convention, 0);
        var actual = g with { InnerRadiusMm = turning ? 24.01 : 24.003, OuterRadiusMm = turning ? 57.99 : 58.006 };
        if (highDensity) actual = g with { InnerRadiusMm = 23.981, OuterRadiusMm = 58.019 };
        var s = GenerationSnapshot.CreateSynthetic("dense-independent-reference", new("DVD+R", 120, 15, 1, null), g,
            new("half-v1", "half-disc", .3, "analytic-disc-xy-v1"));
        // Independent Simpson/bisection oracle, tabulated only to render the software photograph.
        var ga = new ReferenceGeometry(g, 2048); var aa = new ReferenceGeometry(actual, 2048);
        double Phase(double r) => aa.Angle(r) - ga.Angle(ga.Radius(aa.Arc(r) / actual.TrackLengthMm)) + .41;
        var table = Enumerable.Range(0, 257).Select(i => Phase(actual.InnerRadiusMm + (actual.OuterRadiusMm - actual.InnerRadiusMm) * i / 256)).ToArray();
        double Interpolate(double r)
        {
            double x = (r - actual.InnerRadiusMm) / (actual.OuterRadiusMm - actual.InnerRadiusMm) * 256;
            int i = Math.Clamp((int)x, 0, 255); return table[i] + (table[i + 1] - table[i]) * (x - i);
        }
        int size = highDensity ? 500 : 1000; byte[] pixels = new byte[size * size * 3];
        double scale = size / 1000d;
        ImmutableArray<double> matrix = [7.5 * scale, .2 * scale, 500 * scale, -.1 * scale, 7.4 * scale, 500 * scale, .0001, -.0001, 1];
        var h = new DiscPhotoTransform(matrix);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            var d = h.SourceToDisc(new(x, y)); double r = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            bool active = !flat && r > actual.InnerRadiusMm && r < actual.OuterRadiusMm && (!gap || r is < 38 or > 41);
            double value = active ? (Math.Sin(Math.Atan2(d.Y, d.X) - Interpolate(r)) >= 0 ? 195 : 65) : 130;
            if (inverted) value = 260 - value;
            int at = (y * size + x) * 3;
            pixels[at] = 120; // A deliberately uninformative red channel.
            pixels[at + 1] = (byte)value;
            pixels[at + 2] = (byte)(130 + (value - 130) * .6);
        }
        var image = new RgbImage(size, size, pixels); string hash = image.PixelsSha256;
        ImmutableArray<PixelPoint> Circle(double radius) => Enumerable.Range(0, 32).Select(i => h.DiscToSource(new(radius * Math.Cos(i * Math.PI / 16), radius * Math.Sin(i * Math.PI / 16)))).ToImmutableArray();
        var session = PhotoSession.Create(s.Id, s.ContentSha256, new(hash, hash, hash, size, size, size, size, 1)).WithBoundaries(Circle(60), Circle(7.5));
        // Exact known synthetic calibration, separate from the boundary fitter under test in PhotoChecks.
        session = session with { Rectification = new(session.BoundaryHash(), PhotoSession.Algorithm, PhotoSession.MatrixConvention,
            matrix, 0, 0) };
        session = session.Confirm();
        if (mixed)
        {
            var trace = Enumerable.Range(0, 28).Select(i => { double r = 29 + 4d * i / 27, a = Phase(r) + (wrongTrace ? .6 : 0); return h.DiscToSource(new(r * Math.Cos(a), r * Math.Sin(a))); }).ToImmutableArray();
            session = session.WithTraces([new("line-1", trace)]);
        }
        return (s, session, image);
    }

    public static void Run(Action<string, Action> test)
    {
        var sample = Sample(gap: true);
        DenseAnalysis? fitted = null;
        test("dense varying-frequency independent photograph recovers complete radii across missing band", () =>
        {
            fitted = DenseAnalyzer.Analyze(sample.Snapshot, sample.Session, sample.Image);
            var best = fitted.Candidates.FirstOrDefault(c => c.Supported);
            Check(best is not null, "no supported candidate: " + string.Join(";", fitted.Candidates.Select(c => $"{c.Parameters.InnerRadiusMm:G9}/{c.Parameters.OuterRadiusMm:G9} phase {c.TrainingPhaseRmsRadians:F3}/{c.HoldoutPhaseRmsRadians:F3} image {c.TrainingImageError:F3}/{c.HoldoutImageError:F3}: {string.Join(',', c.RejectionReasons)}")));
            Check(Math.Abs(best!.Parameters.InnerRadiusMm - 24.003) < .00008 && Math.Abs(best.Parameters.OuterRadiusMm - 58.006) < .00008, "oracle radii mismatch");
            Check(fitted.Observations.Fragments.Length >= 2 && fitted.Observations.Fragments.All(f => f.Channel == 1), "gap or RGB selection lost");
            Check(fitted.Observations.Frequencies.Max(f => Math.Abs(f.SignedPhaseCyclesPerMm)) > fitted.Observations.Frequencies.Min(f => Math.Abs(f.SignedPhaseCyclesPerMm)) * 1.08,
                $"local frequency did not vary: {fitted.Observations.Frequencies.Min(f => f.CyclesPerMm):F4}..{fitted.Observations.Frequencies.Max(f => f.CyclesPerMm):F4}; signed {fitted.Observations.Frequencies.Min(f => f.SignedPhaseCyclesPerMm):F4}..{fitted.Observations.Frequencies.Max(f => f.SignedPhaseCyclesPerMm):F4}");
            Check(fitted.NextDisc is { TraceableAtCurrentResolution: true } && fitted.PhysicalValidation == "pending", "next-disc diagnostic missing");
            fitted.Verify(sample.Snapshot, sample.Session, sample.Image);
        });
        test("dense persistence rejects changed observations branches scores and next-disc curves", () =>
        {
            Check(fitted is not null && fitted.Candidates.Length > 0, "fit prerequisite failed");
            var copy = RecordJson.Parse<DenseAnalysis>(System.Text.Json.JsonSerializer.Serialize(fitted, RecordJson.Options));
            copy.Verify(sample.Snapshot, sample.Session, sample.Image);
            Reject(() => (copy with { QualityStatus = "coarse-ready" }).Verify(sample.Snapshot, sample.Session, sample.Image));
            Reject(() => (copy with { Candidates = copy.Candidates.SetItem(0, copy.Candidates[0] with { HoldoutImageError = 0 }) }).Verify(sample.Snapshot, sample.Session, sample.Image));
            Reject(() => copy.Verify(sample.Snapshot, sample.Session.WithMasks([new([new(0, 0), new(20, 0), new(20, 20)])]), sample.Image));
        });
        test("featureless image is explicitly insufficient without invented parameters", () =>
        {
            var s = Sample(flat: true); var result = DenseAnalyzer.Analyze(s.Snapshot, s.Session, s.Image);
            Check(result.QualityStatus == "photo-insufficient" && result.Candidates.IsEmpty && result.NextDisc is null, "flat image accepted");
        });
        test("independent angular image contradiction cannot be fitted away by brightness or phase", () =>
        {
            byte[] pixels = sample.Image.Pixels.ToArray(); var h = sample.Session.GetTransform();
            for (int y = 0; y < sample.Image.Height; y++) for (int x = 0; x < sample.Image.Width; x++)
            {
                var d = h.SourceToDisc(new(x, y)); double angle = Math.Atan2(d.Y, d.X);
                int ray = (int)Math.Round(angle * 48 / (2 * Math.PI));
                if (Math.Abs(ray % 2) == 1 && Math.Abs(angle - ray * 2 * Math.PI / 48) < .035)
                    pixels[(y * sample.Image.Width + x) * 3 + 1] = (byte)(260 - pixels[(y * sample.Image.Width + x) * 3 + 1]);
            }
            var image = new RgbImage(sample.Image.Width, sample.Image.Height, pixels); string hash = image.PixelsSha256;
            var session = sample.Session with { Photo = sample.Session.Photo with { OriginalSha256 = hash, OrientedFileSha256 = hash, OrientedPixelsSha256 = hash }, ConfirmedRectificationSha256 = null };
            session = session with { Rectification = session.Rectification! with { InputSha256 = session.BoundaryHash() } };
            session = session.Confirm();
            var result = DenseAnalyzer.Analyze(sample.Snapshot, session, image);
            Check(result.Candidates.Length > 0 && result.Candidates.All(c => !c.Supported) && result.Candidates.Any(c => c.HoldoutImageError > .25), "angular contradiction accepted");
            Check(result.Candidates.Min(c => c.TrainingImageError) < .15, "heldout corruption leaked into training image fit");
        });
        test("source pixels below ring sampling budget refuse a supported result", () =>
        {
            var s = Sample(highDensity: true); var result = DenseAnalyzer.Analyze(s.Snapshot, s.Session, s.Image);
            Check(result.Candidates.All(c => !c.Supported), "unresolved dense rings accepted: pixels/cycle "
                + string.Join(',', result.Observations.Frequencies.Select(f => f.EffectivePixelsPerCycle.ToString("F2"))));
            Check(result.Candidates.Any(c => c.MinimumPredictedPixelsPerCycle < 8), "under-resolution fixture did not exercise pixel budget");
        });
        test("opposite spiral and inverted optical polarity retain complete model and pi branch", () =>
        {
            var s = Sample(reversed: true, inverted: true); var result = DenseAnalyzer.Analyze(s.Snapshot, s.Session, s.Image);
            Check(result.Candidates.Any(c => c.Supported && c.Parameters.Direction == SpiralDirection.Ccw && Math.Abs(c.Parameters.InnerRadiusMm - 24.003) < .0001), "CCW recovery failed");
            Check(result.Candidates.Where(c => c.Supported).Select(c => c.Polarity).Distinct().Count() == 2, "unknown polarity ambiguity silently removed");
        });
        test("partial continuous boundary joins dense image and rejects incorrect arm evidence", () =>
        {
            var s = Sample(mixed: true); var result = DenseAnalyzer.Analyze(s.Snapshot, s.Session, s.Image);
            Check(result.Candidates.Any(c => c.Supported && c.MixedTraceRmsMm < .1), "partial mixed trace rejected");
            s = Sample(mixed: true, wrongTrace: true); result = DenseAnalyzer.Analyze(s.Snapshot, s.Session, s.Image);
            Check(result.Candidates.All(c => !c.Supported), "incorrect mixed trace accepted");
        });
        test("frequency reversal is split and finite search boundary never passes", () =>
        {
            var s = Sample(turning: true); var result = DenseAnalyzer.Analyze(s.Snapshot, s.Session, s.Image);
            Check(result.Observations.Frequencies.Any(f => f.SignedPhaseCyclesPerMm < -.03) && result.Observations.Frequencies.Any(f => f.SignedPhaseCyclesPerMm > .03),
                "turning frequency not preserved: " + string.Join(',', result.Observations.Frequencies.Select(f => f.SignedPhaseCyclesPerMm.ToString("F3"))));
            Check(result.Candidates.Any(c => c.Supported && Math.Abs(c.Parameters.InnerRadiusMm - 24.01) < .0001 && Math.Abs(c.Parameters.OuterRadiusMm - 57.99) < .0001), "turning candidate not recovered");
            result = DenseAnalyzer.Analyze(sample.Snapshot, sample.Session, sample.Image, DenseSettings.Default with { RadiusRangeMm = .001 });
            Check(result.Candidates.All(c => !c.Supported), "truncated search passed");
        });
        test("dense input rejects stale pixels unsupported patterns unconfirmed geometry and cancellation", () =>
        {
            Reject(() => DenseAnalyzer.Analyze(sample.Snapshot, sample.Session with { ConfirmedRectificationSha256 = null }, sample.Image));
            Reject(() => DenseAnalyzer.Analyze(sample.Snapshot, sample.Session, new(64, 64, new byte[64 * 64 * 3])));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Reject(() => DenseAnalyzer.Analyze(sample.Snapshot, sample.Session, sample.Image, token: cancel.Token));
        });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException or OperationCanceledException) { return; }
        throw new Exception("Invalid dense evidence accepted.");
    }
}

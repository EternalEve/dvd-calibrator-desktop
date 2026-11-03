using System.Collections.Immutable;
using System.Text.Json;
using DvdCalibrator.Core;

internal static class TraceFitChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double x, double y, double tolerance) => Check(Math.Abs(x - y) <= tolerance, $"{x:G17} != {y:G17}");
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid fit input accepted."); }
    public static readonly GeometryParameters Generated = new(24, 58, 2295104, 133.3, 0, 0, 0, SpiralDirection.Cw, GeometryParameters.Convention, 0);
    private static PhotoSession Session(GeometryParameters g, GeometryParameters actual, int count = 64, bool independent = false)
    {
        var s = PhotoSession.Create("g", new string('a', 64), new(new string('b', 64), new string('c', 64), new string('d', 64), 1600, 1600, 1600, 1600, 1));
        PixelPoint Project(double r, double angle) => new(800 + 8 * r * Math.Cos(angle), 800 - 8 * r * Math.Sin(angle));
        ImmutableArray<PixelPoint> Circle(double r) => Enumerable.Range(0, 24).Select(i => Project(r, i * Math.PI / 12)).ToImmutableArray();
        s = s.WithBoundaries(Circle(60), Circle(7.5)).Rectify().Confirm();
        var model = new ForwardModel(g, actual);
        var rg = independent ? new ReferenceGeometry(g, 8192) : null;
        var ra = independent ? new ReferenceGeometry(actual, 8192) : null;
        var points = Enumerable.Range(0, count).Select(i =>
        {
            double r = 25 + 32d * i / (count - 1);
            double phase = independent ? ra!.Angle(r) - rg!.Angle(rg.Radius(ra.Arc(r) / actual.TrackLengthMm)) : model.AtRadius(r).PhaseRadians;
            return Project(r, phase + .4);
        }).ToImmutableArray();
        return s.WithTraces([new("line-1", points)]);
    }
    public static void Register(Action<string, Action> test)
    {
        test("trace fit recovers radii from independent exact-length reference observations", () =>
        {
            var actual = Generated with { InnerRadiusMm = 24.001, OuterRadiusMm = 58.002 };
            var session = Session(Generated, actual, 32, true);
            var result = TraceFitter.Fit(Generated, session.SnapshotSha256, session);
            var best = result.Candidates[0];
            Near(best.Parameters.InnerRadiusMm, actual.InnerRadiusMm, 2e-6);
            Near(best.Parameters.OuterRadiusMm, actual.OuterRadiusMm, 2e-6);
            Check(best.EligibleForReview && best.HoldoutRmsMm < .001, string.Join(',', best.RejectionReasons));
            Check(best.Residuals.Count(r => r.IsHoldout) == 8, "continuous holdout fraction");
        });
        test("nonzero generated coefficients, CCW and non-default bit length are preserved", () =>
        {
            var g = Generated with { PitchLinear = .02, PitchQuadratic = -.03, PitchCubic = .015, Direction = SpiralDirection.Ccw, ChannelBitLengthNm = 133.33 };
            var a = g with { InnerRadiusMm = 24.001, OuterRadiusMm = 58.002 };
            var s = Session(g, a); var fit = TraceFitter.Fit(g, s.SnapshotSha256, s);
            var c = fit.Candidates[0];
            Near(c.Parameters.InnerRadiusMm, a.InnerRadiusMm, 3e-6); Near(c.Parameters.OuterRadiusMm, a.OuterRadiusMm, 3e-6);
            Check(c.Parameters.PitchLinear == g.PitchLinear && c.Parameters.PitchCubic == g.PitchCubic && c.Parameters.ChannelBitLengthNm == g.ChannelBitLengthNm && c.Parameters.Direction == g.Direction, "known generation conditions lost");
        });
        test("held-out mismatch is not optimized away or eligible for confirmation", () =>
        {
            var a = Generated with { InnerRadiusMm = 24.001, OuterRadiusMm = 58.002 };
            var s = Session(Generated, a); var transform = s.GetTransform();
            var trace = s.Traces[0]; var altered = trace.Points.Select((p, i) =>
            {
                if (i < 48) return p;
                var q = transform.SourceToDisc(p); double r = Math.Sqrt(q.X * q.X + q.Y * q.Y), angle = Math.Atan2(q.Y, q.X) + .4;
                return transform.DiscToSource(new(r * Math.Cos(angle), r * Math.Sin(angle)));
            }).ToImmutableArray();
            s = s.WithTraces([trace with { Points = altered }]);
            var result = TraceFitter.Fit(Generated, s.SnapshotSha256, s);
            Check(!result.Candidates.Any(c => c.EligibleForReview), "holdout mismatch passed");
            var best = result.Candidates[0]; Near(best.Parameters.InnerRadiusMm, a.InnerRadiusMm, 3e-6);
            Reject(() => new TraceFitReview(result, best.Id, null).Confirm(Generated, s.SnapshotSha256, s));
        });
        test("stale inputs, masks, reversed order, large gaps and cancellation reject fit", () =>
        {
            var s = Session(Generated, Generated);
            Reject(() => TraceFitter.Fit(Generated, new string('e', 64), s));
            Reject(() => TraceFitter.Fit(Generated, s.SnapshotSha256, s with { ConfirmedRectificationSha256 = null }));
            Reject(() => TraceFitter.Fit(Generated, s.SnapshotSha256, s.WithTraces([s.Traces[0] with { Points = s.Traces[0].Points.Reverse().ToImmutableArray() }])));
            var p = s.Traces[0].Points[10];
            Reject(() => TraceFitter.Fit(Generated, s.SnapshotSha256, s.WithMasks([new([new(p.X - 5, p.Y - 5), new(p.X + 5, p.Y - 5), new(p.X + 5, p.Y + 5), new(p.X - 5, p.Y + 5)])])));
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { TraceFitter.Fit(Generated, s.SnapshotSha256, s, token: cts.Token); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { }
        });
        test("fit review roundtrip preserves input binding and invalidates confirmation", () =>
        {
            var s = Session(Generated, Generated); var result = TraceFitter.Fit(Generated, s.SnapshotSha256, s);
            var review = new TraceFitReview(result, result.Candidates[0].Id, null).Confirm(Generated, s.SnapshotSha256, s);
            var parsed = RecordJson.Parse<TraceFitReview>(JsonSerializer.Serialize(review, RecordJson.Options));
            parsed.Validate(Generated, s.SnapshotSha256, s); Check(parsed.IsConfirmed, "review confirmation lost");
            Reject(() => parsed.Validate(Generated, s.SnapshotSha256, s.WithTraces([s.Traces[0] with { Points = s.Traces[0].Points.RemoveAt(5) }])));
            Check(!parsed.Select(parsed.SelectedCandidateId).IsConfirmed, "selection should reset confirmation");
            var bad = result with { Candidates = result.Candidates.SetItem(0, result.Candidates[0] with
                { Parameters = result.Candidates[0].Parameters with { OuterRadiusMm = 58.005 } }) };
            Reject(() => bad.Validate(Generated, s.SnapshotSha256, s));
        });
        test("radius search expands only when the low-dimensional best hits its boundary", () =>
        {
            var actual = Generated with { InnerRadiusMm = 24.025, OuterRadiusMm = 58.03 };
            var s = Session(Generated, actual, 256); var result = TraceFitter.Fit(Generated, s.SnapshotSha256, s);
            Check(result.SearchedRadiusRangeMm >= .1, "radius range did not expand");
            Near(result.Candidates[0].Parameters.InnerRadiusMm, actual.InnerRadiusMm, 1e-5);
            Check(result.Candidates[0].EligibleForReview, string.Join(',', result.Candidates[0].RejectionReasons));
        });
        test("an incorrectly identified independent arm cannot rotate itself into agreement", () =>
        {
            var s = Session(Generated, Generated);
            var wrongArm = s.Traces[0].Points.Select(p => new PixelPoint(800 - (p.Y - 800), 800 + (p.X - 800))).ToImmutableArray();
            s = s.WithTraces(s.Traces.Add(new("validation-180", wrongArm)));
            var fit = TraceFitter.Fit(Generated, s.SnapshotSha256, s);
            Check(fit.Candidates.All(c => !c.EligibleForReview && c.HoldoutRmsMm > .35), "wrong independent arm was absorbed");
        });
        test("unnecessary higher order is rejected by held-out evidence", () =>
        {
            var s = Session(Generated, Generated); var result = TraceFitter.Fit(Generated, s.SnapshotSha256, s,
                TraceFitSettings.Default with { ShapeOrder = 3 });
            Check(result.Candidates.Any(c => c.ShapeOrder == 3 && !c.EligibleForReview
                && c.RejectionReasons.Contains("higher-order-not-supported-by-holdout")), "high order was allowed without evidence");
        });
        test("supported pitch shape improves a contiguous unoptimized holdout region", () =>
        {
            var a = Generated with { PitchLinear = .005 };
            var s = Session(Generated, a, 384);
            var result = TraceFitter.Fit(Generated, s.SnapshotSha256, s, TraceFitSettings.Default with { ShapeOrder = 1 });
            var shape = result.Candidates.First(c => c.ShapeOrder == 1);
            Check(shape.EligibleForReview && shape.HoldoutRmsMm < .01,
                $"shape holdout {shape.HoldoutRmsMm:G6}, reasons {string.Join(',', shape.RejectionReasons)}");
        });
        test("robust fitting still reports the large residual of a downweighted input point", () =>
        {
            var s = Session(Generated, Generated); var h = s.GetTransform(); var trace = s.Traces[0];
            var p = h.SourceToDisc(trace.Points[15]); double r = Math.Sqrt(p.X * p.X + p.Y * p.Y), a = Math.Atan2(p.Y, p.X) + .1;
            s = s.WithTraces([trace with { Points = trace.Points.SetItem(15, h.DiscToSource(new(r * Math.Cos(a), r * Math.Sin(a)))) }]);
            var result = TraceFitter.Fit(Generated, s.SnapshotSha256, s);
            Check(result.Candidates[0].MaxErrorMm > .8 && !result.Candidates[0].EligibleForReview, "outlier disappeared from quality report");
            Check(result.Candidates[0].Residuals.Length == trace.Points.Length, "input residual dropped");
        });
        test("confirmed profile export uses actual upstream loader and keeps all expressible fields", () =>
        {
            var s = Session(Generated, Generated); var result = TraceFitter.Fit(Generated, s.SnapshotSha256, s);
            var review = new TraceFitReview(result, result.Candidates[0].Id, null);
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            string path = Path.Combine(root, "artifacts", "profile-" + Guid.NewGuid().ToString("N"));
            Reject(() => CalibrationExport.Write(path, review, s));
            review = review.Confirm(Generated, s.SnapshotSha256, s); CalibrationExport.Write(path, review, s);
            var project = DiscImageStudio.Core.ProjectJsonStore.Load(Path.Combine(path, "candidate.discproj"));
            Near(project.Geometry.InnerRadiusMm, review.Selected.Parameters.InnerRadiusMm, 0);
            Near(project.Geometry.OuterRadiusMm, review.Selected.Parameters.OuterRadiusMm, 0);
            Check(project.Geometry.Sectors == Generated.TotalSectors && project.Geometry.PitchCubic == Generated.PitchCubic, "upstream project lost fields");
            var profile = RecordJson.Read<CompensationProfile>(Path.Combine(path, "calibration-profile.json"));
            Check(profile.Parameters == review.Selected.Parameters && profile.PhysicalValidation == "pending", "profile wrong precision or validation status");
            Check(!CalibrationExport.ProjectCompatible(Generated with { ChannelBitLengthNm = 133.33 })
                && !CalibrationExport.ProjectCompatible(Generated with { Direction = SpiralDirection.Ccw }), "inexpressible parameters considered compatible");
        });
        test("incompatible direction and bit length remain complete without a misleading project", () =>
        {
            var g = Generated with { ChannelBitLengthNm = 133.33, Direction = SpiralDirection.Ccw };
            var s = Session(g, g); var result = TraceFitter.Fit(g, s.SnapshotSha256, s);
            var review = new TraceFitReview(result, result.Candidates[0].Id, null).Confirm(g, s.SnapshotSha256, s);
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            string path = Path.Combine(root, "artifacts", "incompatible-profile-" + Guid.NewGuid().ToString("N"));
            CalibrationExport.Write(path, review, s);
            Check(!File.Exists(Path.Combine(path, "candidate.discproj")), "inexpressible parameters were exported as a project");
            var profile = RecordJson.Read<CompensationProfile>(Path.Combine(path, "calibration-profile.json"));
            Check(profile.Parameters.Direction == SpiralDirection.Ccw && profile.Parameters.ChannelBitLengthNm == 133.33
                && !profile.DiscImageProjectCompatible, "parameters were reset to defaults");
        });
    }
}

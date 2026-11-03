using System.Collections.Immutable;
using DvdCalibrator.Core;

internal static class GuidanceChecks
{
    public static void Register(Action<string, Action> test)
    {
        var g = new GeometryParameters(24, 58, 2295104, 133.3, 0, 0, 0, SpiralDirection.Cw, GeometryParameters.Convention, 0);
        PixelPoint Project(double r, double a) => new(800 + 8 * r * Math.Cos(a), 800 - 8 * r * Math.Sin(a));
        ImmutableArray<PixelPoint> Circle(double r) => Enumerable.Range(0, 32).Select(i => Project(r, i * Math.PI / 16)).ToImmutableArray();
        var session = PhotoSession.Create("guidance", new string('a', 64), new(new string('b', 64), new string('c', 64), new string('d', 64), 1600, 1600, 1600, 1600, 1))
            .WithBoundaries(Circle(60), Circle(7.5)).Rectify().Confirm();
        var main = Enumerable.Range(0, 48).Select(i => Project(25 + 32d * i / 47, .4)).ToImmutableArray();
        test("masked click has stable input code and suggests correcting points without removing glare masks", () =>
        {
            var p = main[20]; var bad = session.WithTraces([new("line-1", main)])
                .WithMasks([new([new(p.X - 2, p.Y - 2), new(p.X + 2, p.Y - 2), new(p.X + 2, p.Y + 2), new(p.X - 2, p.Y + 2)])]);
            try { TraceFitter.Prepare(bad, g); throw new Exception("Masked point accepted"); }
            catch (CalibrationInputException e)
            {
                Check(e.Code == "trace-point-masked", "input exception contract changed");
                string guidance = CalibrationGuidance.Describe(e);
                Check(guidance.Contains("同一条边界", StringComparison.Ordinal) && guidance.Contains("不要仅为通过检查取消反光遮罩", StringComparison.Ordinal), "unsafe or missing mask guidance");
            }
        });
        test("actual failed opposite-arm fit produces actionable holdout guidance without weakening confirmation", () =>
        {
            var wrong = Enumerable.Range(0, 32).Select(i => Project(25 + 32d * i / 31, .4 + Math.PI / 2)).ToImmutableArray();
            var bad = session.WithTraces([new("line-1", main), new("validation-180", wrong)]);
            var result = TraceFitter.Fit(g, bad.SnapshotSha256, bad); var review = new TraceFitReview(result, result.Candidates[0].Id, null);
            var issues = CalibrationGuidance.ForTrace(review);
            Check(!review.Selected.EligibleForReview && issues.Any(i => i.Code == "residual-or-holdout-failed"), "failed fit not diagnosed");
            Check(issues.Any(i => i.Action.Contains("不要删除留出点", StringComparison.Ordinal)), "holdout guidance omitted");
            try { review.Confirm(g, bad.SnapshotSha256, bad); throw new Exception("Guidance bypassed evidence gate"); } catch (ArgumentException) { }
        });
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}

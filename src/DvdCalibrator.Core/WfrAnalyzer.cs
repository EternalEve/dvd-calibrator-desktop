using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MathNet.Numerics.Interpolation;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace DvdCalibrator.Core;

/// <summary>Local pyGPA WFR observations, separate angular/radial validation, and full DVD geometry.</summary>
public static class WfrAnalyzer
{
    public const string Algorithm = "half-disc-pygpa-wfr-v1";
    private const double Tau = 2 * Math.PI;
    private sealed record Extraction(WfrEvidence Evidence, DenseObservations Observations, ImmutableArray<TraceDatum> Traces);
    private sealed class Cache { public string? Key; public Extraction? Value; }
    private static readonly ConditionalWeakTable<RgbImage, Cache> Cached = new();
    private static double Wrap(double value) => Math.Atan2(Math.Sin(value), Math.Cos(value));
    private static double ModPi(double value) => .5 * Wrap(2 * value);
    private static double Rms(IEnumerable<double> values) { var a = values.ToArray(); return a.Length == 0 ? 1e9 : Math.Sqrt(a.Average(v => v * v)); }

    public static DenseAnalysis Analyze(IHalfDiscGeneration snapshot, PhotoSession session, RgbImage image, CancellationToken token = default)
    {
        var input = Extract(snapshot, session, image, token); var g = snapshot.Generated;
        var settings = DenseSettings.Default with { RadiusRangeMm = .2 };
        var train = input.Evidence.Points.Where(p => !p.IsHoldout).ToArray();
        if (train.Length < 64 || input.Evidence.Points.Count(p => p.IsHoldout) < 32
            || !CoversHalfCircle(train, input.Evidence.AngularSamples) || input.Observations.UsableRadialFraction < .35)
            return Finish(snapshot, session, settings, input, [], []);

        double lo = input.Observations.Frequencies.Min(f => f.RadiusMm), hi = input.Observations.Frequencies.Max(f => f.RadiusMm);
        double[] radii = Enumerable.Range(0, 65).Select(i => lo + (hi - lo) * i / 64).ToArray();
        var basisModels = new[] { new ForwardModel(g, g with { InnerRadiusMm = g.InnerRadiusMm + 1e-5 }), new ForwardModel(g, g with { OuterRadiusMm = g.OuterRadiusMm + 1e-5 }) };
        var basis = basisModels.Select(m => CubicSpline.InterpolateNaturalSorted(radii, radii.Select(r => m.AtRadius(r).PhaseRadians / 1e-5).ToArray())).ToArray();
        var seeds = new List<double[]>();
        foreach (var ray in train.GroupBy(p => p.Ray))
        {
            if (ray.Count() < 20 || ray.Max(p => p.RadiusMm) - ray.Min(p => p.RadiusMm) < 12) continue;
            var a = Matrix<double>.Build.DenseOfRows(ray.Select(p => new[] { basis[0].Interpolate(p.RadiusMm), basis[1].Interpolate(p.RadiusMm), 1d }));
            var x = a.Svd().Solve(Vector<double>.Build.DenseOfEnumerable(ray.Select(p => p.UnwrappedPhaseRadians)));
            if (x.All(double.IsFinite) && Math.Abs(x[0]) < .18 && Math.Abs(x[1]) < .18) seeds.Add([x[0], x[1]]);
        }
        if (seeds.Count == 0) return Finish(snapshot, session, settings, input, [], []);
        double Median(IEnumerable<double> a) { var x = a.Order().ToArray(); return x[x.Length / 2]; }
        double[] seed = [Median(seeds.Select(s => s[0])), Median(seeds.Select(s => s[1]))];
        var sample = train.Where((_, i) => i % Math.Max(1, train.Length / 700) == 0).ToArray();
        var b0 = sample.Select(p => basis[0].Interpolate(p.RadiusMm)).ToArray(); var b1 = sample.Select(p => basis[1].Interpolate(p.RadiusMm)).ToArray();
        var d0 = sample.Select(p => basis[0].Differentiate(p.RadiusMm)).ToArray(); var d1 = sample.Select(p => basis[1].Differentiate(p.RadiusMm)).ToArray();
        var solutions = new List<(int Sign, double[] X, double Cost)>();
        foreach (int sign in new[] { 1, -1 }) foreach (double cx in new[] { -.15, 0, .15 }) foreach (double cy in new[] { -.15, 0, .15 })
        {
            token.ThrowIfCancellationRequested(); double ri = sign * seed[0], ro = sign * seed[1];
            double beta = .5 * Math.Atan2(sample.Select((p, i) => Math.Sin(2 * (sign * p.PhaseRadians + p.AngleRadians - b0[i] * ri - b1[i] * ro
                - (d0[i] * ri + d1[i] * ro) * (cx * Math.Cos(p.AngleRadians) + cy * Math.Sin(p.AngleRadians))))).Sum(),
                sample.Select((p, i) => Math.Cos(2 * (sign * p.PhaseRadians + p.AngleRadians - b0[i] * ri - b1[i] * ro
                - (d0[i] * ri + d1[i] * ro) * (cx * Math.Cos(p.AngleRadians) + cy * Math.Sin(p.AngleRadians))))).Sum());
            double Cost(Vector<double> x)
            {
                token.ThrowIfCancellationRequested();
                if (Math.Abs(x[0]) > 2 || Math.Abs(x[1]) > 2 || .25 * Math.Sqrt(x[3] * x[3] + x[4] * x[4]) > input.Evidence.CenterShiftLimitMm) return 1e6 + x.DotProduct(x);
                double total = 0, mass = 0;
                for (int i = 0; i < sample.Length; i++)
                {
                    var p = sample[i]; double shift = .25 * (x[3] * Math.Cos(p.AngleRadians) + x[4] * Math.Sin(p.AngleRadians));
                    double error = ModPi(.1 * (b0[i] * x[0] + b1[i] * x[1]) + x[2] + .1 * (d0[i] * x[0] + d1[i] * x[1]) * shift - sign * p.PhaseRadians - p.AngleRadians);
                    double w = Math.Min(20, p.Amplitude); total += w * (Math.Sqrt(1 + error * error / .0625) - 1); mass += w;
                }
                return total / mass;
            }
            try
            {
                var fit = NelderMeadSimplex.Minimum(ObjectiveFunction.Value(Cost), Vector<double>.Build.DenseOfArray([ri * 10, ro * 10, beta, cx * 4, cy * 4]),
                    Vector<double>.Build.DenseOfArray([.002, .002, .1, .05, .05]), 1e-7, 2500);
                solutions.Add((sign, fit.MinimizingPoint.ToArray(), fit.FunctionInfoAtMinimum.Value));
            }
            catch (MaximumIterationsException) { }
        }
        var candidates = ImmutableArray.CreateBuilder<DenseCandidate>(); var centers = ImmutableArray.CreateBuilder<WfrCenter>();
        foreach (var group in solutions.GroupBy(s => s.Sign)) foreach (var solution in group.OrderBy(s => s.Cost).Take(1))
        {
            token.ThrowIfCancellationRequested();
            // Refit and validate against the complete geometry; the linear sensitivity above is only a seed.
            double ExactCost(Vector<double> x)
            {
                token.ThrowIfCancellationRequested();
                if (Math.Abs(x[0]) > 2 || Math.Abs(x[1]) > 2 || .25 * Math.Sqrt(x[3] * x[3] + x[4] * x[4]) > input.Evidence.CenterShiftLimitMm) return 1e6 + x.DotProduct(x);
                var p = g with { InnerRadiusMm = g.InnerRadiusMm + .1 * x[0], OuterRadiusMm = g.OuterRadiusMm + .1 * x[1] };
                var model = new ForwardModel(g, p); double a = Math.Max(p.InnerRadiusMm, lo - .4), b = Math.Min(p.OuterRadiusMm, hi + .4);
                var grid = Enumerable.Range(0, 65).Select(i => a + (b - a) * i / 64).ToArray();
                var curve = CubicSpline.InterpolateNaturalSorted(grid, grid.Select(r => model.AtRadius(r).PhaseRadians).ToArray());
                double total = 0, mass = 0;
                foreach (var q in sample)
                {
                    double r = ShiftedRadius(q, .25 * x[3], .25 * x[4]);
                    if (r <= a || r >= b) return 1e6;
                    double angle = Math.Atan2(q.RadiusMm * Math.Sin(q.AngleRadians) + .25 * x[4], q.RadiusMm * Math.Cos(q.AngleRadians) + .25 * x[3]);
                    double e = ModPi(curve.Interpolate(r) + x[2] - solution.Sign * q.PhaseRadians - angle);
                    double w = Math.Min(20, q.Amplitude); total += w * (Math.Sqrt(1 + e * e / .0625) - 1); mass += w;
                }
                return total / mass;
            }
            try
            {
                var fit = NelderMeadSimplex.Minimum(ObjectiveFunction.Value(ExactCost), Vector<double>.Build.DenseOfArray(solution.X),
                    Vector<double>.Build.DenseOfArray([.0001, .0001, .02, .01, .01]), 1e-7, 1800);
                var x = fit.MinimizingPoint; string id = $"dense-{candidates.Count + 1}";
                var p = g with { InnerRadiusMm = g.InnerRadiusMm + .1 * x[0], OuterRadiusMm = g.OuterRadiusMm + .1 * x[1] };
                var center = new WfrCenter(id, .25 * x[3], .25 * x[4]); centers.Add(center);
                candidates.Add(Score(snapshot, input, settings, id, p, solution.Sign, x[2] - Math.PI / 2, center, token));
            }
            catch (MaximumIterationsException) { }
        }
        return Finish(snapshot, session, settings, input, candidates.ToImmutable(), centers.ToImmutable());
    }

    private static double ShiftedRadius(WfrPoint p, double x, double y) => Math.Sqrt(Math.Pow(p.RadiusMm * Math.Cos(p.AngleRadians) + x, 2) + Math.Pow(p.RadiusMm * Math.Sin(p.AngleRadians) + y, 2));

    private static bool CoversHalfCircle(IEnumerable<WfrPoint> points, int samples)
    {
        var rays = points.Select(p => p.Ray).Distinct().Order().ToArray();
        return rays.Length >= 4 && rays.Zip(rays.Skip(1).Append(rays[0] + samples)).Max(p => p.Second - p.First) <= samples / 2;
    }

    // Densify only for display: keep persisted evidence compact while avoiding long polygon chords through dense rings.
    public static IEnumerable<TraceCurvePoint> DisplayCurve(DenseCandidate candidate)
    {
        var points = candidate.Curve;
        if (points.Length < 2) return points;
        var spline = CubicSpline.InterpolateNaturalSorted(points.Select(p => p.RadiusMm).ToArray(), points.Select(p => p.AngleRadians).ToArray());
        double maximumSlope = points.Zip(points.Skip(1)).Max(q => Math.Abs(q.Second.AngleRadians - q.First.AngleRadians) / (q.Second.RadiusMm - q.First.RadiusMm));
        double lo = points[0].RadiusMm, span = points[^1].RadiusMm - lo;
        int intervals = (int)Math.Clamp(Math.Ceiling(span * maximumSlope / (Math.PI / 64)), points.Length - 1, 65536);
        return Enumerable.Range(0, intervals + 1).Select(i => { double r = lo + span * i / intervals; return new TraceCurvePoint(r, spline.Interpolate(r)); });
    }

    private static Extraction Extract(IHalfDiscGeneration snapshot, PhotoSession session, RgbImage image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); snapshot.Validate(); session.Validate();
        if (!session.IsConfirmed || snapshot.Pattern.Kind != "half-disc" || session.SnapshotSha256 != snapshot.ContentSha256
            || image.PixelsSha256 != session.Photo.OrientedPixelsSha256) throw new ArgumentException("WFR 需要匹配的半盘记录、原图和已确认矫正。");
        string key = RecordJson.Hash(session) + snapshot.ContentSha256; var cache = Cached.GetOrCreateValue(image);
        lock (cache)
        {
            if (cache.Key == key && cache.Value is not null) return cache.Value;
            var legacy = DenseAnalyzer.Extract(snapshot, session, image, DenseSettings.Default, token);
            int rows = legacy.Observations.RadialSamples, split = rows * 3 / 4; const int rays = 128, columns = rays / 2;
            double lo = snapshot.Generated.InnerRadiusMm + .5, step = legacy.Observations.RadialStepMm;
            // Each independently filtered radial section needs both edge guards and interior evidence.
            if (rows < 232)
            {
                cache.Key = key;
                return cache.Value = new(new(0, rays, .01, [], []),
                    new(rows, step, legacy.Observations.SourcePixelsPerMm, 0, [], [], ["insufficient-independent-radial-samples"]), legacy.Traces);
            }
            var h = session.GetTransform(); var frequencies = Enumerable.Range(4, 29).Select(i => i / 256d).ToArray();
            var points = ImmutableArray.CreateBuilder<WfrPoint>(); int channel = 0; double best = -1;
            double[] Sample(int c, int first, int count, int parity)
            {
                var data = new double[count * columns];
                for (int i = 0; i < count; i++) for (int j = 0; j < columns; j++)
                {
                    double a = Tau * (2 * j + parity) / rays, r = lo + (first + i) * step;
                    var p = h.DiscToSource(new(r * Math.Cos(a), r * Math.Sin(a)));
                    var rgb = PhotoRaster.IsMasked(p.X, p.Y, session.Masks) ? null : image.Sample(p.X, p.Y);
                    double v = rgb is null ? double.NaN : c == 0 ? rgb.Value.R : c == 1 ? rgb.Value.G : rgb.Value.B;
                    data[i * columns + j] = v is <= 3 or >= 252 ? double.NaN : v;
                }
                return data;
            }
            bool Reliable(FourierRidgeField field, int i) => field.Amplitude[i] >= 2 && field.Coherence[i] >= .45 && field.Coherence[i] <= 1.3
                && field.Coverage[i] >= .95 && field.Frequency[i] > frequencies[0] && field.Frequency[i] < frequencies[^1];
            FourierRidgeField? chosen = null;
            for (int c = 0; c < 3; c++)
            {
                var field = WindowedFourierRidges.Analyze(Sample(c, 0, split, 0), split, columns, frequencies, token: token);
                double score = Enumerable.Range(27 * columns, (split - 54) * columns).Where(i => Reliable(field, i)).Sum(i => field.Amplitude[i]);
                if (score > best) { best = score; channel = c; chosen = field; }
            }
            void Add(FourierRidgeField field, int first, int count, int parity)
            {
                for (int j = 0; j < columns; j += 2)
                {
                    double previous = field.Phase[j], unwrapped = previous;
                    for (int i = 1; i < count; i++)
                    {
                        int at = i * columns + j; unwrapped += Wrap(field.Phase[at] - previous); previous = field.Phase[at];
                        if (i < 27 || i >= count - 27 || (first + i) % 4 != 0 || !Reliable(field, at)) continue;
                        points.Add(new(first + i, 2 * j + parity, lo + (first + i) * step, Tau * (2 * j + parity) / rays,
                            field.Phase[at], unwrapped, field.Frequency[at], field.Amplitude[at], field.Coherence[at], parity == 1 || first > 0, first > 0));
                    }
                }
            }
            Add(chosen!, 0, split, 0);
            Add(WindowedFourierRidges.Analyze(Sample(channel, 0, split, 1), split, columns, frequencies, token: token), 0, split, 1);
            foreach (int parity in new[] { 0, 1 })
                Add(WindowedFourierRidges.Analyze(Sample(channel, split, rows - split, parity), rows - split, columns, frequencies, token: token), split, rows - split, parity);
            var fragments = points.GroupBy(p => (p.Ray, p.IsRadialHoldout)).Where(q => q.Count() >= 6)
                .Select((q, i) => new DenseFragment($"wfr-{i}", channel, q.Select(p => new DensePhasePoint(p.Row, p.RadiusMm, p.UnwrappedPhaseRadians, p.IsHoldout)).ToImmutableArray())).ToImmutableArray();
            var spectral = points.GroupBy(p => p.Row).Select(q => new LocalFrequency(q.First().RadiusMm, q.Average(p => p.FrequencyCyclesPerPixel) / step,
                q.Average(p => p.FrequencyCyclesPerPixel) / step, q.Average(p => p.Coherence), 1 / q.Average(p => p.FrequencyCyclesPerPixel))).ToImmutableArray();
            double fraction = points.Select(p => p.Row).Distinct().Count() * 4d / rows;
            double limit = Math.Clamp(2.5 * Math.Max(session.Rectification!.OuterRmsPixels, session.Rectification.HoleRmsPixels) / legacy.Observations.SourcePixelsPerMm, .01, .35);
            var evidence = new WfrEvidence(channel, rays, limit, points.ToImmutable(), []);
            var observation = new DenseObservations(rows, step, legacy.Observations.SourcePixelsPerMm, Math.Min(1, fraction), fragments, spectral,
                ["local-wfr-training-and-validation-filtered-separately", "photo-center-offset-fitted-within-contour-error"]);
            cache.Key = key; return cache.Value = new(evidence, observation, legacy.Traces);
        }
    }

    private static DenseCandidate Score(IHalfDiscGeneration snapshot, Extraction input, DenseSettings settings, string id,
        GeometryParameters p, int sign, double rotation, WfrCenter center, CancellationToken token)
    {
        var model = new ForwardModel(snapshot.Generated, p); var train = new List<double>(); var hold = new List<double>();
        var trainNormal = new List<double>(); var holdNormal = new List<double>(); var radial = new List<double>(); var angular = new List<double>();
        foreach (var q in input.Evidence.Points)
        {
            token.ThrowIfCancellationRequested(); double r = ShiftedRadius(q, center.XMm, center.YMm);
            if (r <= p.InnerRadiusMm || r >= p.OuterRadiusMm) continue;
            double a = Math.Atan2(q.RadiusMm * Math.Sin(q.AngleRadians) + center.YMm, q.RadiusMm * Math.Cos(q.AngleRadians) + center.XMm);
            double e = ModPi(model.AtRadius(r).PhaseRadians + rotation + Math.PI / 2 - sign * q.PhaseRadians - a);
            double low = Math.Max(p.InnerRadiusMm, r - .005), high = Math.Min(p.OuterRadiusMm, r + .005);
            double slope = (model.AtRadius(high).PhaseRadians - model.AtRadius(low).PhaseRadians) / (high - low);
            double normal = e * r / Math.Sqrt(1 + r * r * slope * slope);
            (q.IsHoldout ? hold : train).Add(e); (q.IsHoldout ? holdNormal : trainNormal).Add(normal);
            if (q.IsRadialHoldout) radial.Add(normal); else if (q.IsHoldout) angular.Add(normal);
        }
        var reasons = ImmutableArray.CreateBuilder<string>();
        var traceErrors = new List<double>();
        foreach (var group in input.Traces.GroupBy(t => t.TraceId))
        {
            var errors = new List<(double Error, double Scale)>();
            foreach (var t in group)
            {
                double x = t.RadiusMm * Math.Cos(t.AngleRadians) + center.XMm, y = t.RadiusMm * Math.Sin(t.AngleRadians) + center.YMm;
                double r = Math.Sqrt(x * x + y * y);
                if (r <= p.InnerRadiusMm || r >= p.OuterRadiusMm) { reasons.Add("mixed-trace-inconsistent"); continue; }
                double a = t.AngleRadians + Wrap(Math.Atan2(y, x) - t.AngleRadians);
                errors.Add((model.AtRadius(r).PhaseRadians + rotation + t.SourceAngleOffsetRadians - a, t.NormalScaleMm));
            }
            if (errors.Count == 0) continue;
            double turns = Tau * Math.Round(errors.Average(e => e.Error) / Tau);
            traceErrors.AddRange(errors.Select(e => (e.Error - turns) * e.Scale));
        }
        if (traceErrors.Count > 0 && (Rms(traceErrors) > .35 || traceErrors.Max(Math.Abs) > .8)) reasons.Add("mixed-trace-inconsistent");
        if (train.Count < 64 || radial.Count < 16 || angular.Count < 32 || input.Observations.UsableRadialFraction < .35
            || !CoversHalfCircle(input.Evidence.Points.Where(q => !q.IsHoldout), input.Evidence.AngularSamples)) reasons.Add("insufficient-independent-observations");
        if (Rms(train) > .65 || Rms(hold) > .8 || Rms(trainNormal) > .15 || Rms(radial) > .2 || Rms(angular) > .2) reasons.Add("wfr-holdout-failed");
        bool boundary = Math.Abs(p.InnerRadiusMm - snapshot.Generated.InnerRadiusMm) >= settings.RadiusRangeMm * .99 || Math.Abs(p.OuterRadiusMm - snapshot.Generated.OuterRadiusMm) >= settings.RadiusRangeMm * .99;
        if (boundary) reasons.Add("search-boundary");
        double lo = Math.Max(p.InnerRadiusMm, input.Observations.Frequencies.Min(f => f.RadiusMm));
        double hi = Math.Min(p.OuterRadiusMm, input.Observations.Frequencies.Max(f => f.RadiusMm));
        var curve = Enumerable.Range(0, 1025).Select(i => { double r = lo + (hi - lo) * i / 1024; return new TraceCurvePoint(r, model.AtRadius(r).PhaseRadians + rotation); }).ToImmutableArray();
        double slopeMaximum = curve.Zip(curve.Skip(1)).Max(q => Math.Abs(q.Second.AngleRadians - q.First.AngleRadians) / (q.Second.RadiusMm - q.First.RadiusMm));
        double pixelsPerCycle = slopeMaximum < 1e-12 ? 1e9 : input.Observations.SourcePixelsPerMm * Tau / slopeMaximum;
        if (pixelsPerCycle < 8) reasons.Add("predicted-source-undersampled");
        return new(id, p, sign, rotation, Rms(train), Rms(hold), Rms(trainNormal), Rms(holdNormal), traceErrors.Count == 0 ? 0 : Rms(traceErrors), boundary, pixelsPerCycle, reasons.Count == 0,
            reasons.ToImmutable(), [], curve);
    }

    private static DenseAnalysis Finish(IHalfDiscGeneration snapshot, PhotoSession session, DenseSettings settings, Extraction input,
        ImmutableArray<DenseCandidate> candidates, ImmutableArray<WfrCenter> centers)
    {
        if (candidates.Length > 1)
        {
            double best = candidates.Min(c => c.TrainingPhaseRmsRadians);
            candidates = candidates.OrderBy(c => c.TrainingPhaseRmsRadians).Select(c => c.TrainingPhaseRmsRadians > best * 1.5 && c.TrainingPhaseRmsRadians > best + .12
                ? c with { Supported = false, RejectionReasons = c.RejectionReasons.Add("wfr-competing-phase-direction") } : c).ToImmutableArray();
        }
        var legacy = new DenseAnalyzer.Input(snapshot, session, settings, input.Observations, new double[0, 0, 0], []);
        return DenseAnalyzer.Finish(legacy, candidates) with { AlgorithmVersion = Algorithm, Wfr = input.Evidence with { Centers = centers } };
    }

    public static void Verify(DenseAnalysis result, IHalfDiscGeneration snapshot, PhotoSession session, RgbImage image, CancellationToken token = default)
    {
        var input = Extract(snapshot, session, image, token); var evidence = result.Wfr ?? throw new ArgumentException("缺少 WFR 观测。");
        if (result.SchemaVersion != 1 || result.RecordType != "dense-analysis" || result.PhysicalValidation != "pending" || result.SnapshotSha256 != snapshot.ContentSha256
            || result.Settings != (DenseSettings.Default with { RadiusRangeMm = .2 }) || result.Candidates.IsDefault
            || result.SessionSha256 != RecordJson.Hash(session) || result.Candidates.Length > 2 || evidence.Centers.Length != result.Candidates.Length
            || result.Candidates.Select(c => c.Id).Distinct().Count() != result.Candidates.Length || result.Candidates.Any(c => c.Id is not ("dense-1" or "dense-2"))
            || RecordJson.Hash(input.Evidence with { Centers = evidence.Centers }) != RecordJson.Hash(evidence)) throw new ArgumentException("WFR 输入或观测已改变。");
        var scored = result.Candidates.Select(c =>
        {
            c.Parameters.Validate(); var center = evidence.Centers.Single(x => x.CandidateId == c.Id);
            if (c.Polarity is not (-1 or 1) || !double.IsFinite(c.RotationRadians) || c.Parameters != (snapshot.Generated with { InnerRadiusMm = c.Parameters.InnerRadiusMm, OuterRadiusMm = c.Parameters.OuterRadiusMm })
                || !double.IsFinite(center.XMm + center.YMm) || Math.Sqrt(center.XMm * center.XMm + center.YMm * center.YMm) > evidence.CenterShiftLimitMm
                || Math.Abs(c.Parameters.InnerRadiusMm - snapshot.Generated.InnerRadiusMm) > .2 || Math.Abs(c.Parameters.OuterRadiusMm - snapshot.Generated.OuterRadiusMm) > .2)
                throw new ArgumentException("WFR 候选或照片中心偏移无效。");
            return Score(snapshot, input, result.Settings, c.Id, c.Parameters, c.Polarity, c.RotationRadians, center, token);
        }).ToImmutableArray();
        var rebuilt = Finish(snapshot, session, result.Settings, input, scored, evidence.Centers);
        if (RecordJson.Hash(rebuilt) != RecordJson.Hash(result)) throw new ArgumentException("WFR 评分或诊断已改变。");
    }
}

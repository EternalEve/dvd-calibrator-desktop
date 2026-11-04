using System.Collections.Immutable;

namespace DvdCalibrator.Core;

/// <summary>Known analytic half-disc only. Angular harmonics resolve phase sign; each radial fragment keeps one turn branch.</summary>
public static class DenseAnalyzer
{
    internal sealed record Input(IHalfDiscGeneration Snapshot, PhotoSession Session, DenseSettings Settings,
        DenseObservations Observations, double[,,] Samples, ImmutableArray<TraceDatum> Traces);
    private const double Tau = 2 * Math.PI;

    public static DenseAnalysis Analyze(IHalfDiscGeneration snapshot, PhotoSession session, RgbImage image,
        DenseSettings? settings = null, CancellationToken token = default)
    {
        var input = Extract(snapshot, session, image, settings ?? DenseSettings.Default, token);
        if (input.Observations.Fragments.Sum(f => f.Points.Count(p => !p.IsHoldout)) < 24
            || input.Observations.UsableRadialFraction < .35 || input.Observations.Frequencies.Length < 3)
            return Finish(input, []);
        var g = snapshot.Generated; var original = new TrackGeometry(g);
        var points = input.Observations.Fragments.SelectMany(f => f.Points.Where(p => !p.IsHoldout)
            .Zip(f.Points.Where(p => !p.IsHoldout).Skip(1), (a, b) => (a.RadiusMm, b.RadiusMm, Change: b.PhaseRadians - a.PhaseRadians))).ToArray();
        // Differences never cross missing fragments or the contiguous radial holdout boundary.
        var derivative = new double[points.Length, 2]; double epsilon = 1e-5;
        for (int c = 0; c < 2; c++)
        {
            double difference = c == 1 && g.OuterRadiusMm + epsilon > 60 ? -epsilon : epsilon;
            var p = c == 0 ? g with { InnerRadiusMm = g.InnerRadiusMm + difference } : g with { OuterRadiusMm = g.OuterRadiusMm + difference };
            var model = new ForwardModel(original, new TrackGeometry(p));
            for (int i = 0; i < points.Length; i++) derivative[i, c] =
                (model.AtRadius(points[i].Item2).PhaseRadians - model.AtRadius(points[i].Item1).PhaseRadians) / difference;
        }
        var normal = new double[2, 2]; var rhs = new double[2];
        for (int i = 0; i < points.Length; i++) for (int a = 0; a < 2; a++)
        {
            rhs[a] += derivative[i, a] * points[i].Change;
            for (int b = 0; b < 2; b++) normal[a, b] += derivative[i, a] * derivative[i, b];
        }
        var seed = Solve2(normal, rhs) ?? [0, 0];
        double range = input.Settings.RadiusRangeMm;
        var starts = new List<double[]> { new double[2], seed.Select(v => Math.Clamp(v / range, -.99, .99)).ToArray() };
        foreach (var (a, b) in new[] { (-.8, -.8), (-.8, .8), (.8, -.8), (.8, .8) }) starts.Add([a, b]);
        var solutions = new List<(double[] X, double Rotation, double Cost)>();
        double[]? Evaluate(double[] x, out double rotation)
        {
            token.ThrowIfCancellationRequested(); rotation = 0;
            try
            {
                var p = g with { InnerRadiusMm = g.InnerRadiusMm + x[0] * range, OuterRadiusMm = g.OuterRadiusMm + x[1] * range };
                if (input.Traces.Any(t => t.RadiusMm < p.InnerRadiusMm || t.RadiusMm > p.OuterRadiusMm)) return null;
                var model = new ForwardModel(original, new TrackGeometry(p));
                var groups = input.Observations.Fragments.Select(f => f.Points.Where(v => !v.IsHoldout).ToArray()).ToArray();
                var differences = groups.SelectMany(f => f.Select(v => v.PhaseRadians - model.AtRadius(v.RadiusMm).PhaseRadians)).ToArray();
                rotation = Math.Atan2(differences.Sum(Math.Sin), differences.Sum(Math.Cos));
                double beta = rotation;
                var residuals = new List<double>();
                foreach (var group in groups)
                {
                    double turns = Math.Round(group.Average(v => (model.AtRadius(v.RadiusMm).PhaseRadians + beta - v.PhaseRadians) / Tau));
                    foreach (var v in group) residuals.Add(model.AtRadius(v.RadiusMm).PhaseRadians + rotation - v.PhaseRadians - Tau * turns);
                }
                // Partial continuous traces keep their boundary identity and one branch for the whole line.
                foreach (var group in input.Traces.Where(t => !t.IsHoldout).GroupBy(t => t.TraceId))
                {
                    double turns = Math.Round(group.Average(v => (model.AtRadius(v.RadiusMm).PhaseRadians + beta + v.SourceAngleOffsetRadians - v.AngleRadians) / Tau));
                    foreach (var v in group) residuals.Add((model.AtRadius(v.RadiusMm).PhaseRadians + rotation + v.SourceAngleOffsetRadians - v.AngleRadians - Tau * turns) * .5);
                }
                return residuals.ToArray();
            }
            catch (ArgumentException) { return null; }
        }
        static double Cost(double[] residuals) => residuals.Average(e => Math.Abs(e) < .2 ? e * e : .4 * Math.Abs(e) - .04);
        foreach (var start in starts)
        {
            double[] x = (double[])start.Clone(); var current = Evaluate(x, out double rotation); if (current is null) continue;
            double cost = Cost(current), lambda = 1e-4;
            for (int iteration = 0; iteration < 45; iteration++)
            {
                var jac = new double[current.Length, 2]; bool valid = true;
                for (int c = 0; c < 2; c++)
                {
                    double[] plus = (double[])x.Clone(), minus = (double[])x.Clone();
                    plus[c] = Math.Min(1, x[c] + 1e-4); minus[c] = Math.Max(-1, x[c] - 1e-4);
                    var pa = Evaluate(plus, out _); var pb = Evaluate(minus, out _);
                    if (pa is null || pb is null) { valid = false; break; }
                    for (int i = 0; i < current.Length; i++) jac[i, c] = (pa[i] - pb[i]) / (plus[c] - minus[c]);
                }
                if (!valid) break;
                var matrix = new double[2, 2]; var right = new double[2];
                for (int i = 0; i < current.Length; i++)
                {
                    double w = Math.Abs(current[i]) <= .2 ? 1 : .2 / Math.Abs(current[i]);
                    for (int a = 0; a < 2; a++)
                    {
                        right[a] -= w * jac[i, a] * current[i];
                        for (int b = 0; b < 2; b++) matrix[a, b] += w * jac[i, a] * jac[i, b];
                    }
                }
                for (int a = 0; a < 2; a++) matrix[a, a] += lambda * Math.Max(matrix[a, a], 1e-8);
                var step = Solve2(matrix, right); if (step is null) break;
                double[] nextX = x.Select((v, i) => Math.Clamp(v + step[i], -1, 1)).ToArray();
                var next = Evaluate(nextX, out double nextRotation);
                if (next is not null && Cost(next) < cost)
                {
                    double improvement = cost - Cost(next); x = nextX; current = next; rotation = nextRotation; cost = Cost(next); lambda *= .25;
                    if (improvement < 1e-11) break;
                }
                else { lambda *= 10; if (lambda > 1e8) break; }
            }
            if (!solutions.Any(s => s.X.Zip(x).Max(p => Math.Abs(p.First - p.Second)) < 1e-4)) solutions.Add((x, rotation, cost));
        }
        var candidates = ImmutableArray.CreateBuilder<DenseCandidate>();
        foreach (var solution in solutions.OrderBy(s => s.Cost).Take(4))
        {
            var p = g with { InnerRadiusMm = g.InnerRadiusMm + solution.X[0] * range, OuterRadiusMm = g.OuterRadiusMm + solution.X[1] * range };
            // Unknown global photographic polarity is a real pi ambiguity. Never choose per-pixel polarity.
            foreach (int polarity in new[] { 1, -1 }) candidates.Add(Score(input, $"dense-{candidates.Count + 1}", p,
                polarity, solution.Rotation + (polarity < 0 ? Math.PI : 0), token));
        }
        return Finish(input, candidates.OrderBy(c => c.TrainingImageError + c.TrainingPhaseRmsRadians).ToImmutableArray());
    }

    internal static (DiscPhotoTransform Transform, double InnerRadius, int Count, double Step, double PixelsPerMm)
        PrepareSampling(IHalfDiscGeneration snapshot, PhotoSession session, RgbImage image, DenseSettings settings, CancellationToken token)
    {
        snapshot.Validate(); session.Validate(); settings.Validate(); token.ThrowIfCancellationRequested();
        if (snapshot.Pattern.Kind != "half-disc" || session.SnapshotId != snapshot.Id || session.SnapshotSha256 != snapshot.ContentSha256
            || !session.IsConfirmed || image.Width != session.Photo.OrientedWidth || image.Height != session.Photo.OrientedHeight
            || image.PixelsSha256 != session.Photo.OrientedPixelsSha256)
            throw new ArgumentException("密环入口要求已确认矫正、匹配原图与已知解析 half-disc 冻结快照；不能推测外部图片的靶型。");
        var transform = session.GetTransform(); var g = snapshot.Generated;
        double margin = Math.Max(.5, settings.RadiusRangeMm + .05), lo = g.InnerRadiusMm + margin, hi = g.OuterRadiusMm - margin;
        if (hi - lo < 10) throw new ArgumentException("密环分析需要至少 10 mm 的安全径向范围。");
        double pixelsPerMm = double.PositiveInfinity;
        for (int i = 0; i < settings.AngularSamples; i++) for (int j = 0; j < 3; j++)
        {
            double a = Tau * i / settings.AngularSamples, r = lo + (hi - lo) * j / 2;
            var p = transform.DiscToSource(new(r * Math.Cos(a), r * Math.Sin(a)));
            var q = transform.DiscToSource(new((r + .1) * Math.Cos(a), (r + .1) * Math.Sin(a)));
            pixelsPerMm = Math.Min(pixelsPerMm, Math.Sqrt(Math.Pow(q.X - p.X, 2) + Math.Pow(q.Y - p.Y, 2)) / .1);
        }
        // At most one radial sample per effective source pixel; oversampling is not extra evidence.
        int count = Math.Min(1024, (int)Math.Floor((hi - lo) * pixelsPerMm) + 1);
        if (count < 64) throw new ArgumentException("原图径向有效分辨率不足 64 像素，请补拍清晰大图。");
        return (transform, lo, count, (hi - lo) / (count - 1), pixelsPerMm);
    }

    internal static Input Extract(IHalfDiscGeneration snapshot, PhotoSession session, RgbImage image, DenseSettings settings, CancellationToken token)
    {
        var (transform, lo, count, step, pixelsPerMm) = PrepareSampling(snapshot, session, image, settings, token);
        int rays = settings.AngularSamples;
        var samples = new double[count, rays, 3];
        var phases = new double?[count]; var channels = new int[count];
        var channelPhases = new double[count, 3]; var amplitudes = new double[count, 3];
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested(); double r = lo + i * step;
            for (int j = 0; j < rays; j++)
            {
                double a = Tau * j / rays; var p = transform.DiscToSource(new(r * Math.Cos(a), r * Math.Sin(a)));
                var color = PhotoRaster.IsMasked(p.X, p.Y, session.Masks) ? null : image.Sample(p.X, p.Y);
                for (int c = 0; c < 3; c++)
                {
                    double v = color is null ? double.NaN : c == 0 ? color.Value.R : c == 1 ? color.Value.G : color.Value.B;
                    samples[i, j, c] = v is <= 3 or >= 252 ? double.NaN : v;
                }
            }
            // Only even rays extract phase/channel. Odd rays are untouched image holdout.
            for (int c = 0; c < 3; c++)
            {
                var matrix = new double[3, 3]; var rhs = new double[3]; int used = 0;
                for (int j = 0; j < rays; j += 2)
                {
                    double value = samples[i, j, c]; if (!double.IsFinite(value)) continue;
                    double a = Tau * j / rays; double[] row = [1, Math.Cos(a), Math.Sin(a)]; used++;
                    for (int u = 0; u < 3; u++) { rhs[u] += row[u] * value; for (int v = 0; v < 3; v++) matrix[u, v] += row[u] * row[v]; }
                }
                var fit = Solve3(matrix, rhs); if (used < rays / 3 || fit is null) continue;
                double amplitude = Math.Sqrt(fit[1] * fit[1] + fit[2] * fit[2]);
                amplitudes[i, c] = amplitude; channelPhases[i, c] = Math.Atan2(-fit[1], fit[2]);
            }
        }
        // Choose a single channel from training radii/rays, then freeze it for all validation pixels.
        int selectedChannel = Enumerable.Range(0, 3).MaxBy(c => Enumerable.Range(0, count * 3 / 4).Sum(i => amplitudes[i, c]));
        for (int i = 0; i < count; i++)
        {
            channels[i] = selectedChannel;
            if (amplitudes[i, selectedChannel] >= settings.MinimumContrast) phases[i] = channelPhases[i, selectedChannel];
        }
        var slow = new bool[count];
        for (int i = 4; i + 4 < count; i++)
        {
            if (Enumerable.Range(i - 4, 9).Any(j => phases[j] is null)) continue;
            double change = 0;
            for (int j = i - 3; j <= i + 4; j++) change += Wrap(phases[j]!.Value - phases[j - 1]!.Value);
            slow[i] = Math.Abs(change / (8 * step)) < .03 * Tau;
        }
        for (int i = 0; i < count; i++) if (slow[i]) phases[i] = null;
        var fragments = ImmutableArray.CreateBuilder<DenseFragment>();
        var frequencies = ImmutableArray.CreateBuilder<LocalFrequency>();
        // Retain contiguous phase only. Low contrast, channel changes, alias risk and derivative reversals cut fragments.
        var current = new List<DensePhasePoint>(); int channel = -1; double previousSlope = 0;
        void Store()
        {
            if (current.Count >= 12)
            {
                int train = (int)(current.Count * .75);
                var points = current.Select((p, j) => p with { IsHoldout = j >= train || p.IsHoldout }).ToImmutableArray();
                if (points.Count(p => !p.IsHoldout) >= 6 && points.Count(p => p.IsHoldout) >= 3)
                    fragments.Add(new($"fragment-{fragments.Count + 1}", channel, points));
            }
            current.Clear(); previousSlope = 0;
        }
        for (int i = 0; i < count; i++)
        {
            if (phases[i] is not { } angle) { Store(); continue; }
            if (current.Count > 0)
            {
                double change = Wrap(angle - current[^1].PhaseRadians);
                if (channel != channels[i] || Math.Abs(change) > Math.PI / 2
                    || (previousSlope * change < 0 && Math.Abs(previousSlope) > .15 && Math.Abs(change) > .15)) Store();
                else { angle = current[^1].PhaseRadians + change; if (Math.Abs(change) > .08) previousSlope = change; }
            }
            channel = channels[i]; current.Add(new(i, lo + step * i, angle, i >= (int)(count * .75)));
        }
        Store();
        foreach (var fragment in fragments)
        {
            var p = fragment.Points;
            int halfWindow = Math.Min(24, (p.Length - 1) / 2);
            for (int center = halfWindow; center + halfWindow < p.Length; center += 8)
            {
                var window = p.Skip(center - halfWindow).Take(2 * halfWindow + 1).ToArray();
                double span = window[^1].RadiusMm - window[0].RadiusMm;
                double meanPhase = window.Average(v => v.PhaseRadians), meanR = window.Average(v => v.RadiusMm);
                double slope = window.Sum(v => (v.RadiusMm - meanR) * (v.PhaseRadians - meanPhase)) / window.Sum(v => Math.Pow(v.RadiusMm - meanR, 2));
                double bestPower = 0, totalPower = 0, bestFrequency = 0;
                // Local windowed spectra, averaged over training rays. Never a whole-disc single FFT.
                for (int k = 1; k <= 64; k++)
                {
                    double frequency = k / (8 * span), power = 0;
                    for (int ray = 0; ray < rays; ray += 2)
                    {
                        var values = window.Select(v => samples[v.RadialIndex, ray, fragment.Channel]).ToArray();
                        if (values.Any(v => !double.IsFinite(v))) continue;
                        double mean = values.Average(), re = 0, im = 0;
                        for (int j = 0; j < window.Length; j++)
                        {
                            double v = (values[j] - mean) * (.5 - .5 * Math.Cos(Tau * j / (window.Length - 1)));
                            double a = Tau * frequency * (window[j].RadiusMm - window[0].RadiusMm); re += v * Math.Cos(a); im += v * Math.Sin(a);
                        }
                        power += re * re + im * im;
                    }
                    totalPower += power; if (power > bestPower) { bestPower = power; bestFrequency = frequency; }
                }
                if (bestPower > 0 && Math.Abs(slope) / Tau >= .03)
                    frequencies.Add(new(p[center].RadiusMm, bestFrequency, slope / Tau,
                        bestPower / totalPower, pixelsPerMm / Math.Max(bestFrequency, Math.Abs(slope) / Tau)));
            }
        }
        var warnings = ImmutableArray.CreateBuilder<string>();
        if (phases.Any(p => p is null)) warnings.Add("missing-low-contrast-saturated-or-masked-radii-not-bridged");
        if (frequencies.Any(f => f.EffectivePixelsPerCycle < 8)) warnings.Add("source-sampling-below-eight-pixels-per-cycle");
        if (fragments.Any(f => f.Points.Max(p => p.PhaseRadians) - f.Points.Min(p => p.PhaseRadians) < Math.PI)) warnings.Add("sparse-fragment-use-traces-or-more-visible-rings");
        var observations = new DenseObservations(count, step, pixelsPerMm, fragments.Sum(f => f.Points.Length) / (double)count,
            fragments.ToImmutable(), frequencies.ToImmutable(), warnings.ToImmutable());
        return new(snapshot, session, settings, observations, samples, PrepareTraces(session, transform));
    }

    internal static DenseCandidate Score(Input input, string id, GeometryParameters parameters, int polarity, double rotation, CancellationToken token)
    {
        var model = new ForwardModel(input.Snapshot.Generated, parameters);
        var branches = ImmutableArray.CreateBuilder<FragmentBranch>(); var trainPhase = new List<double>(); var holdPhase = new List<double>();
        var trainImage = new List<double>(); var holdImage = new List<double>(); var reasons = ImmutableArray.CreateBuilder<string>();
        double polarityPhase = polarity < 0 ? Math.PI : 0; int rays = input.Settings.AngularSamples;
        foreach (var fragment in input.Observations.Fragments)
        {
            token.ThrowIfCancellationRequested(); var training = fragment.Points.Where(p => !p.IsHoldout).ToArray();
            if (training.Length == 0) { reasons.Add("fragment-without-training"); continue; }
            int turns = checked((int)Math.Round(training.Average(p => (model.AtRadius(p.RadiusMm).PhaseRadians + rotation - polarityPhase - p.PhaseRadians) / Tau)));
            var fitPixels = new List<(double Pattern, double Value)>();
            foreach (var point in training)
            {
                var forward = model.AtRadius(point.RadiusMm);
                for (int ray = 0; ray < rays; ray += 2)
                {
                    double value = input.Samples[point.RadialIndex, ray, fragment.Channel]; if (!double.IsFinite(value)) continue;
                    double pattern = input.Snapshot.Pattern.IsWhite(forward.SourceRadiusMm, Tau * ray / rays - forward.PhaseRadians - rotation) ? 1 : 0;
                    fitPixels.Add((pattern, value));
                }
            }
            var dark = fitPixels.Where(p => p.Pattern == 0).Select(p => p.Value).ToArray();
            var light = fitPixels.Where(p => p.Pattern == 1).Select(p => p.Value).ToArray();
            if (dark.Length < 12 || light.Length < 12) { reasons.Add("image-class-coverage-insufficient"); continue; }
            double offset = dark.Average(), gain = light.Average() - offset;
            if (gain * polarity < input.Settings.MinimumContrast) reasons.Add("image-polarity-or-contrast-failed");
            var localTrain = new List<double>(); var localHold = new List<double>();
            foreach (var point in fragment.Points)
            {
                var forward = model.AtRadius(point.RadiusMm);
                double e = forward.PhaseRadians + rotation - polarityPhase - point.PhaseRadians - Tau * turns;
                (point.IsHoldout ? holdPhase : trainPhase).Add(e); (point.IsHoldout ? localHold : localTrain).Add(e);
                for (int ray = 0; ray < rays; ray++)
                {
                    double value = input.Samples[point.RadialIndex, ray, fragment.Channel]; if (!double.IsFinite(value)) continue;
                    double pattern = input.Snapshot.Pattern.IsWhite(forward.SourceRadiusMm, Tau * ray / rays - forward.PhaseRadians - rotation) ? 1 : 0;
                    double error = (value - offset - gain * pattern) / Math.Max(Math.Abs(gain), input.Settings.MinimumContrast);
                    (point.IsHoldout || ray % 2 == 1 ? holdImage : trainImage).Add(error);
                }
            }
            branches.Add(new(fragment.Id, turns, Rms(localTrain), Rms(localHold), offset, gain));
        }
        var traceErrors = new List<double>();
        foreach (var group in input.Traces.GroupBy(t => t.TraceId))
        {
            var branchPoints = group.Where(p => !p.IsHoldout).ToArray(); if (branchPoints.Length == 0) branchPoints = group.ToArray();
            double turns = Math.Round(branchPoints.Average(p => (model.AtRadius(p.RadiusMm).PhaseRadians + rotation + p.SourceAngleOffsetRadians - p.AngleRadians) / Tau));
            traceErrors.AddRange(group.Select(p => (model.AtRadius(p.RadiusMm).PhaseRadians + rotation + p.SourceAngleOffsetRadians - p.AngleRadians - Tau * turns) * p.NormalScaleMm));
        }
        bool boundary = Math.Abs(parameters.InnerRadiusMm - input.Snapshot.Generated.InnerRadiusMm) >= input.Settings.RadiusRangeMm * .995
            || Math.Abs(parameters.OuterRadiusMm - input.Snapshot.Generated.OuterRadiusMm) >= input.Settings.RadiusRangeMm * .995;
        if (boundary) reasons.Add("search-boundary");
        if (trainPhase.Count < 24 || holdPhase.Count < 6 || holdImage.Count < 100) reasons.Add("insufficient-independent-observations");
        if (Rms(trainPhase) > input.Settings.MaximumPhaseErrorRadians || Rms(holdPhase) > input.Settings.MaximumPhaseErrorRadians) reasons.Add("phase-or-radial-holdout-failed");
        if (Rms(trainImage) > input.Settings.MaximumImageError || Rms(holdImage) > input.Settings.MaximumImageError) reasons.Add("raw-image-or-angular-holdout-failed");
        if (traceErrors.Count > 0 && (Rms(traceErrors) > .35 || traceErrors.Max(Math.Abs) > .8)) reasons.Add("mixed-trace-inconsistent");
        if (input.Observations.Frequencies.Any(f => f.EffectivePixelsPerCycle < 8)) reasons.Add("source-undersampled");
        double lo = input.Observations.Fragments.Min(f => f.Points[0].RadiusMm), hi = input.Observations.Fragments.Max(f => f.Points[^1].RadiusMm);
        var curve = Curve(model, lo, hi, rotation);
        double maximumSlope = curve.Zip(curve.Skip(1)).Max(p => Math.Abs(p.Second.AngleRadians - p.First.AngleRadians) / (p.Second.RadiusMm - p.First.RadiusMm));
        double predictedPixels = maximumSlope > 1e-12 ? input.Observations.SourcePixelsPerMm * Tau / maximumSlope : 1e9;
        if (predictedPixels < 8) reasons.Add("predicted-source-undersampled");
        if (curve.Zip(curve.Skip(1)).Any(p => Math.Abs(p.First.AngleRadians - p.Second.AngleRadians) > Math.PI / 8)) reasons.Add("curve-undersampled");
        return new(id, parameters, polarity, rotation, Rms(trainPhase), Rms(holdPhase), Rms(trainImage), Rms(holdImage), Rms(traceErrors),
            boundary, predictedPixels, reasons.Count == 0, reasons.Distinct().ToImmutableArray(), branches.ToImmutable(), curve);
    }

    internal static DenseAnalysis Finish(Input input, ImmutableArray<DenseCandidate> candidates)
    {
        var supported = candidates.Where(c => c.Supported).ToArray(); NextDiscDiagnostic? next = null;
        string status = candidates.Length == 0 ? "photo-insufficient" : supported.Length == 0 ? "model-mismatch" : "dense-review";
        if (supported.Length > 0)
        {
            var selected = supported[0].Parameters;
            double lo = Math.Max(selected.InnerRadiusMm, supported.Max(c => c.Parameters.InnerRadiusMm)) + .5;
            double hi = Math.Min(selected.OuterRadiusMm, supported.Min(c => c.Parameters.OuterRadiusMm)) - .5;
            var curves = supported.Select(c => new NextDiscCurve(c.Id, Curve(new(selected, c.Parameters), lo, hi, 0))).ToImmutableArray();
            double difference = 0, pixelsPerTurn = 1e9, lineWidthPixels = 1e9; var reference = curves[0].Curve;
            var finePattern = new PatternDefinition("next-disc-cross-v1", "radial-cross", .3, "analytic-disc-xy-v1");
            foreach (var c in curves)
            {
                var model = new ForwardModel(selected, supported.First(s => s.Id == c.CandidateId).Parameters);
                double shift = c.Curve[0].AngleRadians - reference[0].AngleRadians;
                for (int i = 0; i < c.Curve.Length; i++)
                {
                    difference = Math.Max(difference, c.Curve[i].RadiusMm * Math.Abs(c.Curve[i].AngleRadians - reference[i].AngleRadians - shift));
                    if (i > 0)
                    {
                        double change = Math.Abs(c.Curve[i].AngleRadians - c.Curve[i - 1].AngleRadians);
                        if (change > 1e-12) pixelsPerTurn = Math.Min(pixelsPerTurn, input.Observations.SourcePixelsPerMm * Tau * (hi - lo) / 1024 / change);
                        double r = c.Curve[i].RadiusMm, slope = change * 1024 / (hi - lo);
                        double width = finePattern.LineWidthMm * r / model.AtRadius(r).SourceRadiusMm / Math.Sqrt(1 + Math.Pow(r * slope, 2));
                        lineWidthPixels = Math.Min(lineWidthPixels, width * input.Observations.SourcePixelsPerMm);
                    }
                }
            }
            double armPixels = pixelsPerTurn / 4;
            bool traceable = armPixels >= 8 && lineWidthPixels >= 2 && difference <= .2;
            string nextStatus = difference > .2 ? "candidate-ambiguous" : traceable ? "next-disc-review" : "next-disc-undersampled";
            next = new(selected, finePattern, lo, hi, difference, pixelsPerTurn, armPixels, lineWidthPixels, traceable, nextStatus, curves);
            if (difference > .2) status = "candidate-ambiguous";
        }
        return new(1, "dense-analysis", DenseAnalysis.Algorithm, input.Snapshot.ContentSha256, RecordJson.Hash(input.Session), input.Settings,
            input.Observations, status, "pending", candidates, next,
            ["finite-local-search-not-exhaustive", "half-disc-global-polarity-pi-ambiguity-retained",
                "fixed-bit-length-equivalent-compensation-only", "unobserved-gaps-not-validated",
                "eight-pixels-per-cycle-is-engineering-screen-not-physical-proof", "next-disc-review-is-not-coarse-ready"]);
    }

    internal static ImmutableArray<TraceDatum> PrepareTraces(PhotoSession session, DiscPhotoTransform transform)
    {
        if (session.Traces.IsEmpty) return [];
        var result = ImmutableArray.CreateBuilder<TraceDatum>();
        foreach (var trace in session.Traces)
        {
            if (trace.Id is not ("line-1" or "validation-180") || trace.Points.Length is < 6 or > 600)
                throw new CalibrationInputException("mixed-trace-point-count", "混合模式仅接受 6–600 点的主边界或已知对侧 180° 边界，可只覆盖稀疏片段。");
            var points = new List<(double R, double A)>();
            foreach (var p in trace.Points)
            {
                if (PhotoRaster.IsMasked(p.X, p.Y, session.Masks)) throw new CalibrationInputException("trace-point-masked", "混合描点位于遮罩内。");
                var d = transform.SourceToDisc(p); double r = Math.Sqrt(d.X * d.X + d.Y * d.Y), a = Math.Atan2(d.Y, d.X);
                if (r is < 7.5 or > 60 || (points.Count > 0 && r - points[^1].R < .01)) throw new CalibrationInputException("trace-radius-order", "混合描点必须严格从内向外排列。");
                if (points.Count > 0)
                {
                    double change = Wrap(a - points[^1].A); if (Math.Abs(change) >= Math.PI / 2) throw new CalibrationInputException("trace-adjacent-angle", "混合描线相邻转角过大，请补点。");
                    a = points[^1].A + change;
                }
                points.Add((r, a));
            }
            int training = trace.Id == "line-1" ? points.Count * 3 / 4 : 0;
            for (int i = 0; i < points.Count; i++)
            {
                int start = i < training ? 0 : training, end = i < training ? training - 1 : points.Count - 1;
                int a = Math.Max(start, i - 1), b = Math.Min(end, i + 1);
                double slope = (points[b].A - points[a].A) / (points[b].R - points[a].R);
                double scale = points[i].R / Math.Sqrt(1 + Math.Pow(points[i].R * slope, 2));
                result.Add(new(trace.Id, i, points[i].R, points[i].A, trace.Id == "validation-180" ? Math.PI : 0, scale, i >= training));
            }
        }
        return result.ToImmutable();
    }
    private static ImmutableArray<TraceCurvePoint> Curve(ForwardModel model, double lo, double hi, double rotation)
        => Enumerable.Range(0, 1025).Select(i => { double r = lo + (hi - lo) * i / 1024; return new TraceCurvePoint(r, model.AtRadius(r).PhaseRadians + rotation); }).ToImmutableArray();
    private static double Wrap(double a) => Math.Atan2(Math.Sin(a), Math.Cos(a));
    private static double Rms(List<double> values) => values.Count == 0 ? 0 : Math.Sqrt(values.Average(v => v * v));
    private static double[]? Solve2(double[,] a, double[] b)
    {
        double det = a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0];
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-20) return null;
        return [(b[0] * a[1, 1] - a[0, 1] * b[1]) / det, (a[0, 0] * b[1] - b[0] * a[1, 0]) / det];
    }
    private static double[]? Solve3(double[,] matrix, double[] rhs)
    {
        var a = (double[,])matrix.Clone(); var b = (double[])rhs.Clone();
        for (int c = 0; c < 3; c++)
        {
            int pivot = c; for (int i = c + 1; i < 3; i++) if (Math.Abs(a[i, c]) > Math.Abs(a[pivot, c])) pivot = i;
            if (Math.Abs(a[pivot, c]) < 1e-8) return null;
            for (int j = c; j < 3; j++) (a[c, j], a[pivot, j]) = (a[pivot, j], a[c, j]); (b[c], b[pivot]) = (b[pivot], b[c]);
            for (int i = c + 1; i < 3; i++) { double f = a[i, c] / a[c, c]; for (int j = c; j < 3; j++) a[i, j] -= f * a[c, j]; b[i] -= f * b[c]; }
        }
        var x = new double[3]; for (int i = 2; i >= 0; i--) { double v = b[i]; for (int j = i + 1; j < 3; j++) v -= a[i, j] * x[j]; x[i] = v / a[i, i]; }
        return x.All(double.IsFinite) ? x : null;
    }
}

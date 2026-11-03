using System.Collections.Immutable;

namespace DvdCalibrator.Core;

/// <summary>Bounded, multi-start continuous-phase fitting. One turn offset per entire trace.</summary>
public static class TraceFitter
{
    private sealed record Evaluation(double[] X, GeometryParameters Parameters, double Rotation, double Cost, double[] Residuals);

    public static ImmutableArray<TraceDatum> Prepare(PhotoSession session, GeometryParameters generated)
    {
        session.Validate(); generated.Validate();
        if (!session.IsConfirmed) throw new ArgumentException("请先核对并确认照片矫正。");
        var primary = session.Traces.FirstOrDefault(t => t.Id == "line-1") ?? throw new ArgumentException("缺少主描线 line-1。");
        if (primary.Points.Length < 24 || primary.Points.Length > 600)
            throw new ArgumentException("主线需要 24–600 点，建议 40 点以上并覆盖内中外；末段 25% 将留出检查。");
        if (session.Traces.Any(t => t.Id is not ("line-1" or "validation-180")))
            throw new ArgumentException("仅接受主线和明确为对侧 180° 的留出线。");
        var transform = session.GetTransform();
        var all = ImmutableArray.CreateBuilder<TraceDatum>();
        foreach (var trace in session.Traces)
        {
            if (trace.Points.Length > 600 || (trace.Id == "validation-180" && trace.Points.Length < 6))
                throw new ArgumentException("独立留出线需要 6–600 个点。");
            var radii = new double[trace.Points.Length]; var angles = new double[trace.Points.Length];
            for (int i = 0; i < radii.Length; i++)
            {
                var source = trace.Points[i];
                if (PhotoRaster.IsMasked(source.X, source.Y, session.Masks))
                    throw new ArgumentException($"{trace.Id} 的第 {i + 1} 点位于遮罩内，请修正点位或遮罩，不能静默删除后继续展开角度。");
                var disc = transform.SourceToDisc(source);
                radii[i] = Math.Sqrt(disc.X * disc.X + disc.Y * disc.Y);
                double angle = Math.Atan2(disc.Y, disc.X);
                if (radii[i] is < 7.5 or > 60 || (i > 0 && radii[i] - radii[i - 1] < .01))
                    throw new ArgumentException("描点必须按真实曲线从内向外排列，半径严格增加，不自动排序或截断点。");
                if (i == 0) angles[i] = angle;
                else
                {
                    double change = Math.Atan2(Math.Sin(angle - angles[i - 1]), Math.Cos(angle - angles[i - 1]));
                    if (Math.Abs(change) >= Math.PI / 2) throw new ArgumentException("相邻描点转角过大，请沿同一条曲线补点以降低漏圈风险。");
                    angles[i] = angles[i - 1] + change;
                }
            }
            if ((radii[^1] - radii[0]) / (generated.OuterRadiusMm - generated.InnerRadiusMm) < .6)
                throw new ArgumentException("描线径向覆盖不足 60%，请补充靠内和靠外的点。");
            int trainingCount = trace.Id == "line-1" ? (int)Math.Floor(radii.Length * .75) : 0;
            for (int i = 0; i < radii.Length; i++)
            {
                // Estimate tangent from observations, never from a candidate that can game its own scale.
                int blockStart = i < trainingCount ? 0 : trainingCount;
                int blockEnd = i < trainingCount ? trainingCount - 1 : radii.Length - 1;
                int lo = Math.Max(blockStart, i - 1), hi = Math.Min(blockEnd, i + 1);
                double slope = (angles[hi] - angles[lo]) / (radii[hi] - radii[lo]);
                double scale = radii[i] / Math.Sqrt(1 + Math.Pow(radii[i] * slope, 2));
                all.Add(new(trace.Id, i, radii[i], angles[i], trace.Id == "validation-180" ? Math.PI : 0, scale, i >= trainingCount));
            }
        }
        return all.ToImmutable();
    }

    public static TraceFitResult Fit(GeometryParameters generated, string snapshotHash, PhotoSession session,
        TraceFitSettings? settings = null, CancellationToken token = default)
    {
        settings ??= TraceFitSettings.Default; settings.Validate(); token.ThrowIfCancellationRequested();
        if (snapshotHash != session.SnapshotSha256) throw new ArgumentException("会话不属于当前生成快照。");
        var data = Prepare(session, generated); var training = data.Where(p => !p.IsHoldout).ToArray();
        if (settings.ShapeOrder > 0 && session.Traces.First(t => t.Id == "line-1").Points.Length < 40)
            throw new ArgumentException("轨距形状拟合至少需要 40 个主线点。");
        var original = new TrackGeometry(generated);
        double range = settings.RadiusRangeMm;
        List<TraceFitCandidate> candidates;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            candidates = Solve(0, range);
            if (candidates.Count == 0)
            {
                if (range < .5) { range = range < .1 ? .1 : .5; continue; }
                throw new ArgumentException("搜索范围内没有覆盖全部观测的有效模型；请检查照片尺度、生成记录和点位。");
            }
            if (!candidates[0].BoundaryLimited || range >= .5) break;
            range = range < .1 ? .1 : .5;
        }
        var baseline = candidates[0];
        if (settings.ShapeOrder > 0)
        {
            foreach (var shape in Solve(settings.ShapeOrder, range, baseline.Parameters))
            {
                var reasons = shape.RejectionReasons.ToBuilder();
                if (!(shape.HoldoutRmsMm < baseline.HoldoutRmsMm * .8 && baseline.HoldoutRmsMm > .005))
                    reasons.Add("higher-order-not-supported-by-holdout");
                candidates.Add(shape with { RejectionReasons = reasons.ToImmutable(), EligibleForReview = reasons.Count == 0 });
            }
        }
        // Training score ranks candidates. Holdout points never enter optimization or rotation fitting.
        candidates = candidates.OrderBy(c => c.RobustCost).Take(4).Select((c, i) => c with { Id = $"candidate-{i + 1}" }).ToList();
        var eligible = candidates.Where(c => c.EligibleForReview).ToArray();
        bool ambiguous = eligible.Length > 1 && eligible.Skip(1).Any(c => CurveDifference(eligible[0], c) > .2);
        string status = ambiguous ? "candidate-ambiguous" : eligible.Length > 0 ? "fit-review"
            : candidates.All(c => c.BoundaryLimited) ? "search-boundary" : "model-mismatch";
        var warnings = ImmutableArray.CreateBuilder<string>();
        warnings.Add("fixed-bit-length-equivalent-compensation-not-unique-physical-measurement");
        warnings.Add("finite-multistart-search-not-exhaustive");
        warnings.Add("unwrap-assumes-no-missing-turns-inspect-entire-photo-and-between-points");
        warnings.Add("unobserved-radii-and-physical-output-not-validated");
        if (candidates.Any(c => c.ParameterCorrelation > .998)) warnings.Add("parameters-correlated-prediction-and-parameter-uniqueness-are-different");
        var result = new TraceFitResult(1, "trace-fit-result", TraceFitResult.Algorithm, snapshotHash,
            RecordJson.Hash(session), generated, settings, range, status, "pending", candidates.ToImmutableArray(), warnings.ToImmutable());
        result.Validate(generated, snapshotHash, session); return result;

        List<TraceFitCandidate> Solve(int order, double radiusRange, GeometryParameters? seedModel = null)
        {
            int dimensions = 2 + order;
            double[] scales = Enumerable.Range(0, dimensions).Select(i => i < 2 ? radiusRange : settings.ShapeRange).ToArray();
            double[] center = [generated.InnerRadiusMm, generated.OuterRadiusMm, generated.PitchLinear, generated.PitchQuadratic, generated.PitchCubic];
            Evaluation? Evaluate(double[] x)
            {
                token.ThrowIfCancellationRequested();
                if (x.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1.0000001)) return null;
                double[] p = (double[])center.Clone(); for (int i = 0; i < dimensions; i++) p[i] += x[i] * scales[i];
                var parameters = generated with { InnerRadiusMm = p[0], OuterRadiusMm = p[1], PitchLinear = p[2], PitchQuadratic = p[3], PitchCubic = p[4] };
                try
                {
                    var model = new ForwardModel(original, new TrackGeometry(parameters));
                    if (data.Any(d => d.RadiusMm < p[0] || d.RadiusMm > p[1])) return null;
                    var phases = training.Select(d => model.AtRadius(d.RadiusMm).PhaseRadians).ToArray();
                    double rotation = training.Select((d, i) => (d.AngleRadians - phases[i]) * d.NormalScaleMm * d.NormalScaleMm).Sum()
                        / training.Sum(d => d.NormalScaleMm * d.NormalScaleMm);
                    for (int iteration = 0; iteration < 5; iteration++)
                    {
                        double sum = 0, weights = 0;
                        for (int i = 0; i < training.Length; i++)
                        {
                            double a = training[i].AngleRadians - phases[i], s = training[i].NormalScaleMm;
                            double w = s * s * RobustWeight((a - rotation) * s, settings.HuberDeltaMm);
                            sum += w * a; weights += w;
                        }
                        rotation = sum / weights;
                    }
                    double[] residuals = training.Select((d, i) => (phases[i] + rotation - d.AngleRadians) * d.NormalScaleMm).ToArray();
                    double cost = residuals.Select(e => Huber(e, settings.HuberDeltaMm)).Average();
                    return new((double[])x.Clone(), parameters, rotation, cost, residuals);
                }
                catch (ArgumentException) { return null; }
            }
            double[,]? Jacobian(Evaluation point)
            {
                var jac = new double[training.Length, dimensions];
                for (int c = 0; c < dimensions; c++)
                {
                    double[] plus = (double[])point.X.Clone(), minus = (double[])point.X.Clone();
                    plus[c] = Math.Min(1, plus[c] + 1e-4); minus[c] = Math.Max(-1, minus[c] - 1e-4);
                    var a = Evaluate(plus); var b = Evaluate(minus);
                    if (a is null && b is null) return null;
                    a ??= point; b ??= point;
                    double difference = a.X[c] - b.X[c]; if (difference == 0) return null;
                    for (int i = 0; i < training.Length; i++) jac[i, c] = (a.Residuals[i] - b.Residuals[i]) / difference;
                }
                return jac;
            }
            Evaluation Refine(Evaluation current)
            {
                double lambda = 1e-4;
                for (int iteration = 0; iteration < 40; iteration++)
                {
                    token.ThrowIfCancellationRequested(); var jac = Jacobian(current); if (jac is null) break;
                    var normal = new double[dimensions, dimensions]; var right = new double[dimensions];
                    for (int row = 0; row < training.Length; row++)
                    {
                        double weight = RobustWeight(current.Residuals[row], settings.HuberDeltaMm);
                        for (int a = 0; a < dimensions; a++)
                        {
                            right[a] -= weight * jac[row, a] * current.Residuals[row];
                            for (int b = 0; b < dimensions; b++) normal[a, b] += weight * jac[row, a] * jac[row, b];
                        }
                    }
                    for (int a = 0; a < dimensions; a++) normal[a, a] += lambda * Math.Max(normal[a, a], 1e-10);
                    var step = LinearSolve(normal, right); if (step is null) break;
                    double[] x = current.X.Select((v, i) => Math.Clamp(v + step[i], -1, 1)).ToArray();
                    var next = Evaluate(x);
                    if (next is not null && next.Cost < current.Cost)
                    {
                        double improvement = current.Cost - next.Cost; current = next; lambda = Math.Max(1e-12, lambda * .25);
                        if (improvement < 1e-13 || step.Max(Math.Abs) < 1e-8) break;
                    }
                    else { lambda *= 10; if (lambda > 1e8) break; }
                }
                return current;
            }

            var starts = new List<double[]> { new double[dimensions] };
            if (seedModel is not null)
            {
                double[] seed = new double[dimensions]; seed[0] = (seedModel.InnerRadiusMm - center[0]) / scales[0]; seed[1] = (seedModel.OuterRadiusMm - center[1]) / scales[1];
                starts.Insert(0, seed);
            }
            else foreach (var (a, b) in new[] { (-.6, -.6), (-.6, .6), (.6, -.6), (.6, .6) }) starts.Add([a, b]);
            var solutions = new List<Evaluation>();
            foreach (var start in starts)
            {
                var initial = Evaluate(start); if (initial is null) continue;
                var solution = Refine(initial);
                if (!solutions.Any(s => s.X.Zip(solution.X).Max(p => Math.Abs(p.First - p.Second)) < 1e-4)) solutions.Add(solution);
            }
            var output = new List<TraceFitCandidate>();
            foreach (var solution in solutions.OrderBy(s => s.Cost).Take(order == 0 ? 3 : 1))
            {
                var model = new ForwardModel(original, new TrackGeometry(solution.Parameters));
                var residuals = ImmutableArray.CreateBuilder<TraceResidual>();
                foreach (var group in data.GroupBy(d => d.TraceId))
                {
                    // Resolve only a single global integer offset for an independent validation arm.
                    double offset = group.Key == "line-1" ? 0 : 2 * Math.PI * Math.Round(group.Average(d =>
                        (d.AngleRadians - model.AtRadius(d.RadiusMm).PhaseRadians - solution.Rotation - d.SourceAngleOffsetRadians) / (2 * Math.PI)));
                    foreach (var d in group)
                    {
                        double e = (model.AtRadius(d.RadiusMm).PhaseRadians + solution.Rotation + d.SourceAngleOffsetRadians + offset - d.AngleRadians) * d.NormalScaleMm;
                        residuals.Add(new(d.TraceId, d.SourceIndex, d.IsHoldout, e));
                    }
                }
                double Rms(bool holdout) => Math.Sqrt(residuals.Where(r => r.IsHoldout == holdout).Average(r => r.ErrorMm * r.ErrorMm));
                double trainRms = Rms(false), holdoutRms = Rms(true), maximum = residuals.Max(r => Math.Abs(r.ErrorMm));
                bool boundary = solution.X.Any(x => Math.Abs(x) > .995);
                var reasons = ImmutableArray.CreateBuilder<string>();
                if (boundary) reasons.Add("search-boundary");
                if (trainRms > settings.MaximumRmsMm || holdoutRms > settings.MaximumRmsMm || maximum > settings.MaximumErrorMm) reasons.Add("residual-or-holdout-failed");
                double lo = data.Min(d => d.RadiusMm), hi = data.Max(d => d.RadiusMm);
                var curve = Enumerable.Range(0, 1025).Select(i =>
                { double r = lo + (hi - lo) * i / 1024; return new TraceCurvePoint(r, model.AtRadius(r).PhaseRadians + solution.Rotation); }).ToImmutableArray();
                if (curve.Zip(curve.Skip(1)).Any(p => Math.Abs(p.First.AngleRadians - p.Second.AngleRadians) > Math.PI / 8)) reasons.Add("curve-undersampled");
                foreach (var group in data.GroupBy(d => d.TraceId))
                {
                    var points = group.ToArray();
                    for (int i = 1; i < points.Length; i++)
                    {
                        var segment = curve.Where(c => c.RadiusMm >= points[i - 1].RadiusMm && c.RadiusMm <= points[i].RadiusMm).Select(c => c.AngleRadians).ToArray();
                        if (segment.Length > 1 && segment.Max() - segment.Min() > Math.PI / 2) { reasons.Add("between-point-turn-risk"); break; }
                    }
                }
                var jac = Jacobian(solution); double correlation = jac is null ? 1 : 0;
                if (jac is not null)
                    for (int a = 0; a < dimensions; a++) for (int b = a + 1; b < dimensions; b++)
                    {
                        double dot = 0, aa = 0, bb = 0;
                        for (int i = 0; i < training.Length; i++) { dot += jac[i, a] * jac[i, b]; aa += jac[i, a] * jac[i, a]; bb += jac[i, b] * jac[i, b]; }
                        correlation = Math.Max(correlation, aa * bb > 0 ? Math.Abs(dot) / Math.Sqrt(aa * bb) : 1);
                    }
                output.Add(new("unassigned", solution.Parameters, order, solution.Rotation, solution.Cost, trainRms, holdoutRms, maximum,
                    correlation, boundary, reasons.Count == 0, reasons.Distinct().ToImmutableArray(), residuals.ToImmutable(), curve));
            }
            return output;
        }
    }

    private static double CurveDifference(TraceFitCandidate a, TraceFitCandidate b)
    {
        double shift = 2 * Math.PI * Math.Round((a.Curve[0].AngleRadians - b.Curve[0].AngleRadians) / (2 * Math.PI));
        return a.Curve.Zip(b.Curve).Max(p => p.First.RadiusMm * Math.Abs(p.First.AngleRadians - p.Second.AngleRadians - shift));
    }
    private static double RobustWeight(double e, double delta) => Math.Abs(e) <= delta ? 1 : delta / Math.Abs(e);
    private static double Huber(double e, double delta) => Math.Abs(e) <= delta ? e * e / 2 : delta * (Math.Abs(e) - delta / 2);
    private static double[]? LinearSolve(double[,] matrix, double[] rhs)
    {
        int n = rhs.Length; var a = (double[,])matrix.Clone(); var b = (double[])rhs.Clone();
        for (int c = 0; c < n; c++)
        {
            int pivot = c; for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c])) pivot = r;
            if (!double.IsFinite(a[pivot, c]) || Math.Abs(a[pivot, c]) < 1e-20) return null;
            for (int j = c; j < n; j++) (a[c, j], a[pivot, j]) = (a[pivot, j], a[c, j]); (b[c], b[pivot]) = (b[pivot], b[c]);
            for (int r = c + 1; r < n; r++)
            { double f = a[r, c] / a[c, c]; for (int j = c; j < n; j++) a[r, j] -= f * a[c, j]; b[r] -= f * b[c]; }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--) { double sum = b[r]; for (int j = r + 1; j < n; j++) sum -= a[r, j] * x[j]; x[r] = sum / a[r, r]; }
        return x.All(double.IsFinite) ? x : null;
    }
}

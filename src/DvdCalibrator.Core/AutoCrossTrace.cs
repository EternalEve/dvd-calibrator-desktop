using System.Collections.Immutable;

namespace DvdCalibrator.Core;

/// <summary>Finds two independently observed, opposite arms of the DVD reference cross.</summary>
public static class AutoCrossTrace
{
    private const int Angles = 2048;
    private const int GridSize = 1526;
    private const double GridStep = .08;
    private const double RadiusStep = .12;
    private const double AngleStep = 2 * Math.PI / Angles;
    private sealed record Arm(double[] Angle, bool[] Supported, double Score);

    public static ImmutableArray<TraceObservation> Extract(RgbImage image, PhotoSession session, GeometryParameters generated,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!session.IsConfirmed) throw new CalibrationInputException("photo-rectification-unconfirmed", "请先确认照片矫正。");
        if (image.Width != session.Photo.OrientedWidth || image.Height != session.Photo.OrientedHeight
            || image.PixelsSha256 != session.Photo.OrientedPixelsSha256)
            throw new ArgumentException("自动描线的图像与会话原图不一致。");
        generated.Validate();
        var transform = session.GetTransform();
        var parallel = new ParallelOptions { CancellationToken = token };
        double firstRadius = generated.InnerRadiusMm + .55, lastRadius = generated.OuterRadiusMm - .4;
        int rows = (int)Math.Floor((lastRadius - firstRadius) / RadiusStep) + 1;
        if (rows < 48) throw Insufficient();

        var gray = new float[GridSize * GridSize];
        Parallel.For(0, GridSize, parallel, y =>
        {
            double ym = y * GridStep - 61;
            for (int x = 0; x < GridSize; x++)
            {
                double xm = x * GridStep - 61;
                if (xm * xm + ym * ym > 3600) continue;
                var p = transform.DiscToSource(new(xm, ym));
                gray[y * GridSize + x] = Luminance(image, p) ?? 128;
            }
        });
        var (dark, light) = Sato(gray, parallel);

        var darkResponse = new float[rows * Angles];
        var lightResponse = new float[darkResponse.Length];
        var contrast = new float[darkResponse.Length];
        var saturated = new bool[darkResponse.Length];
        var directions = Enumerable.Range(0, Angles).Select(i => Math.SinCos(i * AngleStep)).ToArray();
        double lineWidth = ReferenceTarget.LineWidthMm(generated.OuterRadiusMm);
        Parallel.For(0, rows, parallel, row =>
        {
            double r = firstRadius + row * RadiusStep;
            int offset = row * Angles;
            var values = new float[Angles];
            for (int a = 0; a < Angles; a++)
            {
                var p = transform.DiscToSource(new(r * directions[a].Cos, r * directions[a].Sin));
                float? value = Luminance(image, p);
                if (value is null || PhotoRaster.IsMasked(p.X, p.Y, session.Masks))
                {
                    values[a] = float.NaN;
                    continue;
                }
                saturated[offset + a] = value > 244;
                values[a] = value.Value;
            }
            double width = lineWidth / (r * AngleStep);
            var narrow = Gaussian(Math.Max(.5, .24 * width));
            var wide = Gaussian(.8 * width);
            for (int a = 0; a < Angles; a++)
            {
                int i = offset + a;
                if (float.IsNaN(values[a])) continue;
                float difference = FilterAngle(values, a, wide) - FilterAngle(values, a, narrow);
                if (!float.IsFinite(difference)) continue;
                contrast[i] = difference;
                double gx = (r * directions[a].Cos + 61) / GridStep;
                double gy = (r * directions[a].Sin + 61) / GridStep;
                var ridge = difference > 0 ? dark : light;
                float score = MathF.Sqrt(Math.Clamp(SampleGrid(ridge, gx, gy) / 3.57f, 0, 2)
                    * Math.Clamp(MathF.Abs(difference) / 9, 0, 2));
                if (difference > 0) darkResponse[i] = score;
                else lightResponse[i] = score;
            }
        });
        ExcludeGlare(darkResponse, lightResponse, saturated, rows, token);

        (Arm Main, Arm Opposite, double Score)? best = null;
        foreach (var (response, polarity) in new[] { (darkResponse, 1), (lightResponse, -1) })
        {
            var arms = FindSeeds(response, rows, token)
                .Select(seed => Follow(response, contrast, rows, seed, polarity, token))
                .Where(a => a.Score > 0).OrderByDescending(a => a.Score).ToArray();
            for (int i = 0; i < arms.Length; i++)
            for (int j = i + 1; j < arms.Length; j++)
            {
                double quality = OppositeAgreement(arms[i], arms[j]);
                if (quality <= 0) continue;
                double score = (arms[i].Score + arms[j].Score) * quality;
                if (best is null || score > best.Value.Score) best = (arms[i], arms[j], score);
            }
        }
        if (best is not { } pair) throw Insufficient();
        int stride = Math.Clamp(rows / 90, 1, 3);
        var traces = ImmutableArray.Create(Observation("line-1", pair.Main), Observation("validation-180", pair.Opposite));
        try { TraceFitter.Prepare(session.WithTraces(traces), generated); }
        catch (CalibrationInputException) { throw Insufficient(); }
        return traces;

        TraceObservation Observation(string id, Arm arm)
        {
            var samples = new List<(int Row, PixelPoint Point)>();
            int previous = -stride;
            for (int row = 0; row < rows; row++)
            {
                if (!arm.Supported[row] || row - previous < stride) continue;
                double r = firstRadius + row * RadiusStep;
                var (sin, cos) = Math.SinCos(arm.Angle[row]);
                var p = transform.DiscToSource(new(r * cos, r * sin));
                if (p.X < 0 || p.X > image.Width - 1 || p.Y < 0 || p.Y > image.Height - 1 || PhotoRaster.IsMasked(p.X, p.Y, session.Masks)) continue;
                samples.Add((row, p));
                previous = row;
            }
            return new(id, samples.Where((_, i) => !IsolatedJump(i)).Select(s => s.Point).ToImmutableArray());

            bool IsolatedJump(int i)
            {
                if (i < 2 || i >= samples.Count - 2 || samples[i + 2].Row - samples[i - 2].Row > 20) return false;
                int row = samples[i].Row, before = samples[i - 1].Row, after = samples[i + 1].Row;
                double anchor = arm.Angle[before];
                double anglePerRow = Math.IEEERemainder(arm.Angle[after] - anchor, 2 * Math.PI) / (after - before);
                double radius = firstRadius + row * RadiusStep;
                double scale = radius / Math.Sqrt(1 + Math.Pow(radius * anglePerRow / RadiusStep, 2));
                double Deviation(int at) => Math.Abs(Math.IEEERemainder(arm.Angle[at] - anchor - (at - before) * anglePerRow, 2 * Math.PI)) * scale;
                // Drop a solitary excursion only when the neighboring observations support the same local direction.
                return Deviation(row) > .3 && Deviation(samples[i - 2].Row) < .16 && Deviation(samples[i + 2].Row) < .16;
            }
        }
    }

    private static CalibrationInputException Insufficient() => new("auto-trace-insufficient", "照片中找不到两条清晰、连续的对侧十字臂。");

    private static float? Luminance(RgbImage image, PixelPoint p) => image.Sample(p.X, p.Y) is { } rgb
        ? .2126f * rgb.R + .7152f * rgb.G + .0722f * rgb.B : null;

    // Two-dimensional Sato tubeness (Sato et al., Medical Image Analysis 2(2), 1998).
    // The positive normal Hessian eigenvalue, normalized by sigma squared, is the dark ridge response.
    private static (float[] Dark, float[] Light) Sato(float[] gray, ParallelOptions parallel)
    {
        var dark = new float[gray.Length];
        var light = new float[gray.Length];
        var smooth = new float[gray.Length];
        var dx = new float[gray.Length];
        var dxx = new float[gray.Length];
        foreach (double sigma in new[] { 1.5, 2.2, 3.1 })
        {
            var g = Gaussian(sigma);
            int half = g.Length / 2;
            float variance = (float)(sigma * sigma);
            var g1 = new float[g.Length];
            var g2 = new float[g.Length];
            for (int i = 0; i < g.Length; i++)
            {
                float x = i - half;
                g1[i] = x * g[i] / variance;
                g2[i] = (x * x / variance - 1) * g[i] / variance;
            }
            float dc = g2.Sum();
            for (int i = 0; i < g2.Length; i++) g2[i] -= dc * g[i];
            // A padded grid lets us ignore edge values; all sampled annuli are within the interior.
            Parallel.For(0, GridSize, parallel, y =>
            {
                int row = y * GridSize;
                for (int x = half; x < GridSize - half; x++)
                {
                    float s = 0, sx = 0, sxx = 0;
                    int start = row + x - half;
                    for (int k = 0; k < g.Length; k++)
                    {
                        float v = gray[start + k];
                        s += v * g[k]; sx += v * g1[k]; sxx += v * g2[k];
                    }
                    smooth[row + x] = s; dx[row + x] = sx; dxx[row + x] = sxx;
                }
            });
            Parallel.For(half, GridSize - half, parallel, y =>
            {
                for (int x = half; x < GridSize - half; x++)
                {
                    float xx = 0, xy = 0, yy = 0;
                    int j = (y - half) * GridSize + x;
                    for (int k = 0; k < g.Length; k++, j += GridSize)
                    {
                        xx += dxx[j] * g[k]; xy += dx[j] * g1[k]; yy += smooth[j] * g2[k];
                    }
                    float difference = xx - yy;
                    float gap = MathF.Sqrt(difference * difference + 4 * xy * xy);
                    int i = y * GridSize + x;
                    dark[i] = Math.Max(dark[i], .5f * variance * (xx + yy + gap));
                    light[i] = Math.Max(light[i], .5f * variance * (gap - xx - yy));
                }
            });
        }
        return (dark, light);
    }

    private static float[] Gaussian(double sigma)
    {
        int half = (int)Math.Ceiling(4 * sigma);
        var weights = new float[2 * half + 1];
        for (int i = -half; i <= half; i++) weights[i + half] = (float)Math.Exp(-.5 * i * i / (sigma * sigma));
        float total = weights.Sum();
        for (int i = 0; i < weights.Length; i++) weights[i] /= total;
        return weights;
    }

    private static float SampleGrid(float[] grid, double x, double y)
    {
        int col = (int)x, row = (int)y * GridSize;
        float fx = (float)(x - col), fy = (float)(y - (int)y);
        float upper = grid[row + col] * (1 - fx) + grid[row + col + 1] * fx;
        float lower = grid[row + GridSize + col] * (1 - fx) + grid[row + GridSize + col + 1] * fx;
        return upper * (1 - fy) + lower * fy;
    }

    private static int Wrap(int angle) => angle & (Angles - 1);
    private static float FilterAngle(float[] values, int center, float[] weights)
    {
        float sum = 0;
        int start = center - weights.Length / 2;
        for (int i = 0; i < weights.Length; i++) sum += weights[i] * values[Wrap(start + i)];
        return sum;
    }

    private static void ExcludeGlare(float[] dark, float[] light, bool[] saturated, int rows, CancellationToken token)
    {
        for (int row = 0; row < rows; row++)
        {
            token.ThrowIfCancellationRequested();
            for (int angle = 0; angle < Angles; angle++)
            {
                if (!saturated[row * Angles + angle]) continue;
                for (int r = Math.Max(0, row - 3); r <= Math.Min(rows - 1, row + 3); r++)
                for (int a = angle - 7; a <= angle + 7; a++)
                    dark[r * Angles + Wrap(a)] = light[r * Angles + Wrap(a)] = 0;
            }
        }
    }

    private static List<int> FindSeeds(float[] response, int rows, CancellationToken token)
    {
        var profile = new float[Angles];
        var radial = new float[rows];
        for (int a = 0; a < Angles; a++)
        {
            if ((a & 63) == 0) token.ThrowIfCancellationRequested();
            for (int row = 0; row < rows; row++) radial[row] = response[row * Angles + a];
            Array.Sort(radial);
            profile[a] = radial[(int)(.84 * (rows - 1))];
        }
        var weights = Gaussian(25);
        var scores = Enumerable.Range(0, Angles).Select(a => FilterAngle(profile, a, weights)).ToArray();
        var seeds = new List<int>();
        foreach (int a in Enumerable.Range(0, Angles).Where(a => scores[a] > .015 && scores[a] >= scores[Wrap(a - 1)]
            && scores[a] > scores[Wrap(a + 1)]).OrderByDescending(a => scores[a]))
        {
            if (seeds.Any(other => Math.Min(Wrap(other - a), Wrap(a - other)) < 210)) continue;
            seeds.Add(a);
            if (seeds.Count == 6) break;
        }
        return seeds;
    }

    // A shortest path on the polar grid. Every edge advances one radius sample.
    private static Arm Follow(float[] response, float[] contrast, int rows, int seed, int polarity, CancellationToken token)
    {
        const int width = 385;
        const int reach = 6;
        int start = seed - width / 2;
        var back = new short[rows * width];
        var previous = new float[width];
        var next = new float[width];
        for (int col = 0; col < width; col++) previous[col] = response[Wrap(start + col)];
        for (int row = 1; row < rows; row++)
        {
            token.ThrowIfCancellationRequested();
            for (int col = 0; col < width; col++)
            {
                int best = col;
                for (int k = Math.Max(0, col - reach); k <= Math.Min(width - 1, col + reach); k++)
                    if (previous[k] > previous[best]) best = k;
                next[col] = previous[best] + response[row * Angles + Wrap(start + col)];
                back[row * width + col] = (short)best;
            }
            (previous, next) = (next, previous);
        }
        int at = Array.IndexOf(previous, previous.Max());
        var angles = new double[rows];
        var supported = new bool[rows];
        int support = 0;
        double sum = 0;
        for (int row = rows - 1; row >= 0; row--)
        {
            int angle = start + at;
            int offset = row * Angles;
            float score = response[offset + Wrap(angle)];
            if (score > .36 && polarity * contrast[offset + Wrap(angle)] > 2.2)
            {
                int best = angle;
                for (int a = angle - 2; a <= angle + 2; a++)
                    if (response[offset + Wrap(a)] > 0 && polarity * contrast[offset + Wrap(a)] > polarity * contrast[offset + Wrap(best)]) best = a;
                float left = polarity * contrast[offset + Wrap(best - 1)];
                float peak = polarity * contrast[offset + Wrap(best)];
                float right = polarity * contrast[offset + Wrap(best + 1)];
                float denominator = 2 * (left - 2 * peak + right);
                double correction = denominator < -.35 ? Math.Clamp((left - right) / denominator, -.5, .5) : 0;
                angles[row] = (best + correction) * AngleStep;
                supported[row] = true;
                support++;
                sum += score;
            }
            at = back[row * width + at];
        }
        int first = Array.IndexOf(supported, true), last = Array.LastIndexOf(supported, true);
        double quality = support >= rows * .55 && last - first >= rows * .62 ? sum / rows : 0;
        return new(angles, supported, quality);
    }

    private static double OppositeAgreement(Arm a, Arm b)
    {
        var deviations = new List<double>();
        for (int i = 0; i < a.Angle.Length; i++)
        {
            if (!a.Supported[i] || !b.Supported[i]) continue;
            double difference = Math.IEEERemainder(a.Angle[i] - b.Angle[i] - Math.PI, 2 * Math.PI);
            deviations.Add(Math.Abs(difference));
        }
        if (deviations.Count < a.Angle.Length * .38) return 0;
        deviations.Sort();
        if (deviations[deviations.Count / 2] > Math.PI / 60
            || deviations[(int)(.85 * (deviations.Count - 1))] > Math.PI / 22.5) return 0;
        return (double)deviations.Count / a.Angle.Length;
    }
}

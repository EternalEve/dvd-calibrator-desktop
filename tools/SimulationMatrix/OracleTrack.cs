using DvdCalibrator.Core;

namespace SimulationMatrix;

// Independent Simpson cells and local bisection. Never calls TrackGeometry or ForwardModel.
internal sealed class OracleTrack
{
    public GeometryParameters Parameters { get; }
    private readonly double[] arc, angle;
    private readonly double step, pitch;
    private const int Cells = 8192;
    public OracleTrack(GeometryParameters parameters, CancellationToken token)
    {
        Parameters = parameters; pitch = new ReferenceGeometry(parameters, 4096).Pitch;
        step = (parameters.OuterRadiusMm - parameters.InnerRadiusMm) / Cells;
        arc = new double[Cells + 1]; angle = new double[Cells + 1]; double arcError = 0, angleError = 0;
        for (int i = 1; i <= Cells; i++)
        {
            if (i % 512 == 0) token.ThrowIfCancellationRequested();
            double lo = parameters.InnerRadiusMm + step * (i - 1), hi = lo + step;
            double Add(double previous, double value, ref double error)
            { double term = value - error, next = previous + term; error = (next - previous) - term; return next; }
            arc[i] = Add(arc[i - 1], Integral(lo, hi, true), ref arcError);
            angle[i] = Add(angle[i - 1], Integral(lo, hi, false), ref angleError);
        }
    }
    private double Rate(double r, bool length)
    {
        double x = (r - Parameters.InnerRadiusMm) / (Parameters.OuterRadiusMm - Parameters.InnerRadiusMm);
        double q = 1 + Parameters.PitchLinear * x + Parameters.PitchQuadratic * x * x + Parameters.PitchCubic * x * x * x;
        double theta = 2 * Math.PI / (pitch * q);
        return length ? Math.Sqrt(1 + r * r * theta * theta) : theta;
    }
    private double Integral(double lo, double hi, bool length) => (hi - lo) / 6 * (Rate(lo, length) + 4 * Rate((lo + hi) / 2, length) + Rate(hi, length));
    private double At(double r, bool length)
    {
        if (r < Parameters.InnerRadiusMm - 1e-10 || r > Parameters.OuterRadiusMm + 1e-10) throw new ArgumentException("Oracle radius outside domain.");
        r = Math.Clamp(r, Parameters.InnerRadiusMm, Parameters.OuterRadiusMm);
        int i = Math.Min(Cells - 1, (int)((r - Parameters.InnerRadiusMm) / step));
        return (length ? arc[i] : angle[i]) + Integral(Parameters.InnerRadiusMm + i * step, r, length);
    }
    public double Angle(double r) => (Parameters.Direction == SpiralDirection.Cw ? -1 : 1) * At(r, false);
    public double Progress(double r) => Math.Clamp(At(r, true) / Parameters.TrackLengthMm, 0, 1);
    public double Radius(double progress)
    {
        if (progress is < 0 or > 1) throw new ArgumentException("Oracle progress outside domain.");
        if (progress == 0) return Parameters.InnerRadiusMm; if (progress == 1) return Parameters.OuterRadiusMm;
        double target = progress * Parameters.TrackLengthMm;
        int found = Array.BinarySearch(arc, target), i = found >= 0 ? Math.Min(found, Cells - 1) : Math.Clamp(~found - 1, 0, Cells - 1);
        double start = Parameters.InnerRadiusMm + i * step, lo = start, hi = start + step;
        for (int j = 0; j < 36; j++) { double mid = (lo + hi) / 2; if (arc[i] + Integral(start, mid, true) < target) lo = mid; else hi = mid; }
        return (lo + hi) / 2;
    }
    public static void Verify(CancellationToken token)
    {
        foreach (bool clockwise in new[] { true, false })
        {
            var p = new GeometryParameters(24, 58, 2295104, 133.33, .02, -.03, .015, clockwise ? SpiralDirection.Cw : SpiralDirection.Ccw, GeometryParameters.Convention, 0);
            var fast = new OracleTrack(p, token); var reference = new ReferenceGeometry(p, 16384);
            foreach (double fraction in new[] { .013, .18, .49, .86, .997 })
            {
                double r = 24 + fraction * 34;
                if (Math.Abs(fast.Angle(r) - reference.Angle(r)) > 2e-8 || Math.Abs(fast.Radius(fast.Progress(r)) - r) > 2e-10
                    || Math.Abs(fast.Radius(fraction) - reference.Radius(fraction)) > 2e-10) throw new Exception("Independent oracle failed refinement check.");
            }
        }
    }
}

internal sealed class TruthMapping
{
    public OracleTrack Generated { get; }
    public OracleTrack Actual { get; }
    private readonly (double Phase, double SourceRadius)[] table;
    public TruthMapping(GeometryParameters generated, GeometryParameters actual, CancellationToken token)
    {
        Generated = new(generated, token); Actual = new(actual, token);
        table = Enumerable.Range(0, 8193).Select(i => At(actual.InnerRadiusMm + (actual.OuterRadiusMm - actual.InnerRadiusMm) * i / 8192)).ToArray();
    }
    public (double Phase, double SourceRadius) At(double r)
    { double rg = Generated.Radius(Actual.Progress(r)); return (Actual.Angle(r) - Generated.Angle(rg), rg); }
    public (double Phase, double SourceRadius) Interpolate(double r)
    {
        double x = (r - Actual.Parameters.InnerRadiusMm) / (Actual.Parameters.OuterRadiusMm - Actual.Parameters.InnerRadiusMm) * 8192;
        int i = Math.Clamp((int)x, 0, 8191); double f = x - i;
        return (table[i].Phase + f * (table[i + 1].Phase - table[i].Phase), table[i].SourceRadius + f * (table[i + 1].SourceRadius - table[i].SourceRadius));
    }
}

using DvdCalibrator.Core;

// Independent numerical oracle: composite Simpson integration with compensated sums,
// bisection in p0 and in r. No calls to production quadrature, tables or root finders.
internal sealed class ReferenceGeometry
{
    private readonly GeometryParameters p;
    private readonly int intervals;
    public double Pitch { get; }

    public ReferenceGeometry(GeometryParameters p, int intervals = 16384)
    {
        this.p = p;
        this.intervals = intervals;
        double low = 1e-8, high = 1;
        double Arc(double pitch) => Integrate(r => Math.Sqrt(1 + Math.Pow(2 * Math.PI * r / (pitch * Shape(r)), 2)), p.OuterRadiusMm);
        if (Arc(low) < p.TrackLengthMm || Arc(high) > p.TrackLengthMm)
            throw new Exception("Oracle root is not bracketed.");
        for (int i = 0; i < 70; i++)
        {
            double mid = (low + high) / 2;
            if (Arc(mid) > p.TrackLengthMm) low = mid; else high = mid;
        }
        Pitch = (low + high) / 2;
    }

    public double Arc(double radius) => Integrate(r => Math.Sqrt(1 + Math.Pow(2 * Math.PI * r / (Pitch * Shape(r)), 2)), radius);
    public double Angle(double radius) => (p.Direction == SpiralDirection.Cw ? -1 : 1)
        * Integrate(r => 2 * Math.PI / (Pitch * Shape(r)), radius);
    public double Radius(double progress)
    {
        double low = p.InnerRadiusMm, high = p.OuterRadiusMm;
        for (int i = 0; i < 48; i++)
        {
            double mid = (low + high) / 2;
            if (Arc(mid) < progress * p.TrackLengthMm) low = mid; else high = mid;
        }
        return (low + high) / 2;
    }
    private double Shape(double r)
    {
        double x = (r - p.InnerRadiusMm) / (p.OuterRadiusMm - p.InnerRadiusMm);
        return 1 + p.PitchLinear * x + p.PitchQuadratic * x * x + p.PitchCubic * x * x * x;
    }
    private double Integrate(Func<double, double> f, double end)
    {
        double h = (end - p.InnerRadiusMm) / intervals;
        double sum = f(p.InnerRadiusMm) + f(end), error = 0;
        for (int i = 1; i < intervals; i++)
        {
            double term = (i % 2 == 0 ? 2 : 4) * f(p.InnerRadiusMm + i * h) - error;
            double next = sum + term;
            error = (next - sum) - term;
            sum = next;
        }
        return sum * h / 3;
    }
}

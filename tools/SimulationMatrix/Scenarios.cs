using DvdCalibrator.Core;

namespace SimulationMatrix;

internal sealed record Scenario(string Id, string Variant, int Seed, string Expectation, string Fault,
    double PixelNoiseSigmaDn, double ClickSigmaPx, double ContourSigmaPx, bool Perspective,
    double MaskDegrees, bool MissingBand, int FinePixels, double SharedContourScaleBias)
{
    public static IReadOnlyList<Scenario> All(bool quick)
    {
        var cases = new List<Scenario>();
        foreach (string variant in new[] { "cw-133.3", "ccw-133.33" })
        {
            foreach (int seed in quick ? new[] { 11 } : new[] { 11, 29 })
            {
                foreach (string kind in quick ? new[] { "baseline", "combined" } : new[] { "baseline", "pixel-noise", "click-noise", "contour-noise", "perspective", "masked-gap", "combined" })
                {
                    bool combined = kind == "combined", masked = combined || kind == "masked-gap";
                    cases.Add(new($"{variant}-{kind}-{seed}", variant, seed, "accept", "none", kind == "pixel-noise" ? 10 : combined ? 8 : 0,
                        kind == "click-noise" ? .25 : combined ? .15 : 0, kind == "contour-noise" ? .15 : combined ? .1 : 0,
                        kind == "perspective" || combined, masked ? 40 : 0, masked, 900, 0));
                }
            }
            foreach (string fault in quick ? new[] { "blank", "wrong-arm", "disc-drift", "wrong-g0", "low-resolution" }
                : new[] { "blank", "heavy-occlusion", "wrong-hole", "wrong-arm", "branch-switch", "reverse-order", "masked-point", "short-coverage", "disc-drift", "wrong-g0", "stale-coarse", "low-resolution" })
                cases.Add(new($"{variant}-{fault}-11", variant, 11, "reject", fault, 0, 0, 0, false, fault == "heavy-occlusion" ? 270 : 0, false, fault == "low-resolution" ? 360 : 900, 0));
            cases.Add(new($"{variant}-shared-contour-bias-11", variant, 11, "characterize", "none", 0, 0, 0, false, 0, false, 900, .001));
        }
        return cases;
    }
    public GeometryParameters Generated => new(24, 58, Variant.StartsWith("cw-", StringComparison.Ordinal) ? 2295104 : 2298496,
        Variant.StartsWith("cw-", StringComparison.Ordinal) ? 133.3 : 133.33,
        Variant.StartsWith("cw-", StringComparison.Ordinal) ? .02 : -.02, Variant.StartsWith("cw-", StringComparison.Ordinal) ? -.03 : .03,
        Variant.StartsWith("cw-", StringComparison.Ordinal) ? .015 : -.01,
        Variant.StartsWith("cw-", StringComparison.Ordinal) ? SpiralDirection.Cw : SpiralDirection.Ccw, GeometryParameters.Convention, 0);
    public GeometryParameters FirstActual => Generated with { InnerRadiusMm = 24.003, OuterRadiusMm = 58.006 };
    public GeometryParameters SecondActual => FirstActual with { InnerRadiusMm = Fault == "disc-drift" ? 24.0035 : 24.003, OuterRadiusMm = Fault == "disc-drift" ? 58.0068 : 58.006 };
}

internal sealed class Noise(uint seed)
{
    private uint state = seed == 0 ? 1 : seed;
    private double Uniform() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (state + .5) / 4294967296d; }
    public double Normal() => Math.Sqrt(-2 * Math.Log(Uniform())) * Math.Cos(2 * Math.PI * Uniform());
}

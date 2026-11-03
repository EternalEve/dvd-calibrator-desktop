using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace DvdCalibrator.Core;

public sealed record PredictionResult(int SchemaVersion, string RecordType, string SnapshotSha256,
    GeometryParameters Actual, double RotationRadians, double GeneratedPitchBaseMm, double ActualPitchBaseMm,
    double MaximumAdjacentAngleRadians, string PhysicalValidation, ImmutableArray<ForwardPoint> Curve,
    ImmutableArray<string> Warnings);

public static class Prediction
{
    public static PredictionResult Calculate(GenerationSnapshot snapshot, GeometryParameters actual,
        double rotationRadians, int samples, CancellationToken cancellationToken = default)
    {
        snapshot.Validate();
        if (samples is < 2 or > 100_001) throw new ArgumentException("采样点数要求 2..100001。");
        var model = new ForwardModel(snapshot.Generated, actual);
        var points = ImmutableArray.CreateBuilder<ForwardPoint>(samples);
        double maxDelta = 0;
        for (int i = 0; i < samples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double r = i == samples - 1 ? actual.OuterRadiusMm : actual.InnerRadiusMm
                + (actual.OuterRadiusMm - actual.InnerRadiusMm) * i / (samples - 1);
            var point = model.AtRadius(r, rotationRadians: rotationRadians);
            if (i > 0) maxDelta = Math.Max(maxDelta, Math.Abs(point.ObservedAngleRadians - points[^1].ObservedAngleRadians));
            points.Add(point);
        }
        return new(1, "forward-prediction", snapshot.ContentSha256, actual, rotationRadians,
            model.Generated.PitchBaseMm, model.Actual.PitchBaseMm, maxDelta, "pending", points.MoveToImmutable(),
            ["synthetic-geometry-only-not-optical-or-ecc-simulation", "not-a-fit-or-coarse-ready-decision",
             maxDelta > Math.PI / 8 ? "curve-undersampled-increase-samples" : "sampling-check-is-local-not-a-global-error-bound"]);
    }

    public static void WriteArtifacts(string folder, GenerationSnapshot snapshot, PredictionResult result)
    {
        RecordJson.WriteNew(Path.Combine(folder, "prediction.json"), result);
        using (var csv = new StreamWriter(new FileStream(Path.Combine(folder, "curve.csv"), FileMode.CreateNew)))
        {
            csv.WriteLine("r_mm,source_r_mm,u,phase_rad,alpha_rad,x_mm,y_mm");
            foreach (var p in result.Curve)
                csv.WriteLine(string.Join(',', new[] { p.ObservedRadiusMm, p.SourceRadiusMm, p.Progress,
                    p.PhaseRadians, p.ObservedAngleRadians, p.XMm, p.YMm }.Select(F)));
        }
        File.WriteAllText(Path.Combine(folder, "prediction.svg"), Svg(snapshot, result));
    }

    public static string Svg(GenerationSnapshot snapshot, PredictionResult result)
    {
        var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-64 -72 128 144\">\n");
        svg.Append("<rect x=\"-64\" y=\"-72\" width=\"128\" height=\"144\" fill=\"#f7f7f7\"/>");
        svg.Append("<text x=\"-60\" y=\"-66\" font-size=\"3\">Synthetic boundary prediction; physical validation pending</text>");
        svg.Append("<circle r=\"60\" fill=\"white\" stroke=\"#999\" stroke-width=\".2\"/><circle r=\"7.5\" fill=\"#eee\"/>");
        if (result.MaximumAdjacentAngleRadians > Math.PI / 8)
        {
            svg.Append("<text x=\"-48\" y=\"0\" font-size=\"3\">Curve undersampled: see JSON; increase samples.</text>");
        }
        else
        {
            // A half-disc has two boundaries; a cross has four centre lines (not line edges).
            int arms = snapshot.Pattern.Kind == "half-disc" ? 2 : 4;
            for (int arm = 0; arm < arms; arm++)
            {
                double angleOffset = arm * 2 * Math.PI / arms;
                svg.Append("<polyline fill=\"none\" stroke=\"#c45c12\" stroke-width=\".22\" points=\"");
                foreach (var point in result.Curve)
                {
                    double angle = point.ObservedAngleRadians + angleOffset;
                    // Only rendering adapts mathematical y-up into SVG y-down.
                    svg.Append(F(point.ObservedRadiusMm * Math.Cos(angle))).Append(',')
                        .Append(F(-point.ObservedRadiusMm * Math.Sin(angle))).Append(' ');
                }
                svg.Append("\"/>");
            }
        }
        svg.Append("<text x=\"-60\" y=\"67\" font-size=\"2.8\">Analytic half-disc boundaries / cross centre lines; mm coordinates</text></svg>");
        return svg.ToString();
    }

    private static string F(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
}

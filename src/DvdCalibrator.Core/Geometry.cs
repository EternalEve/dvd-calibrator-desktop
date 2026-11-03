using DiscImageStudio.Core;
using System.Text.Json.Serialization;

namespace DvdCalibrator.Core;

public enum SpiralDirection { Cw, Ccw }

// Required constructor parameters intentionally have no compatibility defaults.
public sealed record GeometryParameters(double InnerRadiusMm, double OuterRadiusMm,
    long TotalSectors, double ChannelBitLengthNm, double PitchLinear, double PitchQuadratic,
    double PitchCubic, SpiralDirection Direction, string DirectionConvention, long StartLba)
{
    public const string Convention = "disc-xy-y-up-recording-side";
    public const int ChannelBitsPerSector = 38688;
    [JsonIgnore] public double TrackLengthMm => TotalSectors * (double)ChannelBitsPerSector * ChannelBitLengthNm * 1e-6;

    public void Validate()
    {
        if (!double.IsFinite(InnerRadiusMm) || !double.IsFinite(OuterRadiusMm)
            || InnerRadiusMm < 7.5 || OuterRadiusMm > 60 || OuterRadiusMm <= InnerRadiusMm)
            throw new ArgumentException("12 cm DVD 模型要求 7.5 <= ri < ro <= 60 mm。");
        if (TotalSectors is <= 0 or > 3_000_000 || !double.IsFinite(ChannelBitLengthNm)
            || ChannelBitLengthNm <= 0 || !double.IsFinite(TrackLengthMm) || TrackLengthMm <= OuterRadiusMm - InnerRadiusMm)
            throw new ArgumentException("扇区数或位长无效；首版仅接受单层 DVD 容量范围。");
        if (!Enum.IsDefined(Direction) || DirectionConvention != Convention || StartLba != 0)
            throw new ArgumentException("首版要求显式记录面 y 向上方向约定及零起始 LBA。");
        DvdTrackGeometry.ValidatePitchCoefficients(PitchLinear, PitchQuadratic, PitchCubic);
    }
}

/// <summary>Strict bounded adapter; upstream's extension and clamping APIs are not exposed.</summary>
public sealed class TrackGeometry
{
    private readonly DvdTrackGeometry geometry;
    public GeometryParameters Parameters { get; }
    public double PitchBaseMm => geometry.TrackPitchMm;
    public double TotalAngleRadians => Sign * geometry.TotalAngleRadians;
    private double Sign => Parameters.Direction == SpiralDirection.Cw ? -1 : 1;

    public TrackGeometry(GeometryParameters parameters)
    {
        parameters.Validate();
        Parameters = parameters;
        geometry = DvdTrackGeometry.Create(parameters.InnerRadiusMm, parameters.OuterRadiusMm,
            parameters.TrackLengthMm, parameters.PitchLinear, parameters.PitchQuadratic, parameters.PitchCubic);
    }

    public double ProgressAtRadius(double radiusMm)
    {
        CheckRadius(radiusMm);
        if (radiusMm == Parameters.InnerRadiusMm) return 0;
        if (radiusMm == Parameters.OuterRadiusMm) return 1;
        return geometry.ArcLengthAtRadius(radiusMm) / Parameters.TrackLengthMm;
    }

    public double RadiusAtProgress(double progress)
    {
        if (!double.IsFinite(progress) || progress < 0 || progress > 1)
            throw new ArgumentOutOfRangeException(nameof(progress), "进度必须在 [0,1]，禁止静默夹取。");
        if (progress == 0) return Parameters.InnerRadiusMm;
        if (progress == 1) return Parameters.OuterRadiusMm;
        return geometry.RadiusAtArcLength(progress * Parameters.TrackLengthMm);
    }

    public double AngleAtRadius(double radiusMm)
    {
        CheckRadius(radiusMm);
        return Sign * geometry.AngleAtRadius(radiusMm);
    }

    private void CheckRadius(double radiusMm)
    {
        if (!double.IsFinite(radiusMm) || radiusMm < Parameters.InnerRadiusMm || radiusMm > Parameters.OuterRadiusMm)
            throw new ArgumentOutOfRangeException(nameof(radiusMm), "半径不在模型有效域内，首版不进行延拓。");
    }
}

public sealed record ForwardPoint(double ObservedRadiusMm, double SourceRadiusMm,
    double Progress, double PhaseRadians, double ObservedAngleRadians, double XMm, double YMm);

/// <summary>alpha = psi + theta_A(r) - theta_G(inverse_u_G(u_A(r))) + beta.</summary>
public sealed class ForwardModel
{
    public TrackGeometry Generated { get; }
    public TrackGeometry Actual { get; }
    public ForwardModel(GeometryParameters generated, GeometryParameters actual)
    {
        if (generated.TotalSectors != actual.TotalSectors || generated.StartLba != actual.StartLba)
            throw new ArgumentException("正向映射要求相同通道位数和数据起点。");
        Generated = new(generated);
        Actual = new(actual);
    }

    public ForwardPoint AtRadius(double observedRadiusMm, double sourceAngleRadians = 0, double rotationRadians = 0)
    {
        if (!double.IsFinite(sourceAngleRadians) || !double.IsFinite(rotationRadians))
            throw new ArgumentException("源角度和照片转角必须有限。");
        double progress = Actual.ProgressAtRadius(observedRadiusMm);
        double sourceRadius = Generated.RadiusAtProgress(progress);
        double phase = Actual.AngleAtRadius(observedRadiusMm) - Generated.AngleAtRadius(sourceRadius);
        double angle = sourceAngleRadians + phase + rotationRadians;
        return new(observedRadiusMm, sourceRadius, progress, phase, angle,
            observedRadiusMm * Math.Cos(angle), observedRadiusMm * Math.Sin(angle));
    }
}

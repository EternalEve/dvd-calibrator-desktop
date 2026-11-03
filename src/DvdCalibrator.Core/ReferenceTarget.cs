using System.Collections.Immutable;
using DiscImageStudio.Core.Calibration;

namespace DvdCalibrator.Core;

public sealed record ReferenceTargetContract(string AdapterVersion, string UpstreamCommit, string PngSha256,
    string CoordinateConvention, int PixelSize, double PixelCentre, double StartAngleDegrees,
    int LuminanceThreshold, int AlphaThreshold, int SampleEveryChannelBits, string Tone, string PsnOffset)
{
    public static ReferenceTargetContract Default => new("discimage-dvd-target-slim-v1", DiscImageRecord.UpstreamCommit,
        CalibrationReferencePattern.PngSha256, "disc-xy-y-up-recording-side-to-image-y-down", 2400, 1199.5, 0, 128, 128, 1, "legacy-null", "0x30000");
    public void Validate()
    { if (this != Default) throw new ArgumentException("标靶映射与固定 DVD 参考契约不一致；当前要求起始角 0、128 阈值、tone=null。"); }
}

public static class ReferenceTarget
{
    public static byte[] PngBytes() => CalibrationReferencePattern.ReadPngBytes();
    public static byte[] GrayPixels() => CalibrationReferencePattern.ReadGrayPixels();
    public static double LineWidthMm(double outerRadius) => 10 * outerRadius / 1199.5;
    public static ImmutableArray<double> RingRadiiMm(double outerRadius) =>
        CalibrationReferencePattern.RingRadiiPixels.Select(r => r * outerRadius / 1199.5).ToImmutableArray();
    // The exact upstream sampler accepts disc Y down. Our public convention is always Y up.
    public static byte Sample(double xMm, double yMm, double outerRadius)
        => CalibrationReferencePattern.Sample(xMm, -yMm, outerRadius, CalibrationDiscKind.Dvd);
    public static byte AtProgress(GeometryParameters g, double progress, double startAngleDegrees = 0)
    {
        var geometry = new TrackGeometry(g); double r = geometry.RadiusAtProgress(progress);
        double a = geometry.AngleAtRadius(r) + startAngleDegrees * Math.PI / 180;
        return Sample(r * Math.Cos(a), r * Math.Sin(a), g.OuterRadiusMm);
    }
}

// Explicit simulation evidence; this is never a substitute for an engine sidecar.
public sealed record ReferenceTargetSimulation(int SchemaVersion, string RecordType, string Id, string EvidenceSource,
    string PlanSha256, DiscSpec Disc, GeometryParameters Generated, ReferenceTargetContract Target, string ContentSha256)
{
    public static ReferenceTargetSimulation Create(string id, string planHash, DiscSpec disc, GeometryParameters generated)
    {
        var s = new ReferenceTargetSimulation(1, "reference-target-simulation", id, "simulation", planHash, disc, generated, ReferenceTargetContract.Default, "");
        s = s with { ContentSha256 = RecordJson.Hash(s) }; s.Validate(); return s;
    }
    public void Validate()
    {
        Disc.Validate(); Generated.Validate(); Target.Validate();
        if (SchemaVersion != 1 || RecordType != "reference-target-simulation" || string.IsNullOrWhiteSpace(Id)
            || EvidenceSource != "simulation" || !RecordJson.IsHash(PlanSha256) || ContentSha256 != RecordJson.Hash(this with { ContentSha256 = "" }))
            throw new ArgumentException("参考靶模拟快照版本或内容绑定无效。");
    }
}

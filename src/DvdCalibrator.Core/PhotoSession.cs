using System.Collections.Immutable;
using DiscImageStudio.Core.Calibration;
using System.Text.Json.Serialization;

namespace DvdCalibrator.Core;

public sealed record PhotoMetadata(string OriginalSha256, string OrientedFileSha256, string OrientedPixelsSha256,
    int OriginalWidth, int OriginalHeight, int OrientedWidth, int OrientedHeight, int AppliedExifOrientation);
public sealed record MaskPolygon(ImmutableArray<PixelPoint> Vertices);
public sealed record PhotoRectification(string InputSha256, string AlgorithmVersion, string MatrixConvention,
    ImmutableArray<double> DiscToSourceMatrix9, double OuterRmsPixels, double HoleRmsPixels);

public sealed record PhotoSession(int SchemaVersion, string RecordType, string SnapshotId, string SnapshotSha256,
    string ObservationSide, PhotoMetadata Photo, ImmutableArray<PixelPoint> OuterBoundary, ImmutableArray<PixelPoint> HoleBoundary,
    ImmutableArray<MaskPolygon> Masks, ImmutableArray<TraceObservation> Traces,
    PhotoRectification? Rectification, string? ConfirmedRectificationSha256)
{
    public const string Algorithm = "discimage-dual-circle-374c063-v1";
    public const string MatrixConvention = "disc-xy-y-down-to-oriented-source-pixels";
    public static PhotoSession Create(string snapshotId, string snapshotHash, PhotoMetadata photo)
        => new(2, "photo-session", snapshotId, snapshotHash, "recording-side", photo, [], [], [], [], null, null);

    public string BoundaryHash() => RecordJson.Hash(new { SnapshotId, SnapshotSha256, Photo,
        OuterBoundary, HoleBoundary, physicalOuterRadiusMm = 60, physicalHoleRadiusMm = 7.5, algorithm = Algorithm });
    [JsonIgnore] public bool IsConfirmed => Rectification is not null
        && ConfirmedRectificationSha256 == RecordJson.Hash(Rectification) && Rectification.InputSha256 == BoundaryHash();
    public PhotoSession WithBoundaries(ImmutableArray<PixelPoint> outer, ImmutableArray<PixelPoint> hole)
        => this with { OuterBoundary = outer, HoleBoundary = hole, Rectification = null, ConfirmedRectificationSha256 = null };
    // Original source pixels are preserved, so editing a contour does not fabricate new trace clicks.
    public PhotoSession WithMasks(ImmutableArray<MaskPolygon> masks) => this with { Masks = masks };
    public PhotoSession WithTraces(ImmutableArray<TraceObservation> traces) => this with { Traces = traces };
    public PhotoSession Confirm()
    {
        Validate();
        if (Rectification is null) throw new ArgumentException("请先计算并核对矫正预览。");
        return this with { ConfirmedRectificationSha256 = RecordJson.Hash(Rectification) };
    }

    public PhotoSession Rectify(CancellationToken token = default)
    {
        Validate();
        var fit = CalibrationPhotoRectifier.Fit(OuterBoundary.Select(p => new CalibrationPoint(p.X, p.Y)).ToArray(),
            HoleBoundary.Select(p => new CalibrationPoint(p.X, p.Y)).ToArray(), 60, 7.5, token);
        if (!fit.Succeeded || fit.Transform is null) throw new ArgumentException(fit.Message);
        return this with { Rectification = new(BoundaryHash(), Algorithm, MatrixConvention,
            fit.Transform.DiscToSourceMatrix9.ToImmutableArray(), fit.OuterRmsPixels, fit.HoleRmsPixels),
            ConfirmedRectificationSha256 = null };
    }

    public DiscPhotoTransform GetTransform()
    {
        Validate();
        if (Rectification is null) throw new ArgumentException("尚未计算照片矫正。");
        return new(Rectification.DiscToSourceMatrix9);
    }

    public void Validate()
    {
        if (SchemaVersion != 2 || RecordType != "photo-session" || string.IsNullOrWhiteSpace(SnapshotId)
            || !RecordJson.IsHash(SnapshotSha256) || ObservationSide != "recording-side") throw new ArgumentException("照片会话版本、观察面或快照绑定无效；本版本要求记录面照片。");
        ArgumentNullException.ThrowIfNull(Photo);
        if (!RecordJson.IsHash(Photo.OriginalSha256) || !RecordJson.IsHash(Photo.OrientedFileSha256)
            || !RecordJson.IsHash(Photo.OrientedPixelsSha256) || Photo.OriginalWidth is < 1 or > 30000
            || Photo.OriginalHeight is < 1 or > 30000 || Photo.OrientedWidth is < 1 or > 30000
            || Photo.OrientedHeight is < 1 or > 30000 || (long)Photo.OrientedWidth * Photo.OrientedHeight > 40_000_000
            || Photo.AppliedExifOrientation is < 1 or > 8
            || Photo.OrientedWidth != (Photo.AppliedExifOrientation >= 5 ? Photo.OriginalHeight : Photo.OriginalWidth)
            || Photo.OrientedHeight != (Photo.AppliedExifOrientation >= 5 ? Photo.OriginalWidth : Photo.OriginalHeight))
            throw new ArgumentException("原图身份、EXIF 或尺寸无效。");
        void Points(ImmutableArray<PixelPoint> points, int maximum)
        {
            if (points.IsDefault || points.Length > maximum || points.Any(p => p is null || !double.IsFinite(p.X)
                || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0 || p.X > Photo.OrientedWidth - 1 || p.Y > Photo.OrientedHeight - 1))
                throw new ArgumentException("标注超出定向后原图的像素中心范围。");
        }
        Points(OuterBoundary, 256); Points(HoleBoundary, 256);
        if (Masks.IsDefault || Masks.Length > 64 || Traces.IsDefault || Traces.Length > 4)
            throw new ArgumentException("遮罩或曲线数量无效。");
        foreach (var mask in Masks)
        {
            ArgumentNullException.ThrowIfNull(mask); Points(mask.Vertices, 256);
            if (mask.Vertices.Length < 3) throw new ArgumentException("遮罩多边形至少需要 3 个顶点。");
        }
        if (Traces.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id)) || Traces.Select(t => t.Id).Distinct().Count() != Traces.Length)
            throw new ArgumentException("曲线身份无效。");
        foreach (var trace in Traces) Points(trace.Points, 4000);
        if (Masks.Sum(m => m.Vertices.Length) + Traces.Sum(t => t.Points.Length) > 16000)
            throw new ArgumentException("总标注点超过限制。");
        if (Rectification is { } r)
        {
            if (r.InputSha256 != BoundaryHash() || r.AlgorithmVersion != Algorithm || r.MatrixConvention != MatrixConvention
                || !double.IsFinite(r.OuterRmsPixels) || !double.IsFinite(r.HoleRmsPixels)
                || r.OuterRmsPixels < 0 || r.HoleRmsPixels < 0 || OuterBoundary.Length < 5 || HoleBoundary.Length < 5)
                throw new ArgumentException("矫正结果与当前轮廓不一致，需要重新计算。");
            _ = new DiscPhotoTransform(r.DiscToSourceMatrix9);
        }
        if (ConfirmedRectificationSha256 is not null && !IsConfirmed)
            throw new ArgumentException("矫正确认已失效。");
    }
}

public sealed class DiscPhotoTransform
{
    private readonly CalibrationPhotoTransform transform;
    public DiscPhotoTransform(ImmutableArray<double> matrix)
    {
        if (matrix.IsDefault || matrix.Length != 9) throw new ArgumentException("单应矩阵必须含 9 个数。");
        transform = new(matrix.ToArray());
        if (!transform.IsValidForDisc(60)) throw new ArgumentException("单应矩阵在盘面内退化或越过投影地平线。");
    }
    public PixelPoint SourceToDisc(PixelPoint p)
    { var q = transform.SourceToDisc(new(p.X, p.Y)); return new(q.X, -q.Y); }
    public PixelPoint DiscToSource(PixelPoint p)
    { var q = transform.DiscToSource(new(p.X, -p.Y)); return new(q.X, q.Y); }
}

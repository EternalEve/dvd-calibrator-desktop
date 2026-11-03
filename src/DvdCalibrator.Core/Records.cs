using System.Collections.Immutable;

namespace DvdCalibrator.Core;

public sealed record DiscSpec(string MediaType, double DiameterMm, double HoleDiameterMm, int Layers, string? Mid)
{
    public void Validate()
    {
        if (MediaType is not ("DVD-R" or "DVD+R") || DiameterMm != 120 || HoleDiameterMm != 15 || Layers != 1)
            throw new ArgumentException("首版只接受 120/15 mm 单层 DVD-R 或 DVD+R。");
    }
}

// Analytic patterns are explicitly versioned, and are not the upstream target_slim PNG.
public sealed record PatternDefinition(string Id, string Kind, double LineWidthMm, string MappingVersion)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Kind is not ("half-disc" or "radial-cross")
            || MappingVersion != "analytic-disc-xy-v1" || !double.IsFinite(LineWidthMm)
            || LineWidthMm <= 0 || LineWidthMm > 2)
            throw new ArgumentException("不支持的解析标靶或线宽。");
    }

    public bool IsWhite(double sourceRadius, double sourceAngle)
    {
        if (Kind == "half-disc") return Math.Sin(sourceAngle) >= 0;
        return Math.Abs(sourceRadius * Math.Sin(sourceAngle)) > LineWidthMm / 2
            && Math.Abs(sourceRadius * Math.Cos(sourceAngle)) > LineWidthMm / 2;
    }
}

public sealed record GeneratorInfo(string Name, string Version, string SourceKind)
{
    public void Validate()
    {
        if (Name != "DvdCalibrator" || Version != "0.1.0" || SourceKind != "synthetic")
            throw new ArgumentException("第一阶段完整快照仅支持本工具的合成生成记录；外部记录单独解析并标明缺失项。");
    }
}

public sealed record GenerationSnapshot(int SchemaVersion, string RecordType, string Id, string State,
    DiscSpec Disc, GeometryParameters Generated, PatternDefinition Pattern, GeneratorInfo Generator,
    string ContentSha256)
{
    public static GenerationSnapshot CreateSynthetic(string id, DiscSpec disc, GeometryParameters generated,
        PatternDefinition pattern)
    {
        var snapshot = new GenerationSnapshot(1, "generation-snapshot", id, "frozen", disc, generated, pattern,
            new("DvdCalibrator", "0.1.0", "synthetic"), "");
        snapshot = snapshot with { ContentSha256 = RecordJson.Hash(snapshot) };
        snapshot.Validate();
        return snapshot;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || RecordType != "generation-snapshot" || State != "frozen" || string.IsNullOrWhiteSpace(Id))
            throw new ArgumentException("快照类型、版本、状态或 ID 无效。");
        ArgumentNullException.ThrowIfNull(Disc); Disc.Validate();
        ArgumentNullException.ThrowIfNull(Generated); Generated.Validate();
        ArgumentNullException.ThrowIfNull(Pattern); Pattern.Validate();
        ArgumentNullException.ThrowIfNull(Generator); Generator.Validate();
        if (!RecordJson.IsHash(ContentSha256) || ContentSha256 != RecordJson.Hash(this with { ContentSha256 = "" }))
            throw new ArgumentException("冻结快照哈希不一致，生成条件已改变。");
    }
}

public sealed record PixelPoint(double X, double Y);
public sealed record TraceObservation(string Id, ImmutableArray<PixelPoint> Points);

// Phase A persists original pixels only; rectification and masks will extend a new schema version.
public sealed record Observations(int SchemaVersion, string RecordType, string SnapshotId, string SnapshotSha256,
    string PhotoSha256, string PhotoFile, int OrientedWidth, int OrientedHeight, int AppliedExifOrientation,
    ImmutableArray<PixelPoint> OuterBoundary, ImmutableArray<PixelPoint> HoleBoundary,
    ImmutableArray<TraceObservation> Traces)
{
    public void Validate(GenerationSnapshot snapshot)
    {
        snapshot.Validate();
        if (SchemaVersion != 1 || RecordType != "observations" || SnapshotId != snapshot.Id
            || SnapshotSha256 != snapshot.ContentSha256 || !RecordJson.IsHash(PhotoSha256)
            || string.IsNullOrWhiteSpace(PhotoFile) || Path.IsPathRooted(PhotoFile)
            || PhotoFile.Contains(':') || PhotoFile.Split('/', '\\').Any(p => p is ".." or "")
            || OrientedWidth is < 1 or > 100_000 || OrientedHeight is < 1 or > 100_000
            || AppliedExifOrientation is < 1 or > 8 || OuterBoundary.IsDefault || HoleBoundary.IsDefault || Traces.IsDefault)
            throw new ArgumentException("观测记录无效或未绑定到当前快照。");
        void Check(ImmutableArray<PixelPoint> points)
        {
            if (points.IsDefault || points.Length > 20_000 || points.Any(p => p is null || !double.IsFinite(p.X)
                || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0 || p.X >= OrientedWidth || p.Y >= OrientedHeight))
                throw new ArgumentException("原图像素标注无效。");
        }
        Check(OuterBoundary); Check(HoleBoundary);
        if (Traces.Length > 4 || Traces.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id))
            || Traces.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != Traces.Length)
            throw new ArgumentException("曲线 ID 无效或重复。");
        foreach (var trace in Traces) Check(trace.Points);
    }
}

// Data contract only: Phase A intentionally has no fit command or ready-state producer.
public sealed record FitCandidate(string Id, GeometryParameters Parameters, double RmsErrorMm, double MaxErrorMm);
public sealed record FitResult(int SchemaVersion, string RecordType, string SnapshotSha256, string ObservationsSha256,
    string AlgorithmVersion, string QualityStatus, string PhysicalValidation, ImmutableArray<FitCandidate> Candidates)
{
    public void Validate(GenerationSnapshot snapshot, Observations observations)
    {
        observations.Validate(snapshot);
        if (SchemaVersion != 1 || RecordType != "fit-result" || SnapshotSha256 != snapshot.ContentSha256
            || ObservationsSha256 != RecordJson.Hash(observations) || string.IsNullOrWhiteSpace(AlgorithmVersion)
            || PhysicalValidation != "pending" || QualityStatus is not ("record-incomplete" or "photo-insufficient"
                or "annotation-invalid" or "candidate-ambiguous" or "model-mismatch" or "coarse-ready" or "fine-ready")
            || Candidates.IsDefault || Candidates.Length > 100)
            throw new ArgumentException("结果版本、状态或输入绑定无效。");
        if (QualityStatus is "coarse-ready" or "fine-ready" && Candidates.Length == 0)
            throw new ArgumentException("通过状态必须有候选。");
        if (Candidates.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id))
            || Candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != Candidates.Length)
            throw new ArgumentException("候选 ID 无效或重复。");
        foreach (var candidate in Candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate.Parameters); candidate.Parameters.Validate();
            if (candidate.Parameters.TotalSectors != snapshot.Generated.TotalSectors
                || !double.IsFinite(candidate.RmsErrorMm) || !double.IsFinite(candidate.MaxErrorMm)
                || candidate.RmsErrorMm < 0 || candidate.MaxErrorMm < candidate.RmsErrorMm)
                throw new ArgumentException("候选容量或误差无效。");
        }
    }
}

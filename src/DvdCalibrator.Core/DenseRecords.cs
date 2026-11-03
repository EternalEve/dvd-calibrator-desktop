using System.Collections.Immutable;

namespace DvdCalibrator.Core;

public sealed record DenseSettings(int AngularSamples, double RadiusRangeMm, double MinimumContrast,
    double MaximumImageError, double MaximumPhaseErrorRadians)
{
    public static DenseSettings Default => new(48, .02, 24, .25, .3);
    public void Validate()
    {
        if (AngularSamples is < 24 or > 64 || AngularSamples % 4 != 0
            || !double.IsFinite(RadiusRangeMm) || RadiusRangeMm is < .001 or > .5
            || !double.IsFinite(MinimumContrast) || MinimumContrast is < 12 or > 100
            || !double.IsFinite(MaximumImageError) || MaximumImageError is < .05 or > .25
            || !double.IsFinite(MaximumPhaseErrorRadians) || MaximumPhaseErrorRadians is < .05 or > .3)
            throw new ArgumentException("密环分析设置超出允许范围。");
    }
}

public sealed record DensePhasePoint(int RadialIndex, double RadiusMm, double PhaseRadians, bool IsHoldout);
public sealed record DenseFragment(string Id, int Channel, ImmutableArray<DensePhasePoint> Points);
public sealed record LocalFrequency(double RadiusMm, double CyclesPerMm, double SignedPhaseCyclesPerMm,
    double SpectralConcentration, double EffectivePixelsPerCycle);
public sealed record DenseObservations(int RadialSamples, double RadialStepMm, double SourcePixelsPerMm,
    double UsableRadialFraction, ImmutableArray<DenseFragment> Fragments,
    ImmutableArray<LocalFrequency> Frequencies, ImmutableArray<string> Warnings);
public sealed record FragmentBranch(string FragmentId, int WholeTurns, double TrainingPhaseRmsRadians,
    double HoldoutPhaseRmsRadians, double ImageOffset, double ImageGain);
public sealed record DenseCandidate(string Id, GeometryParameters Parameters, int Polarity, double RotationRadians,
    double TrainingPhaseRmsRadians, double HoldoutPhaseRmsRadians,
    double TrainingImageError, double HoldoutImageError, double MixedTraceRmsMm, bool BoundaryLimited,
    double MinimumPredictedPixelsPerCycle, bool Supported, ImmutableArray<string> RejectionReasons, ImmutableArray<FragmentBranch> Branches,
    ImmutableArray<TraceCurvePoint> Curve);
public sealed record NextDiscCurve(string CandidateId, ImmutableArray<TraceCurvePoint> Curve);
public sealed record NextDiscDiagnostic(GeometryParameters Generated, PatternDefinition FinePattern, double InnerRadiusMm, double OuterRadiusMm,
    double MaximumCandidateDifferenceMm, double MinimumPixelsPerTurn, double MinimumPixelsBetweenArms,
    double MinimumLineWidthPixels, bool TraceableAtCurrentResolution,
    string Status, ImmutableArray<NextDiscCurve> Curves);

public sealed record DenseAnalysis(int SchemaVersion, string RecordType, string AlgorithmVersion,
    string SnapshotSha256, string SessionSha256, DenseSettings Settings, DenseObservations Observations,
    string QualityStatus, string PhysicalValidation, ImmutableArray<DenseCandidate> Candidates,
    NextDiscDiagnostic? NextDisc, ImmutableArray<string> Warnings)
{
    public const string Algorithm = "half-disc-multiangle-fragments-v1";
    // Re-extract from the hash-bound original and rescore, without rerunning parameter optimization.
    // This also rejects changed branches, diagnostic curves, statistics and eligibility flags on load.
    public void Verify(GenerationSnapshot snapshot, PhotoSession session, RgbImage image, CancellationToken token = default)
    {
        Settings.Validate(); snapshot.Validate(); session.Validate();
        if (SchemaVersion != 1 || RecordType != "dense-analysis" || AlgorithmVersion != Algorithm
            || SnapshotSha256 != snapshot.ContentSha256 || SessionSha256 != RecordJson.Hash(session)
            || PhysicalValidation != "pending" || Candidates.IsDefault || Candidates.Length > 8
            || Candidates.Any(c => c is null) || Candidates.Select(c => c.Id).Distinct().Count() != Candidates.Length
            || Warnings.IsDefault || Observations is null)
            throw new ArgumentException("密环结果未绑定当前输入或版本无效。");
        var input = DenseAnalyzer.Extract(snapshot, session, image, Settings, token);
        if (RecordJson.Hash(input.Observations) != RecordJson.Hash(Observations))
            throw new ArgumentException("密环观测与原始图像不一致。");
        foreach (var candidate in Candidates)
        {
            candidate.Parameters.Validate();
            if (!Enumerable.Range(1, 8).Any(i => candidate.Id == $"dense-{i}") || candidate.Polarity is not (-1 or 1) || !double.IsFinite(candidate.RotationRadians)
                || candidate.Parameters != (snapshot.Generated with {
                    InnerRadiusMm = candidate.Parameters.InnerRadiusMm, OuterRadiusMm = candidate.Parameters.OuterRadiusMm })
                || Math.Abs(candidate.Parameters.InnerRadiusMm - snapshot.Generated.InnerRadiusMm) > Settings.RadiusRangeMm + 1e-10
                || Math.Abs(candidate.Parameters.OuterRadiusMm - snapshot.Generated.OuterRadiusMm) > Settings.RadiusRangeMm + 1e-10)
                throw new ArgumentException("密环候选改变了固定参数或越过搜索范围。");
            var expected = DenseAnalyzer.Score(input, candidate.Id, candidate.Parameters, candidate.Polarity, candidate.RotationRadians, token);
            if (RecordJson.Hash(expected) != RecordJson.Hash(candidate)) throw new ArgumentException("密环候选评分或分支已失效。");
        }
        var rebuilt = DenseAnalyzer.Finish(input, Candidates);
        if (RecordJson.Hash(rebuilt) != RecordJson.Hash(this)) throw new ArgumentException("密环状态或下一张预测已失效。");
    }
}

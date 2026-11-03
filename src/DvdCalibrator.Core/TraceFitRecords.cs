using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace DvdCalibrator.Core;

public sealed record TraceFitSettings(int ShapeOrder, double RadiusRangeMm, double ShapeRange,
    double HuberDeltaMm, double MaximumRmsMm, double MaximumErrorMm)
{
    public static TraceFitSettings Default => new(0, .02, .05, .1, .35, .8);
    public void Validate()
    {
        if (ShapeOrder is < 0 or > 3 || !double.IsFinite(RadiusRangeMm) || RadiusRangeMm is < .001 or > .5
            || !double.IsFinite(ShapeRange) || ShapeRange is < .0001 or > .2
            || !double.IsFinite(HuberDeltaMm) || HuberDeltaMm is < .005 or > .5
            || !double.IsFinite(MaximumRmsMm) || MaximumRmsMm is < .005 or > .35
            || !double.IsFinite(MaximumErrorMm) || MaximumErrorMm < MaximumRmsMm || MaximumErrorMm > .8)
            throw new ArgumentException("拟合设置超出允许范围。");
    }
}

public sealed record TraceDatum(string TraceId, int SourceIndex, double RadiusMm, double AngleRadians,
    double SourceAngleOffsetRadians, double NormalScaleMm, bool IsHoldout);
public sealed record TraceResidual(string TraceId, int SourceIndex, bool IsHoldout, double ErrorMm);
public sealed record TraceCurvePoint(double RadiusMm, double AngleRadians);
public sealed record TraceFitCandidate(string Id, GeometryParameters Parameters, int ShapeOrder,
    double RotationRadians, double RobustCost, double TrainingRmsMm, double HoldoutRmsMm, double MaxErrorMm,
    double ParameterCorrelation, bool BoundaryLimited, bool EligibleForReview,
    ImmutableArray<string> RejectionReasons, ImmutableArray<TraceResidual> Residuals, ImmutableArray<TraceCurvePoint> Curve);
public sealed record TraceFitResult(int SchemaVersion, string RecordType, string AlgorithmVersion,
    string SnapshotSha256, string SessionSha256, GeometryParameters Generated, TraceFitSettings Settings,
    double SearchedRadiusRangeMm, string QualityStatus, string PhysicalValidation,
    ImmutableArray<TraceFitCandidate> Candidates, ImmutableArray<string> Warnings)
{
    public const string Algorithm = "continuous-trace-lm-v1";
    public void Validate(GeometryParameters generated, string snapshotHash, PhotoSession session)
    {
        session.Validate(); Settings.Validate(); generated.Validate();
        if (SchemaVersion != 1 || RecordType != "trace-fit-result" || AlgorithmVersion != Algorithm
            || SnapshotSha256 != snapshotHash || session.SnapshotSha256 != snapshotHash || Generated != generated
            || SessionSha256 != RecordJson.Hash(session) || !session.IsConfirmed || PhysicalValidation != "pending"
            || !double.IsFinite(SearchedRadiusRangeMm) || SearchedRadiusRangeMm < Settings.RadiusRangeMm || SearchedRadiusRangeMm > .5
            || QualityStatus is not ("fit-review" or "model-mismatch" or "candidate-ambiguous" or "search-boundary")
            || Candidates.IsDefaultOrEmpty || Candidates.Length > 4 || Candidates.Any(c => c is null) || Warnings.IsDefault
            || Candidates.Select(c => c.Id).Distinct().Count() != Candidates.Length)
            throw new ArgumentException("拟合结果失效或未绑定当前快照和照片观测。");
        var data = TraceFitter.Prepare(session, generated);
        var original = new TrackGeometry(generated);
        foreach (var c in Candidates)
        {
            c.Parameters.Validate();
            if (string.IsNullOrWhiteSpace(c.Id) || c.Parameters.TotalSectors != generated.TotalSectors
                || c.Parameters.ChannelBitLengthNm != generated.ChannelBitLengthNm || c.Parameters.Direction != generated.Direction
                || c.ShapeOrder < 0 || c.ShapeOrder > Settings.ShapeOrder || !double.IsFinite(c.RotationRadians)
                || !double.IsFinite(c.TrainingRmsMm) || !double.IsFinite(c.HoldoutRmsMm) || !double.IsFinite(c.MaxErrorMm)
                || !double.IsFinite(c.RobustCost) || !double.IsFinite(c.ParameterCorrelation)
                || c.TrainingRmsMm < 0 || c.HoldoutRmsMm < 0 || c.MaxErrorMm < 0
                || c.Curve.IsDefaultOrEmpty || c.Curve.Length > 8193 || c.Residuals.IsDefaultOrEmpty || c.RejectionReasons.IsDefault
                || c.EligibleForReview != (c.RejectionReasons.Length == 0)
                || (c.EligibleForReview && (c.TrainingRmsMm > Settings.MaximumRmsMm || c.HoldoutRmsMm > Settings.MaximumRmsMm
                    || c.MaxErrorMm > Settings.MaximumErrorMm || c.BoundaryLimited))
                || c.Curve.Any(p => p is null || !double.IsFinite(p.RadiusMm) || !double.IsFinite(p.AngleRadians))
                || c.Residuals.Any(p => p is null || !double.IsFinite(p.ErrorMm)))
                throw new ArgumentException("拟合候选或诊断字段无效。");
            if (c.Residuals.Length != data.Length || c.RobustCost < 0
                || (c.ShapeOrder < 1 && c.Parameters.PitchLinear != generated.PitchLinear)
                || (c.ShapeOrder < 2 && c.Parameters.PitchQuadratic != generated.PitchQuadratic)
                || (c.ShapeOrder < 3 && c.Parameters.PitchCubic != generated.PitchCubic))
                throw new ArgumentException("候选改变了固定参数，或遗漏了输入残差。");
            for (int i = 0; i < data.Length; i++)
                if (c.Residuals[i].TraceId != data[i].TraceId || c.Residuals[i].SourceIndex != data[i].SourceIndex || c.Residuals[i].IsHoldout != data[i].IsHoldout)
                    throw new ArgumentException("残差身份或训练/留出划分与输入不一致。");
            double Rms(bool holdout) => Math.Sqrt(c.Residuals.Where(r => r.IsHoldout == holdout).Average(r => r.ErrorMm * r.ErrorMm));
            if (Math.Abs(Rms(false) - c.TrainingRmsMm) > 1e-10 || Math.Abs(Rms(true) - c.HoldoutRmsMm) > 1e-10
                || Math.Abs(c.Residuals.Max(r => Math.Abs(r.ErrorMm)) - c.MaxErrorMm) > 1e-10)
                throw new ArgumentException("汇总误差与全点残差不一致。");
            var model = new ForwardModel(original, new TrackGeometry(c.Parameters));
            double previousRadius = double.NegativeInfinity;
            foreach (var point in c.Curve)
            {
                if (point.RadiusMm <= previousRadius || Math.Abs(model.AtRadius(point.RadiusMm).PhaseRadians + c.RotationRadians - point.AngleRadians) > 2e-8)
                    throw new ArgumentException("连续叠线与候选物理模型不一致。");
                previousRadius = point.RadiusMm;
            }
        }
    }
}

public sealed record TraceFitReview(TraceFitResult Result, string SelectedCandidateId, string? ConfirmedCandidateSha256)
{
    [JsonIgnore] public TraceFitCandidate Selected => Result.Candidates.FirstOrDefault(c => c.Id == SelectedCandidateId)
        ?? throw new ArgumentException("候选已不存在。");
    public string ReviewHash() => RecordJson.Hash(new { resultSha256 = RecordJson.Hash(Result), SelectedCandidateId });
    [JsonIgnore] public bool IsConfirmed => ConfirmedCandidateSha256 == ReviewHash();
    public TraceFitReview Select(string id) => this with { SelectedCandidateId = id, ConfirmedCandidateSha256 = null };
    public TraceFitReview Confirm(GeometryParameters generated, string snapshotHash, PhotoSession session)
    {
        Validate(generated, snapshotHash, session);
        if (!Selected.EligibleForReview || Result.QualityStatus == "candidate-ambiguous")
            throw new ArgumentException("当前候选未通过检查，不能用人工确认替代缺失证据。");
        return this with { ConfirmedCandidateSha256 = ReviewHash() };
    }
    public void Validate(GeometryParameters generated, string snapshotHash, PhotoSession session)
    {
        Result.Validate(generated, snapshotHash, session);
        _ = Selected;
        if (ConfirmedCandidateSha256 is not null && (!IsConfirmed || !Selected.EligibleForReview || Result.QualityStatus == "candidate-ambiguous"))
            throw new ArgumentException("候选确认已失效。");
    }
}

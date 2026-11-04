using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace DvdCalibrator.Core;

public sealed record CoarseAlternative(string Id, GeometryParameters Parameters);
public sealed record CoarsePlanEvidence(string Kind, string SnapshotSha256, string SessionSha256, string ResultSha256,
    string SelectedCandidateId, GeometryParameters Parameters, ImmutableArray<CoarseAlternative> Alternatives);
public sealed record FineForecastCurve(string CandidateId, ImmutableArray<TraceCurvePoint> Curve);
public sealed record FineTargetForecast(double InnerRadiusMm, double OuterRadiusMm, double MaximumCandidateDifferenceMm,
    double MinimumArmSpacingPixels, double MinimumLineWidthPixels, string Status, ImmutableArray<FineForecastCurve> Curves);

public sealed record FineTargetPlan(int SchemaVersion, string RecordType, string Id, DiscSpec Disc,
    CoarsePlanEvidence? Coarse, GeometryParameters Generated, ReferenceTargetContract Target,
    double ExpectedPhotoPixelsPerMm, FineTargetForecast Forecast, string PhysicalValidation,
    string ContentSha256, string? ConfirmedPlanSha256)
{
    public static FineTargetPlan Create(DiscSpec disc, CoarsePlanEvidence coarse, double expectedPixelsPerMm)
    {
        return Create(disc, coarse.Parameters, coarse, expectedPixelsPerMm);
    }
    public static FineTargetPlan CreateManual(DiscSpec disc, GeometryParameters generated, double expectedPixelsPerMm)
        => Create(disc, generated, null, expectedPixelsPerMm);

    private static FineTargetPlan Create(DiscSpec disc, GeometryParameters generated, CoarsePlanEvidence? coarse, double expectedPixelsPerMm)
    {
        var plan = new FineTargetPlan(coarse is null ? 2 : 1, "fine-target-plan", Guid.NewGuid().ToString("N"), disc, coarse,
            generated, ReferenceTargetContract.Default, expectedPixelsPerMm,
            Predict(generated, coarse, expectedPixelsPerMm), "pending", "", null);
        plan = plan with { ContentSha256 = plan.Hash() }; plan.Validate(); return plan;
    }
    private string Hash() => RecordJson.Hash(this with { ContentSha256 = "", ConfirmedPlanSha256 = null });
    [JsonIgnore] public bool IsConfirmed => ConfirmedPlanSha256 == ContentSha256 && ContentSha256 == Hash();
    public FineTargetPlan Confirm()
    {
        Validate();
        if (Forecast.Status != "plan-review") throw new ArgumentException("候选差异或预计精调分辨率未通过检查，请补充证据或重新设置预计像素密度。");
        return this with { ConfirmedPlanSha256 = ContentSha256 };
    }
    public void Validate()
    {
        Disc.Validate(); Generated.Validate(); Target.Validate();
        if (SchemaVersion != (Coarse is null ? 2 : 1) || RecordType != "fine-target-plan" || !Guid.TryParseExact(Id, "N", out _)
            || PhysicalValidation != "pending"
            || Generated.TotalSectors % 16 != 0
            || !double.IsFinite(ExpectedPhotoPixelsPerMm) || ExpectedPhotoPixelsPerMm is < 1 or > 500
            || ContentSha256 != Hash() || (ConfirmedPlanSha256 is not null && (!IsConfirmed || Forecast.Status != "plan-review")))
            throw new ArgumentException("精调计划版本、参数或确认绑定无效。");
        if (Coarse is { } coarse) ValidateCoarse(coarse);
        if (RecordJson.Hash(Predict(Generated, Coarse, ExpectedPhotoPixelsPerMm)) != RecordJson.Hash(Forecast))
            throw new ArgumentException("下一张预测已脱离完整候选模型。");
    }

    private void ValidateCoarse(CoarsePlanEvidence coarse)
    {
        if (coarse.Kind is not ("trace" or "dense") || Generated != coarse.Parameters
            || !RecordJson.IsHash(coarse.SnapshotSha256) || !RecordJson.IsHash(coarse.SessionSha256) || !RecordJson.IsHash(coarse.ResultSha256)
            || coarse.Alternatives.IsDefaultOrEmpty || coarse.Alternatives.Length > 8
            || coarse.Alternatives.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id))
            || coarse.Alternatives.Select(c => c.Id).Distinct().Count() != coarse.Alternatives.Length
            || !coarse.Alternatives.Any(c => c.Id == coarse.SelectedCandidateId && c.Parameters == Generated))
            throw new ArgumentException("粗调证据或候选无效。");
        foreach (var c in coarse.Alternatives)
        {
            c.Parameters.Validate();
            if (c.Parameters.TotalSectors != Generated.TotalSectors || c.Parameters.ChannelBitLengthNm != Generated.ChannelBitLengthNm
                || c.Parameters.Direction != Generated.Direction) throw new ArgumentException("候选不共享位长、容量和方向条件。");
        }
    }

    public void VerifyActualGeneration(ExternalGenerationSnapshot snapshot, Func<string, byte[]> read)
    {
        Validate(); snapshot.ValidateAttachments(read);
        var r = snapshot.GenerationRecord;
        if (!IsConfirmed || !MatchesDisc(snapshot.Declarations.Disc) || r.Generated != Generated
            || snapshot.SourceImageSha256 != Target.PngSha256
            || (snapshot.Declarations.EvidenceSource != "manual-import" && (snapshot.Declarations.GeneratorCommit != Target.UpstreamCommit
                || snapshot.Declarations.GeneratorVersion != "1.3.6")) || r.StartAngleDegrees != Target.StartAngleDegrees
            || r.LuminanceThreshold != Target.LuminanceThreshold || r.AlphaThreshold != Target.AlphaThreshold
            || r.OriginalRecord.GetProperty("psnOffset").GetString() != Target.PsnOffset
            || r.OriginalRecord.GetProperty("imageMapping").GetProperty("sampleEveryChannelBits").GetInt32() != Target.SampleEveryChannelBits)
            throw new CalibrationInputException("fine-generation-mismatch", "第二张实际生成记录不匹配已确认 G1、标靶字节或生成契约；不能沿用 G0 或模拟记录。");
    }

    public void VerifyCalibrationGeneration(CalibrationGeneration snapshot, Func<string, byte[]> read)
    {
        Validate(); snapshot.ValidateAttachments(read);
        if (!IsConfirmed || !MatchesDisc(snapshot.Disc) || snapshot.Generated != Generated)
            throw new CalibrationInputException("fine-generation-mismatch", "辅助标定盘的实际参数与第 4 页选用的参数不一致，请核对第 4 页参数和实际生成记录。");
    }

    private bool MatchesDisc(DiscSpec actual) => Disc == DiscSpec.UnspecifiedDvd || actual == Disc;

    private static FineTargetForecast Predict(GeometryParameters p1, CoarsePlanEvidence? evidence, double pixelsPerMm)
    {
        const string manualId = "manual-parameters";
        ImmutableArray<CoarseAlternative> alternatives = evidence?.Alternatives ?? [new(manualId, p1)];
        if (!double.IsFinite(pixelsPerMm) || pixelsPerMm is < 1 or > 500 || alternatives.IsDefaultOrEmpty || alternatives.Length > 8)
            throw new ArgumentException("预计精调照片像素/mm 或候选数量无效。");
        double lo = Math.Max(p1.InnerRadiusMm, alternatives.Max(c => c.Parameters.InnerRadiusMm)) + .5;
        double hi = Math.Min(p1.OuterRadiusMm, alternatives.Min(c => c.Parameters.OuterRadiusMm)) - .5;
        if (hi - lo < 10) throw new ArgumentException("精调预测的共同有效域不足 10 mm。");
        var curves = ImmutableArray.CreateBuilder<FineForecastCurve>(); double armPixels = 1e9, widthPixels = 1e9;
        foreach (var c in alternatives)
        {
            var model = new ForwardModel(p1, c.Parameters);
            var curve = Enumerable.Range(0, 1025).Select(i => { double r = lo + (hi - lo) * i / 1024; return new TraceCurvePoint(r, model.AtRadius(r).PhaseRadians); }).ToImmutableArray();
            for (int i = 1; i < curve.Length; i++)
            {
                double slope = Math.Abs(curve[i].AngleRadians - curve[i - 1].AngleRadians) * 1024 / (hi - lo);
                if (slope > 1e-12) armPixels = Math.Min(armPixels, pixelsPerMm * Math.PI / (2 * slope));
                double r = curve[i].RadiusMm;
                widthPixels = Math.Min(widthPixels, pixelsPerMm * ReferenceTarget.LineWidthMm(p1.OuterRadiusMm) * r
                    / model.AtRadius(r).SourceRadiusMm / Math.Sqrt(1 + Math.Pow(r * slope, 2)));
            }
            curves.Add(new(c.Id, curve));
        }
        var selected = curves.First(c => c.CandidateId == (evidence?.SelectedCandidateId ?? manualId)).Curve; double difference = 0;
        foreach (var curve in curves)
        {
            double shift = curve.Curve[0].AngleRadians - selected[0].AngleRadians;
            for (int i = 0; i < selected.Length; i++) difference = Math.Max(difference,
                selected[i].RadiusMm * Math.Abs(curve.Curve[i].AngleRadians - selected[i].AngleRadians - shift));
        }
        bool undersampled = curves.Any(c => c.Curve.Zip(c.Curve.Skip(1)).Any(p => Math.Abs(p.First.AngleRadians - p.Second.AngleRadians) > Math.PI / 8));
        string status = undersampled ? "curve-undersampled" : difference > .2 ? "candidate-ambiguous" : armPixels < 8 || widthPixels < 2 ? "photo-resolution-insufficient" : "plan-review";
        return new(lo, hi, difference, armPixels, widthPixels, status, curves.ToImmutable());
    }
}

using System.Collections.Immutable;
using System.IO;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;

namespace SimulationMatrix;

internal sealed record Metrics(double? CoarseRadiusMaxErrorMm, double? FineRadiusMaxErrorMm,
    double? PredictionRmsMm, double? PredictionMaxMm, double? PredictionMaxPhaseRadians,
    double? TrainingRmsMm, double? HoldoutRmsMm, double? OuterContourRmsPx, double? HoleContourRmsPx,
    double? CoarseImageHoldoutError, double? FineLineWidthPixels);
internal sealed record CaseResult(Scenario Scenario, string Outcome, string Stage, string Status, bool MeetsExpectation,
    bool TruthBudgetMet, bool KnownLimitation, Metrics Metrics, ImmutableArray<CalibrationIssue> Issues,
    GeometryParameters? CoarseParameters, GeometryParameters? FineParameters,
    string? CoarsePixelsSha256, string? FinePixelsSha256, string? Error);
internal sealed record MatrixReport(int SchemaVersion, string RecordType, string SoftwareVersion, string Suite,
    string Oracle, string NoiseGenerator, string PhysicalValidation, double RadiusBudgetMm, double PredictionRmsBudgetMm,
    double PredictionMaxBudgetMm, double PhaseBudgetRadians, ImmutableArray<CaseResult> Cases);

internal static class MatrixRun
{
    public const double RadiusBudget = .00005, PredictionRmsBudget = .1, PredictionMaxBudget = .25, PhaseBudget = .1;
    public static CaseResult Run(Scenario scenario, string folder, CancellationToken token)
    {
        string stage = "coarse-photo", status = "not-started"; PhotoTask? coarse = null, fine = null;
        TwoDiscTask? workflow = null; Metrics metrics = new(null, null, null, null, null, null, null, null, null, null, null);
        GeometryParameters? coarseParameters = null, fineParameters = null;
        CaseResult Finish(string outcome, IEnumerable<CalibrationIssue> issues, string? error = null)
        {
            bool budget = metrics.FineRadiusMaxErrorMm is { } r && r <= RadiusBudget
                && metrics.PredictionRmsMm is { } rms && rms <= PredictionRmsBudget
                && metrics.PredictionMaxMm is { } maximum && maximum <= PredictionMaxBudget
                && metrics.PredictionMaxPhaseRadians is { } phase && phase <= PhaseBudget;
            bool known = scenario.Expectation == "characterize" && outcome == "accepted" && !budget;
            var diagnosed = issues.ToImmutableArray();
            if (known) diagnosed = diagnosed.Add(new("shared-calibration-bias", "共同轮廓偏差可能通过两盘一致性检查", "对照原图重新核对实体外缘和真实孔缘；两盘相互一致不能排除共有尺度偏差，仍需独立几何证据。"));
            bool expected = error is null && (scenario.Expectation == "characterize" ? outcome is "accepted" or "rejected"
                : scenario.Expectation == "accept" ? outcome == "accepted" && budget : outcome == "rejected" && diagnosed.Length > 0);
            string[] expectedCodes = scenario.Fault switch
            {
                "blank" or "heavy-occlusion" => ["photo-insufficient"], "wrong-hole" => ["photo-contour-mismatch"],
                "wrong-arm" => ["model-mismatch"], "branch-switch" => ["trace-adjacent-angle"], "reverse-order" => ["trace-radius-order"],
                "masked-point" => ["trace-point-masked"], "short-coverage" => ["trace-coverage"],
                "wrong-g0" => ["fine-generation-mismatch"], "stale-coarse" => ["coarse-plan-stale"],
                "disc-drift" => ["disc-inconsistent", "disc-domain-inconsistent"], "low-resolution" => ["fine-photo-resolution-insufficient"], _ => []
            };
            if (scenario.Expectation == "reject") expected &= expectedCodes.Contains(status);
            // Keep examples bounded. Every case still has scalar outcomes and hashes in the report.
            if (coarse is not null && scenario.Variant == "cw-133.3" && scenario.Seed == 11
                && (scenario.Id.Contains("baseline", StringComparison.Ordinal) || scenario.Id.Contains("combined", StringComparison.Ordinal) || scenario.Expectation == "characterize"))
            {
                string example = Path.Combine(folder, "examples", scenario.Id); Directory.CreateDirectory(example);
                File.WriteAllBytes(Path.Combine(example, "coarse.png"), coarse.Photo.OrientedPng);
                if (fine is not null) File.WriteAllBytes(Path.Combine(example, "fine.png"), fine.Photo.OrientedPng);
                if (workflow is not null && outcome == "accepted") workflow.SaveNew(Path.Combine(example, "task.dvdcal"));
            }
            return new(scenario, outcome, stage, status, expected, budget, known, metrics, diagnosed, coarseParameters, fineParameters,
                coarse?.Photo.Metadata.OrientedPixelsSha256, fine?.Photo.Metadata.OrientedPixelsSha256, error);
        }
        try
        {
            coarse = Scene.Coarse(scenario, token); stage = "coarse-fit";
            var dense = coarse.AnalyzeDense(token: token); coarse.AttachDense(dense);
            var candidate = dense.Candidates.FirstOrDefault(c => c.Supported);
            metrics = metrics with { OuterContourRmsPx = coarse.Session.Rectification!.OuterRmsPixels, HoleContourRmsPx = coarse.Session.Rectification.HoleRmsPixels,
                CoarseImageHoldoutError = candidate?.HoldoutImageError };
            if (candidate is null || dense.QualityStatus == "candidate-ambiguous")
            { status = dense.QualityStatus; return Finish("rejected", CalibrationGuidance.ForDense(dense)); }
            coarseParameters = candidate.Parameters;
            metrics = metrics with { CoarseRadiusMaxErrorMm = RadiusError(candidate.Parameters, scenario.FirstActual) };
            stage = "fine-plan"; workflow = TwoDiscTask.CreateSimulation(coarse);
            if (workflow.Plan.Forecast.Status != "plan-review")
            { status = workflow.Plan.Forecast.Status; return Finish("rejected", [CalibrationGuidance.ForCode(status)]); }
            workflow.ConfirmPlan();
            if (scenario.Fault == "stale-coarse")
                coarse.Session = coarse.Session.WithMasks(coarse.Session.Masks.Add(new([new(1, 1), new(10, 1), new(10, 10)])));
            stage = "fine-photo"; fine = Scene.Fine(scenario, workflow.Plan, token);
            stage = "fine-binding"; workflow.BindFine(fine);
            stage = "fine-fit"; var review = fine.FitTrace(token: token);
            fineParameters = review.Selected.Parameters;
            metrics = metrics with { TrainingRmsMm = review.Selected.TrainingRmsMm, HoldoutRmsMm = review.Selected.HoldoutRmsMm,
                FineRadiusMaxErrorMm = RadiusError(review.Selected.Parameters, scenario.SecondActual) };
            if (!review.Selected.EligibleForReview || review.Result.QualityStatus == "candidate-ambiguous")
            { status = review.Result.QualityStatus; return Finish("rejected", CalibrationGuidance.ForTrace(review)); }
            fine.AttachFit(review.Confirm(fine.Snapshot.Geometry, fine.Snapshot.Hash, fine.Session));
            var prediction = PredictionError(review.Selected.Parameters, scenario.SecondActual, token);
            metrics = metrics with { PredictionRmsMm = prediction.Rms, PredictionMaxMm = prediction.Max, PredictionMaxPhaseRadians = prediction.Phase };
            stage = "two-disc-consistency"; var consistency = workflow.CheckConsistency(); status = consistency.Status;
            metrics = metrics with { FineLineWidthPixels = consistency.FineLineWidthPixels };
            if (status != "final-review") return Finish("rejected", [CalibrationGuidance.ForCode(status)]);
            return Finish("accepted", []);
        }
        catch (OperationCanceledException) { throw; }
        catch (CalibrationInputException error) { status = error.Code; return Finish("rejected", [CalibrationGuidance.ForCode(error.Code)]); }
        catch (Exception error) { status = "unexpected-error"; return Finish("error", [], error.ToString()); }
    }
    private static double RadiusError(GeometryParameters candidate, GeometryParameters truth)
        => Math.Max(Math.Abs(candidate.InnerRadiusMm - truth.InnerRadiusMm), Math.Abs(candidate.OuterRadiusMm - truth.OuterRadiusMm));
    private static (double Rms, double Max, double Phase) PredictionError(GeometryParameters compensation, GeometryParameters actual, CancellationToken token)
    {
        var mapping = new TruthMapping(compensation, actual, token);
        double lo = Math.Max(compensation.InnerRadiusMm, actual.InnerRadiusMm) + .6, hi = Math.Min(compensation.OuterRadiusMm, actual.OuterRadiusMm) - .6;
        var points = Enumerable.Range(0, 257).Select(i => { double r = lo + (hi - lo) * i / 256; return (R: r, P: mapping.At(r).Phase); }).ToArray();
        double shift = points.Average(p => p.P); var errors = points.Select(p => p.R * (p.P - shift)).ToArray();
        return (Math.Sqrt(errors.Average(e => e * e)), errors.Max(Math.Abs), points.Max(p => Math.Abs(p.P - shift)));
    }
}

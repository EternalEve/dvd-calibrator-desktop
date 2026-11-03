using System.Collections.Immutable;

namespace DvdCalibrator.Core;

public sealed record CalibrationIssue(string Code, string Title, string Action);

public sealed class CalibrationInputException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

/// <summary>Stable diagnostic codes with concrete next steps, shared by desktop and reports.</summary>
public static class CalibrationGuidance
{
    public static CalibrationIssue ForCode(string code) => code switch
    {
        "photo-insufficient" or "insufficient-independent-observations" or "fragment-without-training" or "image-class-coverage-insufficient"
            => new(code, "可见图案证据不足", "补拍清晰、少反光且覆盖内中外的记录面照片；保留必要遮罩，再重新分析。"),
        "source-undersampled" or "predicted-source-undersampled" or "next-disc-undersampled" or "fine-photo-resolution-insufficient"
            => new(code, "原图有效分辨率不足", "靠近盘面重新拍摄并对焦，增加实际源像素；放大预览或插值不能恢复条纹。"),
        "photo-resolution-insufficient" => new(code, "预计精调分辨率不足", "调整第二张拍摄计划的有效像素密度，重新创建并核对计划；更高的预计值需要实际拍摄达到。"),
        "photo-contour-mismatch" => new(code, "实体轮廓不能同时吻合", "重新标注真实最外缘和贯通孔缘，覆盖整圈；检查阴影、夹持环及镜头畸变，必要时近正拍重拍。"),
        "photo-rectification-unconfirmed" => new(code, "照片矫正尚未确认", "先计算联合矫正，核对两条实体轮廓和正视预览，再确认。"),
        "trace-missing" or "trace-point-count" => new(code, "描线点数不足", "沿同一条主边界从内向外补点，主线至少 24 点；对侧检查线至少 6 点。"),
        "mixed-trace-point-count" => new(code, "局部描线信息不足", "混合模式的局部主边界或明确对侧 180° 边界需要 6–600 点；沿同一条可见边界补点，不与完整描线入口的要求混淆。"),
        "trace-point-masked" => new(code, "描点位于遮罩内", "移除遮罩内的描点，在可见的同一条边界补点；不要仅为通过检查取消反光遮罩。"),
        "trace-radius-order" => new(code, "描点次序或半径有问题", "检查是否回点、重复点或换臂，按同一条线从内向外重新描；程序不会自动排序掩盖问题。"),
        "trace-adjacent-angle" or "between-point-turn-risk" => new(code, "相邻描点存在漏圈或换臂风险", "对照原图沿同一边界补密点；若无法连续追踪，改用匹配靶型的密环入口或补拍。"),
        "trace-coverage" => new(code, "径向覆盖不足", "在靠内和靠外的可见边界补点，使主线覆盖至少 60% 生成环带；不要靠复制邻近点补数量。"),
        "search-boundary" => new(code, "最佳解接近搜索边界", "先核对生成记录、实体轮廓和描线身份，在支持范围内调整明确的搜索设置；仍触边时补充证据，不能据此确认。"),
        "residual-or-holdout-failed" or "phase-or-radial-holdout-failed" => new(code, "拟合或留出区域不一致", "检查末段和对侧线是否换臂或漏圈，修正错误描点并补充独立证据；不要删除留出点来通过检查。"),
        "raw-image-or-angular-holdout-failed" or "image-polarity-or-contrast-failed" => new(code, "不同角度的原图与模型不一致", "核对源靶、生成记录、反光与局部明暗反转；修正遮罩或重新拍摄，再检查完整重建图。"),
        "mixed-trace-inconsistent" => new(code, "局部描线与密环证据冲突", "确认局部点属于正确的源边界及对侧 180° 身份，修正换臂或偏离中心线的点后重新分析。"),
        "higher-order-not-supported-by-holdout" => new(code, "高阶形状缺少独立支持", "优先检查较简单的有效候选；只有新增形状确实改善连续留出区域时再使用高阶模型。"),
        "curve-undersampled" => new(code, "当前曲线采样不足以安全显示", "保留诊断记录，增加有效观测或使用更合适的范围重新求解；不要依据跳接的预览确认候选。"),
        "candidate-ambiguous" => new(code, "竞争候选仍有明显分歧", "补充独立角度、连续边界或更清楚的照片，以排除冲突候选，再创建第二张计划。"),
        "model-domain-mismatch" => new(code, "模型有效域不能覆盖观测", "核对实际容量、生成内外半径、照片尺度和描点；使用对应本张盘的完整生成记录重新求解。"),
        "fine-generation-mismatch" or "generation-record-mismatch" => new(code, "生成记录与当前任务不匹配", "核对本张盘的冻结旁车、原图和全部生成参数；第二张必须匹配已确认 G1，不能沿用 G0。"),
        "fine-simulation-in-actual" => new(code, "模拟记录不能作为实际生成证据", "实际流程请冻结真实引擎旁车及同一份原图；软件样本仅在模拟任务中使用。"),
        "coarse-plan-stale" => new(code, "第一张改变后旧计划已失效", "保留原任务修订，重新完成第一张分析与核对，再创建新的第二张计划。"),
        "disc-inconsistent" or "disc-domain-inconsistent" => new(code, "两张盘的模型不能共同解释观测", "分别核对 G0/G1、盘片身份、写入条件和轮廓；保留第二张独立结果，排查盘间差异后再决定是否重新采样。"),
        _ => new(code, "当前检查尚未通过", "查看完整诊断记录，核对生成条件、原图和观测后重新检查。")
    };

    public static ImmutableArray<CalibrationIssue> ForTrace(TraceFitReview review)
        => Issues(review.Selected.RejectionReasons.Concat(review.Result.QualityStatus == "candidate-ambiguous" ? ["candidate-ambiguous"] : []));
    public static ImmutableArray<CalibrationIssue> ForDense(DenseAnalysis analysis)
    {
        if (analysis.QualityStatus == "candidate-ambiguous") return Issues(["candidate-ambiguous"]);
        if (analysis.Candidates.Any(c => c.Supported)) return [];
        return analysis.Candidates.IsEmpty ? Issues(["photo-insufficient"])
            : Issues(analysis.Candidates.OrderBy(c => c.TrainingImageError + c.TrainingPhaseRmsRadians).First().RejectionReasons);
    }
    public static ImmutableArray<CalibrationIssue> Issues(IEnumerable<string> codes) => codes.Distinct().Select(ForCode).ToImmutableArray();
    public static string Describe(IEnumerable<CalibrationIssue> issues) => string.Join("\n", issues.Select(i => i.Title + "。" + i.Action));
    public static string Describe(Exception error) => error is CalibrationInputException input
        ? input.Message + "\n建议：" + ForCode(input.Code).Action : error.Message;
}

using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using DvdCalibrator.Core;

namespace SimulationMatrix;

internal static class Reports
{
    public static void Write(string folder, MatrixReport report)
    {
        RecordJson.WriteNew(Path.Combine(folder, "matrix.json"), report);
        var csv = new StringBuilder("case,expectation,outcome,stage,status,meets_expectation,truth_budget_met,known_limitation,coarse_radius_error_mm,fine_radius_error_mm,prediction_rms_mm,prediction_max_mm,holdout_rms_mm,outer_contour_rms_px,hole_contour_rms_px,coarse_image_holdout_error,fine_line_width_pixels,coarse_ri_mm,coarse_ro_mm,fine_ri_mm,fine_ro_mm\n");
        foreach (var c in report.Cases)
            csv.AppendLine(string.Join(',', new[] { c.Scenario.Id, c.Scenario.Expectation, c.Outcome, c.Stage, c.Status,
                c.MeetsExpectation.ToString().ToLowerInvariant(), c.TruthBudgetMet.ToString().ToLowerInvariant(), c.KnownLimitation.ToString().ToLowerInvariant(),
                N(c.Metrics.CoarseRadiusMaxErrorMm), N(c.Metrics.FineRadiusMaxErrorMm), N(c.Metrics.PredictionRmsMm), N(c.Metrics.PredictionMaxMm),
                N(c.Metrics.HoldoutRmsMm), N(c.Metrics.OuterContourRmsPx), N(c.Metrics.HoleContourRmsPx), N(c.Metrics.CoarseImageHoldoutError), N(c.Metrics.FineLineWidthPixels),
                N(c.CoarseParameters?.InnerRadiusMm), N(c.CoarseParameters?.OuterRadiusMm), N(c.FineParameters?.InnerRadiusMm), N(c.FineParameters?.OuterRadiusMm) }));
        File.WriteAllText(Path.Combine(folder, "matrix.csv"), csv.ToString());
        string E(string value) => WebUtility.HtmlEncode(value);
        var graded = report.Cases.Where(c => c.Scenario.Expectation != "characterize").ToArray();
        var html = new StringBuilder("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>两盘模拟验收矩阵</title><style>body{font:15px system-ui;margin:32px;color:#20334b}table{border-collapse:collapse;width:100%}td,th{padding:8px;border:1px solid #ccd6e3;text-align:left}th{background:#edf2f8}.bad{background:#ffebeb}.limit{background:#fff2cf}code{overflow-wrap:anywhere}small{color:#53647a}</style><h1>两盘模拟验收矩阵</h1>");
        html.Append("<p>软件 ").Append(E(report.SoftwareVersion)).Append("；套件 ").Append(E(report.Suite)).Append("；实刻验证 pending。</p><p>判定用例 ").Append(graded.Length)
            .Append("，符合预期 ").Append(graded.Count(c => c.MeetsExpectation)).Append("；边界探测 ").Append(report.Cases.Count(c => c.Scenario.Expectation == "characterize"))
            .Append("；边界探测中软件通过但真值超预算 ").Append(report.Cases.Count(c => c.KnownLimitation)).Append("。这里的数量不是实盘成功率，也不构成随机总体统计。</p>");
        html.Append("<p>密环直接受像素噪声影响；精调为人工描点入口，以单独的点击噪声模拟定位误差。轮廓噪声和透视均先作用于源像素标注，再重新联合矫正。真值由独立 Simpson 积分/局部二分产生，未调用生产正向几何来生成轨迹。</p>")
            .Append("<p>接受用例预先预算：半径最大误差 ≤ ").Append(N(report.RadiusBudgetMm)).Append(" mm，最终配置对真值的下一张径向中心线形状 RMS ≤ ")
            .Append(N(report.PredictionRmsBudgetMm)).Append(" mm、最大 ≤ ").Append(N(report.PredictionMaxBudgetMm)).Append(" mm，最大相位偏差 ≤ ").Append(N(report.PhaseBudgetRadians)).Append(" rad。先去掉一个整体转角，未逐点换圈；大相位时毫米量只作诊断，另有相位预算。阈值是软件矩阵断言，不是实盘精度。</p>");
        foreach (var c in report.Cases.Where(c => c.KnownLimitation || !c.MeetsExpectation))
            html.Append("<p class=\"").Append(c.KnownLimitation ? "limit" : "bad").Append("\"><strong>").Append(E(c.Scenario.Id)).Append("：</strong>")
                .Append(E(c.KnownLimitation ? "软件检查通过，但独立真值误差超预算；共同轮廓偏差未被两盘一致性排除。" : "结果未符合预先断言，请查看本行和 JSON，不能当作通过。"))
                .Append(' ').Append(E(string.Join("；", c.Issues.Select(i => i.Action)))).Append("</p>");
        html.Append("<table><tr><th>用例 / 种子</th><th>配置</th><th>预期 / 观察</th><th>阶段 / 原因</th><th>半径误差 粗 / 精 mm</th><th>图像归一 / 描线 mm 留出</th><th>预测 RMS / 最大 mm</th><th>下一步</th></tr>");
        foreach (var c in report.Cases)
        {
            html.Append("<tr").Append(!c.MeetsExpectation ? " class=\"bad\"" : c.KnownLimitation ? " class=\"limit\"" : "").Append("><td><code>").Append(E(c.Scenario.Id)).Append("</code></td><td>")
                .Append("像素 σ ").Append(N(c.Scenario.PixelNoiseSigmaDn)).Append(" DN<br>点击 σ ").Append(N(c.Scenario.ClickSigmaPx)).Append(" px<br>轮廓 σ ").Append(N(c.Scenario.ContourSigmaPx))
                .Append(" px<br>共同尺度偏差 ").Append(N(c.Scenario.SharedContourScaleBias)).Append("</td><td>").Append(E(c.Scenario.Expectation)).Append(" / ").Append(E(c.Outcome)).Append("</td><td>")
                .Append(E(c.Stage)).Append("<br>").Append(E(c.Status)).Append("</td><td>").Append(N(c.Metrics.CoarseRadiusMaxErrorMm)).Append(" / ").Append(N(c.Metrics.FineRadiusMaxErrorMm))
                .Append("</td><td>").Append(N(c.Metrics.CoarseImageHoldoutError)).Append(" / ").Append(N(c.Metrics.HoldoutRmsMm)).Append("</td><td>").Append(N(c.Metrics.PredictionRmsMm)).Append(" / ")
                .Append(N(c.Metrics.PredictionMaxMm)).Append("</td><td>").Append(E(string.Join("；", c.Issues.Select(i => i.Title + "：" + i.Action))));
            if (c.Error is not null) html.Append("<pre>").Append(E(c.Error)).Append("</pre>");
            html.Append("</td></tr>");
        }
        html.Append("</table><p>完整配置、随机种子、照片像素哈希、诊断和未通过记录见 matrix.json；全部用例都会保留，未知异常不计入正确拒绝。共享轮廓偏差为预先声明的边界探测，单独显示，不混入成功/拒绝断言。样本图和任务包见 examples。</p><p>未覆盖真实光学亮度、镜头径向畸变、所有标靶和完整参数置信区间。此矩阵没有操作刻录设备。</p></html>");
        File.WriteAllText(Path.Combine(folder, "report.html"), html.ToString());
    }
    private static string N(double? value) => value?.ToString("G9", CultureInfo.InvariantCulture) ?? "";
}

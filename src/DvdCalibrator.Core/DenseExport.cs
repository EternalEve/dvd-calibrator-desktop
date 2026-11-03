using System.Globalization;
using System.Net;
using System.Text;

namespace DvdCalibrator.Core;

public static class DenseExport
{
    public static void Write(string path, DenseAnalysis analysis, GenerationSnapshot snapshot, PhotoSession session,
        RgbImage image, CancellationToken token = default)
    {
        analysis.Verify(snapshot, session, image, token);
        OutputFolder.Publish(path, folder =>
        {
            RecordJson.WriteNew(Path.Combine(folder, "dense-analysis.json"), analysis);
            RecordJson.WriteNew(Path.Combine(folder, "generation.json"), snapshot);
            RecordJson.WriteNew(Path.Combine(folder, "photo-session.json"), session);
            var csv = new StringBuilder("radius_mm,spectral_cycles_per_mm,signed_phase_cycles_per_mm,spectral_concentration,effective_pixels_per_cycle\n");
            foreach (var f in analysis.Observations.Frequencies)
                csv.AppendLine(string.Join(',', new[] { f.RadiusMm, f.CyclesPerMm, f.SignedPhaseCyclesPerMm, f.SpectralConcentration, f.EffectivePixelsPerCycle }.Select(N)));
            File.WriteAllText(Path.Combine(folder, "local-frequency.csv"), csv.ToString());
            File.WriteAllText(Path.Combine(folder, "reconstruction.svg"), CurveSvg(analysis, false));
            File.WriteAllText(Path.Combine(folder, "next-disc.svg"), CurveSvg(analysis, true));
            File.WriteAllText(Path.Combine(folder, "report.html"), Report(analysis));
        }, token);
    }

    public static string CurveSvg(DenseAnalysis result, bool next)
    {
        var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 700 760\"><rect width=\"700\" height=\"760\" fill=\"#f5f7fb\"/><g fill=\"none\" stroke=\"#aab4c3\"><circle cx=\"350\" cy=\"350\" r=\"300\"/><circle cx=\"350\" cy=\"350\" r=\"37.5\"/></g>");
        var curves = next ? result.NextDisc?.Curves.Select(c => (c.CandidateId, c.Curve)) ?? []
            : result.Candidates.Where(c => c.Supported).Select(c => (c.Id, c.Curve));
        int index = 0; string[] colors = ["#ea8a23", "#3678bc", "#49946b", "#a86cc1"];
        foreach (var (_, points) in curves)
        {
            if (points.Zip(points.Skip(1)).Any(p => Math.Abs(p.First.AngleRadians - p.Second.AngleRadians) > Math.PI / 8)) continue;
            foreach (double offset in next ? new[] { 0d, Math.PI / 2, Math.PI, 3 * Math.PI / 2 } : new[] { 0d, Math.PI })
            {
                svg.Append($"<polyline fill=\"none\" stroke=\"{colors[index % colors.Length]}\" stroke-width=\"1.5\" points=\"");
                foreach (var p in points) svg.Append(N(350 + 5 * p.RadiusMm * Math.Cos(p.AngleRadians + offset))).Append(',').Append(N(350 - 5 * p.RadiusMm * Math.Sin(p.AngleRadians + offset))).Append(' ');
                svg.Append("\"/>");
            }
            index++;
        }
        svg.Append("<text x=\"24\" y=\"700\" font-size=\"16\" fill=\"#25344a\">").Append(next ? "Next disc / G1 = selected compensation" : "Current disc / full-model supported candidates")
            .Append("</text><text x=\"24\" y=\"730\" font-size=\"13\">Physical validation pending; gaps remain unvalidated</text></svg>");
        return svg.ToString();
    }

    private static string Report(DenseAnalysis a)
    {
        var html = new StringBuilder("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>密环与混合观测诊断</title><style>body{font:16px system-ui;max-width:1100px;margin:40px auto;padding:0 24px;color:#203047}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccd5e0;padding:8px}img{max-width:48%}code{overflow-wrap:anywhere}</style><h1>密环与混合观测诊断</h1>");
        html.Append("<p>状态：").Append(WebUtility.HtmlEncode(a.QualityStatus)).Append("；实刻验证 pending。当前结果供核对，不直接授权制作第二张盘。</p><p>算法：").Append(a.AlgorithmVersion)
            .Append("；相位片段 ").Append(a.Observations.Fragments.Length).Append("；可用径向比例 ").Append(N(a.Observations.UsableRadialFraction)).Append("；原图有效像素/mm ").Append(N(a.Observations.SourcePixelsPerMm))
            .Append("。</p><p>偶数角度提取相位并拟合；奇数角度与每片段末侧连续 25% 留出检查。亮度偏置/增益仅在训练像素拟合，每片段一致；每片段只保留一个整圈分支。</p>")
            .Append("<table><tr><th>候选</th><th>完整参数</th><th>相位 RMS 训练/留出 rad</th><th>图像误差 训练/留出</th><th>诊断</th></tr>");
        foreach (var c in a.Candidates)
        {
            html.Append("<tr><td>").Append(c.Id).Append("<br>极性 ").Append(c.Polarity).Append("</td><td><code>")
                .Append(WebUtility.HtmlEncode(System.Text.Json.JsonSerializer.Serialize(c.Parameters, RecordJson.Options)))
                .Append("</code></td><td>").Append(N(c.TrainingPhaseRmsRadians)).Append(" / ").Append(N(c.HoldoutPhaseRmsRadians))
                .Append("</td><td>").Append(N(c.TrainingImageError)).Append(" / ").Append(N(c.HoldoutImageError)).Append("</td><td>")
                .Append(c.Supported ? "支持，待核对" : WebUtility.HtmlEncode(string.Join(", ", c.RejectionReasons))).Append("</td></tr>");
        }
        html.Append("</table><h2>下一张预测</h2>");
        if (a.NextDisc is { } next) html.Append("<p>状态 ").Append(next.Status).Append("；候选间最大形状差 ").Append(N(next.MaximumCandidateDifferenceMm))
            .Append(" mm；最小每圈像素 ").Append(N(next.MinimumPixelsPerTurn)).Append("；十字臂间最小像素 ").Append(N(next.MinimumPixelsBetweenArms))
            .Append("；线宽最小像素 ").Append(N(next.MinimumLineWidthPixels)).Append("。预测靶为 0.3 mm 解析十字，图中显示四条中心线。固定下一张生成模型为首个受支持候选，逐个实际模型重新计算完整正向曲线，整体转角对齐后比较。</p>");
        else html.Append("<p>证据不足，没有可支持的下一张预测。</p>");
        html.Append("<img src=\"reconstruction.svg\" alt=\"第一张重建\"><img src=\"next-disc.svg\" alt=\"下一张预测\"><h2>限制与警告</h2><ul>");
        foreach (var warning in a.Warnings.Concat(a.Observations.Warnings)) html.Append("<li>").Append(WebUtility.HtmlEncode(warning)).Append("</li>");
        return html.Append("</ul><p>仅支持已冻结的解析半盘靶。固定 b/N/方向/轨距形状，仅搜索两个半径；有限搜索不证明物理参数唯一。缺失区、未观测区、镜头畸变和轮廓不确定度未通过本次拟合验证。8 像素/周期是待实盘校验的工程筛查阈值。半盘的全局明暗反转对应 π 转角歧义，报告同时保留。重建线跨越缺失区的部分是模型外推，需对原图检查。</p></html>").ToString();
    }
    private static string N(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
}

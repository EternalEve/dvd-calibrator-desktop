using System.Globalization;
using System.Net;
using System.Text;

namespace DvdCalibrator.Core;

public static class FineTargetExport
{
    public static void Write(string output, FineTargetPlan plan, CancellationToken token = default)
    {
        plan.Validate(); if (!plan.IsConfirmed) throw new ArgumentException("请先核对第一张证据、预计精调分辨率和下一张预测，再确认计划。");
        OutputFolder.Publish(output, folder =>
        {
            RecordJson.WriteNew(Path.Combine(folder, "fine-target-plan.json"), plan);
            File.WriteAllBytes(Path.Combine(folder, "target_slim.png"), ReferenceTarget.PngBytes());
            File.WriteAllText(Path.Combine(folder, "next-disc.svg"), ForecastSvg(plan));
            // Structured token array avoids generated shell quoting and implicit geometry defaults.
            var p = plan.Generated;
            string N(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
            string[] arguments = ["solve", "--image", "target_slim.png", "--iso-output", "fine.iso", "--total-sectors", p.TotalSectors.ToString(CultureInfo.InvariantCulture),
                "--fill-sectors", p.TotalSectors.ToString(CultureInfo.InvariantCulture), "--inner-radius-mm", N(p.InnerRadiusMm), "--outer-radius-mm", N(p.OuterRadiusMm),
                "--channel-bit-nm", N(p.ChannelBitLengthNm), "--pitch-linear", N(p.PitchLinear), "--pitch-quadratic", N(p.PitchQuadratic), "--pitch-cubic", N(p.PitchCubic),
                "--spiral-direction", p.Direction == SpiralDirection.Cw ? "cw" : "ccw", "--lba", "0", "--psn-offset", "0x30000", "--start-angle-deg", "0", "--image-threshold", "128", "--alpha-threshold", "128", "--constraint-step", "1",
                "--algorithm", "dispersion", "--fast-output", "true", "--fast-parallelism", "1"];
            RecordJson.WriteNew(Path.Combine(folder, "generator-arguments.json"), arguments);
            File.WriteAllText(Path.Combine(folder, "report.html"), $"<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>第二张生成计划</title><style>body{{font:16px system-ui;max-width:1000px;margin:40px auto}}pre{{white-space:pre-wrap}}img{{max-width:700px}}</style><h1>第二张生成计划</h1><p>已确认软件计划；实际生成记录及实刻验证仍待提供。本目录不会自动生成 ISO。</p><p>使用固定 DiscImageStudio 1.3.6 / {plan.Target.UpstreamCommit}。原图哈希 {plan.Target.PngSha256}，禁止重采样、换图、移动或旋转图层。结构化命令参数见 generator-arguments.json，以本目录为工作目录执行生成器。</p><p>预计精调照片 {N(plan.ExpectedPhotoPixelsPerMm)} 像素/mm，是计划输入。预测最小臂间距 {N(plan.Forecast.MinimumArmSpacingPixels)} 像素，最小线宽 {N(plan.Forecast.MinimumLineWidthPixels)} 像素，候选差 {N(plan.Forecast.MaximumCandidateDifferenceMm)} mm；这些是工程筛查，不是实刻通过结论。</p><pre>{WebUtility.HtmlEncode(System.Text.Json.JsonSerializer.Serialize(plan.Generated, RecordJson.Options))}</pre><img src=\"next-disc.svg\" alt=\"完整候选精调中心线预测\"><p>圆环按源图像素半径 490–1130 随 G1 外半径缩放；当前描线拟合仅使用已确认的十字中心线，不把圆环当角度约束。生成后冻结实际旁车与这份原图，再导入第二张照片。回读将逐项核对 G1、图片哈希、版本声明、起始角、阈值和采样设置。</p></html>");
        }, token);
    }

    public static string ForecastSvg(FineTargetPlan plan)
    {
        var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-64 -68 128 138\"><rect x=\"-64\" y=\"-68\" width=\"128\" height=\"138\" fill=\"#f4f7fb\"/><circle r=\"60\" fill=\"white\" stroke=\"#bbc\" stroke-width=\".2\"/><circle r=\"7.5\" fill=\"#dde\"/>");
        foreach (var c in plan.Forecast.Curves)
        {
            if (c.Curve.Zip(c.Curve.Skip(1)).Any(p => Math.Abs(p.First.AngleRadians - p.Second.AngleRadians) > Math.PI / 8)) continue;
            foreach (double offset in new[] { 0d, Math.PI / 2, Math.PI, 3 * Math.PI / 2 })
            {
                svg.Append("<polyline fill=\"none\" stroke=\"#bd751c\" stroke-width=\".2\" points=\"");
                foreach (var p in c.Curve) svg.Append((p.RadiusMm * Math.Cos(p.AngleRadians + offset)).ToString("G17", CultureInfo.InvariantCulture))
                    .Append(',').Append((-p.RadiusMm * Math.Sin(p.AngleRadians + offset)).ToString("G17", CultureInfo.InvariantCulture)).Append(' ');
                svg.Append("\"/>");
            }
        }
        return svg.Append("<text x=\"-60\" y=\"65\" font-size=\"2.4\">G1 frozen; exact reference PNG; physical validation pending</text></svg>").ToString();
    }
}

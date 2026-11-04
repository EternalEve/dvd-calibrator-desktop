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
            File.WriteAllText(Path.Combine(folder, "粗调参数.txt"), $"在 DiscImageStudio 的 DVD 参数设置中逐项填写，再到辅助标定页从当前参数创建标靶。\n内半径 (mm): {N(p.InnerRadiusMm)}\n外半径 (mm): {N(p.OuterRadiusMm)}\n扇区数: {p.TotalSectors}\n一阶轨距: {N(p.PitchLinear)}\n二阶轨距: {N(p.PitchQuadratic)}\n三阶轨距: {N(p.PitchCubic)}\n位长 (nm): {N(p.ChannelBitLengthNm)}\n方向: {p.Direction}\n辅助标定页支持顺时针及 133.3 nm；其他设置请使用完整参数生成路径。\n生成并刻录后，回本工具提交输出 ISO 同目录、同名的 .calibration.json 和记录面照片。\n");
            File.WriteAllText(Path.Combine(folder, "report.html"), Report(plan));
        }, token);
    }

    private static string Report(FineTargetPlan plan)
    {
        string N(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
        return $$"""
            <!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>制作精调盘</title>
            <style>body{font:16px/1.8 system-ui;max-width:900px;margin:40px auto;padding:20px}pre{white-space:pre-wrap}img{max-width:100%}</style>
            <h1>用粗调结果制作第二张辅助标定盘</h1>
            <p>打开本目录的“粗调参数.txt”，在 DiscImageStudio 的 DVD 参数设置中填入内外半径、扇区数和三项轨距系数。
            进入辅助标定页，从当前参数创建标靶，选择 ISO 输出位置并生成，再刻录第二张同类型空白盘。</p>
            <p>辅助标定入口使用顺时针、133.3 nm 位长。当前方案：{{plan.Generated.Direction}} / {{N(plan.Generated.ChannelBitLengthNm)}} nm。
            若不匹配，需用支持完整参数的生成入口及 generator-arguments.json，不能静默套用默认值。</p>
            <p>第二张生成后，到所选 ISO 的输出目录寻找同名 <code>.calibration.json</code>，
            在本工具第 5 步点击“导入第二张照片”提交该记录，再选这张盘的记录面原始照片。无需命令行整理记录。
            schema 6 辅助标定记录已经确定源图，无需另交 PNG。</p>
            <p>target_slim.png 是精调原图；next-disc.svg 是预测，不能当作刻录源图。若使用普通图片生成入口，提交其 .iso.json 和实际源图。</p>
            <img src="next-disc.svg" alt="第二张预测"><p>计划已确认；生成、刻录和拍摄由用户完成。</p>
            """;
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

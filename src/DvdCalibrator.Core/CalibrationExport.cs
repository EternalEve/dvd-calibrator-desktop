using System.Globalization;
using System.Net;
using System.Text;
using DiscImageStudio.Core;

namespace DvdCalibrator.Core;

public sealed record CompensationProfile(int SchemaVersion, string RecordType, string ProfileKind,
    string SnapshotSha256, string SessionSha256, string FitResultSha256, string ReviewSha256,
    GeometryParameters Parameters, string PhysicalValidation, bool DiscImageProjectCompatible,
    string ApplicationInstructions);

public static class CalibrationExport
{
    public static bool ProjectCompatible(GeometryParameters p) => p.Direction == SpiralDirection.Cw
        && p.ChannelBitLengthNm == 133.3 && p.StartLba == 0 && p.TotalSectors % 16 == 0;

    public static void Write(string output, TraceFitReview review, PhotoSession session, CancellationToken token = default)
    {
        review.Validate(review.Result.Generated, session.SnapshotSha256, session);
        if (!review.IsConfirmed) throw new ArgumentException("请先检查整条预测曲线并确认当前候选，再导出配置。");
        var p = review.Selected.Parameters;
        bool compatible = ProjectCompatible(p);
        var profile = new CompensationProfile(1, "compensation-profile", "fixed-bit-length-equivalent-compensation",
            session.SnapshotSha256, RecordJson.Hash(session), RecordJson.Hash(review.Result), review.ReviewHash(), p, "pending", compatible,
            compatible ? "在兼容版本的 DiscImageStudio 打开 candidate.discproj，导入要刻录的图像并核对生成设置。此工程只有补偿几何，没有预置图层。"
                : "目标工程格式不能表达当前位长或方向。请使用支持完整参数的生成器 CLI，并逐项传入配置字段；不能恢复成默认位长或方向。");
        string destination = Path.GetFullPath(output);
        OutputFolder.Publish(destination, folder =>
        {
            RecordJson.WriteNew(Path.Combine(folder, "calibration-profile.json"), profile);
            RecordJson.WriteNew(Path.Combine(folder, "fit-review.json"), review);
            RecordJson.WriteNew(Path.Combine(folder, "photo-session.json"), session);
            using (var csv = new StreamWriter(new FileStream(Path.Combine(folder, "residuals.csv"), FileMode.CreateNew)))
            {
                csv.WriteLine("trace_id,source_point_index,is_holdout,normal_error_mm");
                foreach (var r in review.Selected.Residuals) csv.WriteLine($"{r.TraceId},{r.SourceIndex},{r.IsHoldout.ToString().ToLowerInvariant()},{F(r.ErrorMm)}");
            }
            File.WriteAllText(Path.Combine(folder, "curve.svg"), CurveSvg(review, session));
            if (compatible)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var project = new ProjectDocument(2, "DVD calibration candidate", "dvd",
                    new(p.InnerRadiusMm, p.OuterRadiusMm, p.TotalSectors, p.PitchLinear, p.PitchQuadratic, p.PitchCubic), [],
                    new(Path.Combine(destination, "drawing.iso"), false, "", "DVD_CALIBRATOR", false), null, null, now, now);
                string projectPath = Path.Combine(folder, "candidate.discproj");
                ProjectJsonStore.Save(projectPath, project);
                var loaded = ProjectJsonStore.Load(projectPath);
                if (loaded.Geometry != project.Geometry) throw new IOException("目标版本工程回读参数不一致。");
            }
            File.WriteAllText(Path.Combine(folder, "report.html"), Report(profile, review));
        }, token);
    }

    public static string CurveSvg(TraceFitReview review, PhotoSession session)
    {
        var c = review.Selected; var transform = session.GetTransform();
        if (c.RejectionReasons.Contains("curve-undersampled"))
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 600 80\"><text x=\"10\" y=\"40\">Curve undersampled. Review diagnostic JSON; export is blocked.</text></svg>";
        var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-64 -70 128 140\"><rect x=\"-64\" y=\"-70\" width=\"128\" height=\"140\" fill=\"#f4f7fb\"/><circle r=\"60\" fill=\"white\" stroke=\"#aab\" stroke-width=\".2\"/><circle r=\"7.5\" fill=\"#dde\"/>");
        foreach (var trace in session.Traces)
        {
            double offset = trace.Id == "validation-180" ? Math.PI : 0;
            svg.Append("<polyline fill=\"none\" stroke=\"#d97318\" stroke-width=\".22\" points=\"");
            foreach (var p in c.Curve) svg.Append(F(p.RadiusMm * Math.Cos(p.AngleRadians + offset))).Append(',').Append(F(-p.RadiusMm * Math.Sin(p.AngleRadians + offset))).Append(' ');
            svg.Append("\"/>");
            for (int i = 0; i < trace.Points.Length; i++)
            {
                var p = transform.SourceToDisc(trace.Points[i]);
                bool holdout = c.Residuals.First(r => r.TraceId == trace.Id && r.SourceIndex == i).IsHoldout;
                svg.Append($"<circle cx=\"{F(p.X)}\" cy=\"{F(-p.Y)}\" r=\".22\" fill=\"{(holdout ? "#087c66" : "#2563eb")}\"/>");
            }
        }
        svg.Append("<text x=\"-60\" y=\"-65\" font-size=\"2.8\">Continuous prediction: orange; training: blue; holdout: green</text><text x=\"-60\" y=\"66\" font-size=\"2.7\">Equivalent compensation; physical validation pending</text></svg>");
        return svg.ToString();
    }

    private static string Report(CompensationProfile profile, TraceFitReview review)
    {
        var c = review.Selected; string E(string s) => WebUtility.HtmlEncode(s);
        string Warning(string code) => code switch
        {
            "fixed-bit-length-equivalent-compensation-not-unique-physical-measurement" => "参数是固定位长条件下的等效补偿，并非唯一物理测量。",
            "finite-multistart-search-not-exhaustive" => "搜索采用有限多起点，没有穷尽全部参数和绕行分支。",
            "unwrap-assumes-no-missing-turns-inspect-entire-photo-and-between-points" => "角度展开依赖描点间未遗漏整圈，请对照原图检查整条边界。",
            "unobserved-radii-and-physical-output-not-validated" => "未观测的半径和最终实刻效果没有验证证据。",
            "parameters-correlated-prediction-and-parameter-uniqueness-are-different" => "参数相关性较高，曲线吻合不代表参数唯一。", _ => code
        };
        var p = profile.Parameters;
        string parameters = $"<h2>完整补偿参数</h2><table><tr><td>ri / ro (mm)</td><td>{F(p.InnerRadiusMm)} / {F(p.OuterRadiusMm)}</td></tr><tr><td>N / b (nm)</td><td>{p.TotalSectors} / {F(p.ChannelBitLengthNm)}</td></tr><tr><td>c1 / c2 / c3</td><td>{F(p.PitchLinear)} / {F(p.PitchQuadratic)} / {F(p.PitchCubic)}</td></tr><tr><td>方向 / 起始 LBA</td><td>{p.Direction} / {p.StartLba}</td></tr></table>";
        return $"<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>DVD 标定报告</title><style>body{{font:16px system-ui;max-width:920px;margin:40px auto;padding:20px;color:#23364b}}td,th{{padding:8px;border-bottom:1px solid #ddd}}img{{width:100%;max-height:600px}}</style><h1>DVD 描线补偿报告</h1><p>等效补偿参数 · 实刻验证：pending</p><p>已人工核对候选 {E(c.Id)}，照片转角仅用于叠加，不写入生成起始角。</p><table><tr><th>训练 RMS</th><th>留出 RMS</th><th>最大偏差</th><th>最大参数列相关</th></tr><tr><td>{F(c.TrainingRmsMm)} mm</td><td>{F(c.HoldoutRmsMm)} mm</td><td>{F(c.MaxErrorMm)} mm</td><td>{F(c.ParameterCorrelation)}</td></tr></table>{parameters}<img src=\"curve.svg\" alt=\"完整连续曲线与留出点\"><p>{E(profile.ApplicationInstructions)}</p><p>目标兼容基准：{DiscImageRecord.UpstreamCommit}。工程回读只验证可表达参数，不代表软件 UI 已应用或实刻通过。</p><h2>限制</h2><ul>{string.Join("", review.Result.Warnings.Select(w => "<li>" + E(Warning(w)) + "</li>"))}</ul><p>完整双精度参数见 calibration-profile.json；残差含全部输入点，见 residuals.csv。未观测半径不具有验证证据。</p></html>";
    }
    private static string F(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
}

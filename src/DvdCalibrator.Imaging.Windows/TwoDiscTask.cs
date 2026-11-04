using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DvdCalibrator.Core;

namespace DvdCalibrator.Imaging.Windows;

public sealed class TwoDiscTask
{
    public const double MinimumFineLineWidthPixels = 2;
    public string Id { get; }
    public string EvidenceMode { get; }
    public PhotoTask? Coarse { get; }
    public FineTargetPlan Plan { get; private set; }
    public PhotoTask? Fine { get; private set; }
    public bool UsesManualParameters => EvidenceMode == "manual-parameters";
    public string Status => !Plan.IsConfirmed ? "plan-review" : Fine is null ? "awaiting-fine-photo"
        : Fine.Fit is null ? "fine-annotation" : !Fine.Fit.IsConfirmed ? "fine-fit-review" : "final-review";
    private TwoDiscTask(string id, string mode, PhotoTask? coarse, FineTargetPlan plan, PhotoTask? fine)
    { Id = id; EvidenceMode = mode; Coarse = coarse; Plan = plan; Fine = fine; Validate(); }

    public static TwoDiscTask Create(PhotoTask coarse, double expectedFinePixelsPerMm = 8, string? denseCandidateId = null)
        => CreateInternal(coarse, expectedFinePixelsPerMm, denseCandidateId, "external-record-required");
    public static TwoDiscTask CreateSimulation(PhotoTask coarse, double expectedFinePixelsPerMm = 8)
        => CreateInternal(coarse, expectedFinePixelsPerMm, null, "simulation");
    public static TwoDiscTask CreateManual(DiscSpec disc, GeometryParameters parameters, double expectedFinePixelsPerMm = 8, PhotoTask? coarse = null)
        => new(Guid.NewGuid().ToString("N"), "manual-parameters", coarse, FineTargetPlan.CreateManual(disc, parameters, expectedFinePixelsPerMm), null);
    private static TwoDiscTask CreateInternal(PhotoTask coarse, double pixels, string? id, string mode)
    {
        var evidence = Evidence(coarse, id);
        var plan = FineTargetPlan.Create(Disc(coarse.Snapshot), evidence, pixels);
        return new(Guid.NewGuid().ToString("N"), mode, coarse, plan, null);
    }
    public static DiscSpec Disc(SnapshotBundle bundle)
    {
        using var doc = RecordJson.ParseDocument(Encoding.UTF8.GetString(bundle.Files["generation.json"]).TrimStart('\uFEFF'));
        return doc.RootElement.GetProperty("recordType").GetString() switch
        {
            "generation-snapshot" => RecordJson.Parse<GenerationSnapshot>(doc.RootElement.GetRawText()).Disc,
            "external-generation-snapshot" => RecordJson.Parse<ExternalGenerationSnapshot>(doc.RootElement.GetRawText()).Declarations.Disc,
            "upstream-calibration-generation" => RecordJson.Parse<CalibrationGeneration>(doc.RootElement.GetRawText()).Disc,
            _ => throw new ArgumentException("第一张必须有完整生成快照。")
        };
    }
    private static CoarsePlanEvidence Evidence(PhotoTask task, string? denseId = null, string? kind = null, CancellationToken token = default)
    {
        task.Validate();
        if (kind == "trace" || (kind is null && task.Fit?.IsConfirmed == true))
        {
            var review = task.Fit ?? throw new ArgumentException("第一张描线结果已失效。");
            review.Validate(task.Snapshot.Geometry, task.Snapshot.Hash, task.Session);
            if (!review.IsConfirmed || !review.Selected.EligibleForReview || review.Result.QualityStatus == "candidate-ambiguous")
                throw new ArgumentException("请先核对并确认第一张描线候选。");
            return new("trace", task.Snapshot.Hash, RecordJson.Hash(task.Session), RecordJson.Hash(review), review.SelectedCandidateId,
                review.Selected.Parameters, review.Result.Candidates.Where(c => c.EligibleForReview).Select(c => new CoarseAlternative(c.Id, c.Parameters)).ToImmutableArray());
        }
        var dense = task.Dense ?? throw new ArgumentException("请先完成第一张描线确认或密环分析。");
        dense.Verify(task.AnalyticSnapshot(), task.Session, task.Photo.Image, token);
        var selected = denseId is null ? dense.Candidates.FirstOrDefault(c => c.Supported) : dense.Candidates.FirstOrDefault(c => c.Id == denseId && c.Supported);
        if (selected is null || dense.QualityStatus == "candidate-ambiguous") throw new ArgumentException("第一张尚无无冲突的受支持候选。");
        return new("dense", task.Snapshot.Hash, RecordJson.Hash(task.Session), RecordJson.Hash(dense), selected.Id, selected.Parameters,
            dense.Candidates.Where(c => c.Supported).Select(c => new CoarseAlternative(c.Id, c.Parameters)).ToImmutableArray());
    }
    public void Validate() => Validate(default);
    private void Validate(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(Id, "N", out _) || EvidenceMode is not ("simulation" or "external-record-required" or "manual-parameters")
            || UsesManualParameters != (Plan.Coarse is null))
            throw new ArgumentException("两盘任务身份或证据模式无效。");
        Plan.Validate();
        if (Plan.Coarse is { } evidence)
        {
            var coarse = Coarse ?? throw new ArgumentException("两盘任务缺少第一张资料。");
            if (coarse.Snapshot.Hash != evidence.SnapshotSha256 || RecordJson.Hash(coarse.Session) != evidence.SessionSha256)
                throw new CalibrationInputException("coarse-plan-stale", "第一张输入已改变，请重新创建第二张计划。");
            var current = Evidence(coarse, evidence.SelectedCandidateId, evidence.Kind, token);
            if (RecordJson.Hash(current) != RecordJson.Hash(evidence) || Disc(coarse.Snapshot) != Plan.Disc)
                throw new CalibrationInputException("coarse-plan-stale", "第一张输入或候选已改变，请重新创建第二张计划。");
        }
        else Coarse?.Validate();
        if (Fine is not null) ValidateFine(Fine);
    }
    public void ConfirmPlan() { Validate(); Plan = Plan.Confirm(); }
    public void BindFine(PhotoTask fine, bool replaceExisting = false)
    {
        Validate(); ValidateFine(fine);
        if (Fine is not null && !replaceExisting) throw new ArgumentException("第二张已绑定。");
        Fine = fine;
    }
    public void RemoveFine() => Fine = null;
    private void ValidateFine(PhotoTask fine)
    {
        fine.Validate();
        if (!Plan.IsConfirmed || fine.Snapshot.Id == Coarse?.Snapshot.Id || fine.Snapshot.Hash == Coarse?.Snapshot.Hash
            || fine.Snapshot.Geometry != Plan.Generated || ReferenceEquals(fine, Coarse)
            || fine.Photo.Metadata.OriginalSha256 == Coarse?.Photo.Metadata.OriginalSha256
            || fine.Photo.Metadata.OrientedPixelsSha256 == Coarse?.Photo.Metadata.OrientedPixelsSha256)
            throw new CalibrationInputException("fine-generation-mismatch", "第二张必须关联已确认的 G1，不能复用第一张 G0 或其照片任务。");
        using var doc = RecordJson.ParseDocument(Encoding.UTF8.GetString(fine.Snapshot.Files["generation.json"]).TrimStart('\uFEFF'));
        if (doc.RootElement.GetProperty("recordType").GetString() == "reference-target-simulation")
        {
            var simulation = RecordJson.Parse<ReferenceTargetSimulation>(doc.RootElement.GetRawText()); simulation.Validate();
            if (EvidenceMode != "simulation" || simulation.PlanSha256 != Plan.ContentSha256 || simulation.Disc != Plan.Disc)
                throw new CalibrationInputException("fine-simulation-in-actual", "模拟照片不能用于要求实际生成记录的两盘任务。");
        }
        else if (doc.RootElement.GetProperty("recordType").GetString() == "external-generation-snapshot")
        {
            Plan.VerifyActualGeneration(RecordJson.Parse<ExternalGenerationSnapshot>(doc.RootElement.GetRawText()), name => fine.Snapshot.Files[name]);
        }
        else if (doc.RootElement.GetProperty("recordType").GetString() == "upstream-calibration-generation")
            Plan.VerifyCalibrationGeneration(RecordJson.Parse<CalibrationGeneration>(doc.RootElement.GetRawText()), name => fine.Snapshot.Files[name]);
        else throw new ArgumentException("第二张需要匹配精调计划的真实旁车快照；普通合成图不能代替参考靶生成记录。");
        if (fine.Fit is not null && fine.Fit.Result.Generated != Plan.Generated) throw new ArgumentException("第二张拟合未使用 G1。");
    }

    public TwoDiscConsistency CheckConsistency() => CheckConsistency(default);
    public TwoDiscConsistency CheckConsistency(CancellationToken token)
    {
        Validate(token);
        var coarse = Coarse ?? throw new ArgumentException("两盘一致性检查需要第一张资料。");
        var evidence = Plan.Coarse ?? throw new ArgumentException("当前使用手动参数，按单盘精调处理。");
        if (Fine?.Fit?.IsConfirmed != true) throw new ArgumentException("请先完成并确认第二张独立精调曲线。");
        var p2 = Fine.Fit.Selected.Parameters; var g0 = coarse.Snapshot.Geometry;
        var referenceRadii = evidence.Kind == "trace" ? TraceFitter.Prepare(coarse.Session, g0).Select(d => d.RadiusMm).ToArray()
            : coarse.Dense!.Observations.Fragments.SelectMany(f => f.Points.Select(p => p.RadiusMm)).ToArray();
        var p2Model = new ForwardModel(g0, p2); double bestRms = double.PositiveInfinity, bestMaximum = double.PositiveInfinity;
        foreach (var candidate in evidence.Alternatives)
        {
            token.ThrowIfCancellationRequested();
            if (referenceRadii.Any(r => r < p2.InnerRadiusMm || r > p2.OuterRadiusMm)) continue;
            var oldModel = new ForwardModel(g0, candidate.Parameters);
            var differences = referenceRadii.Select(r => p2Model.AtRadius(r).PhaseRadians - oldModel.AtRadius(r).PhaseRadians).ToArray();
            double shift = differences.Average(); var errors = new List<double>();
            for (int i = 0; i < referenceRadii.Length; i++)
            {
                double r = referenceRadii[i], a = Math.Max(candidate.Parameters.InnerRadiusMm, r - .01), b = Math.Min(candidate.Parameters.OuterRadiusMm, r + .01);
                double slope = (oldModel.AtRadius(b).PhaseRadians - oldModel.AtRadius(a).PhaseRadians) / (b - a);
                errors.Add((differences[i] - shift) * r / Math.Sqrt(1 + Math.Pow(r * slope, 2)));
            }
            double rms = Math.Sqrt(errors.Average(e => e * e));
            if (rms < bestRms) { bestRms = rms; bestMaximum = errors.Max(Math.Abs); }
        }
        var (pixels, width) = FineResolution(Fine);
        if (!double.IsFinite(bestRms)) return new("disc-domain-inconsistent", 1e9, 1e9, pixels, width);
        return new(bestRms > .35 || bestMaximum > .8 ? "disc-inconsistent" : width < MinimumFineLineWidthPixels ? "fine-photo-resolution-insufficient" : "final-review",
            bestRms, bestMaximum, pixels, width);
    }
    public double CheckFinePhotoLineWidth(CancellationToken token = default)
    {
        Validate(token);
        if (Fine?.Fit?.IsConfirmed != true) throw new ArgumentException("请先确认精调曲线。");
        return FineResolution(Fine).Width;
    }
    private static (double Pixels, double Width) FineResolution(PhotoTask fine)
    {
        var generated = fine.Snapshot.Geometry;
        var selected = fine.Fit!.Selected;
        double pixels = MinimumPhotoPixelsPerMm(fine.Session, generated);
        double width = double.PositiveInfinity;
        var fineModel = new ForwardModel(generated, selected.Parameters);
        var curve = selected.Curve;
        for (int i = 1; i < curve.Length; i++)
        {
            double r = curve[i].RadiusMm, slope = (curve[i].AngleRadians - curve[i - 1].AngleRadians) / (r - curve[i - 1].RadiusMm);
            width = Math.Min(width, pixels * ReferenceTarget.LineWidthMm(generated.OuterRadiusMm) * r / fineModel.AtRadius(r).SourceRadiusMm / Math.Sqrt(1 + Math.Pow(r * slope, 2)));
        }
        return (pixels, width);
    }
    private static double MinimumPhotoPixelsPerMm(PhotoSession session, GeometryParameters generated)
    {
        var h = session.GetTransform(); double minimum = double.PositiveInfinity;
        for (int i = 0; i < 16; i++) foreach (double r in new[] { generated.InnerRadiusMm + .5, (generated.InnerRadiusMm + generated.OuterRadiusMm) / 2, generated.OuterRadiusMm - .5 })
        {
            double x = r * Math.Cos(i * Math.PI / 8), y = r * Math.Sin(i * Math.PI / 8);
            var p = h.DiscToSource(new(x, y)); var a = h.DiscToSource(new(x + .1, y)); var b = h.DiscToSource(new(x, y + .1));
            double ax = (a.X - p.X) * 10, ay = (a.Y - p.Y) * 10, bx = (b.X - p.X) * 10, by = (b.Y - p.Y) * 10;
            double trace = ax * ax + ay * ay + bx * bx + by * by, det = ax * by - ay * bx;
            minimum = Math.Min(minimum, Math.Sqrt(Math.Max(0, (trace - Math.Sqrt(Math.Max(0, trace * trace - 4 * det * det))) / 2)));
        }
        return minimum;
    }
    public void ExportFinal(string output, CancellationToken token = default)
    {
        if (UsesManualParameters)
        {
            if (CheckFinePhotoLineWidth(token) < MinimumFineLineWidthPixels)
                throw new CalibrationInputException("fine-photo-resolution-insufficient", "请补拍更清晰的标定盘照片。");
            CalibrationExport.Write(output, Fine!.Fit!, Fine.Session, token);
            return;
        }
        var consistency = CheckConsistency(token);
        if (consistency.Status != "final-review") throw new CalibrationInputException(consistency.Status, "两盘一致性或第二张分辨率未通过检查。保留第二张独立结果，不能作为共享最终配置导出。");
        CalibrationExport.Write(output, Fine!.Fit!, Fine.Session, token, folder =>
        {
            RecordJson.WriteNew(Path.Combine(folder, "fine-target-plan.json"), Plan);
            RecordJson.WriteNew(Path.Combine(folder, "two-disc-consistency.json"), consistency);
            Coarse!.SaveNew(Path.Combine(folder, "coarse.dvdphoto")); Fine.SaveNew(Path.Combine(folder, "fine.dvdphoto"));
            File.WriteAllText(Path.Combine(folder, "two-disc-report.html"), $"<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>两盘标定记录</title><style>body{{font:16px system-ui;max-width:950px;margin:40px auto}}pre{{white-space:pre-wrap}}</style><h1>两盘软件流程完成</h1><p>证据模式：{EvidenceMode}；实刻验证 pending。{(EvidenceMode == "simulation" ? "本任务全部为软件模拟，不作为刻盘效果证明。" : "生成器身份及介质容量为显式用户声明，非独立认证。")}</p><p>第一张 G0 快照 {Coarse.Snapshot.Hash}<br>第二张 G1 快照 {Fine.Snapshot.Hash}<br>计划 {Plan.ContentSha256}</p><p>第二张独立使用 G1 拟合，保留生成轨距形状；照片各自保留 H 与整体转角。两盘模型形状一致性 RMS {consistency.CoarseShapeRmsMm.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} mm。此检查不证明介质跨盘稳定或参数唯一。</p><p><a href=\"report.html\">第二张最终参数与留出报告</a>；原始任务见 coarse.dvdphoto / fine.dvdphoto。计划、完整参数、确认和源图均可追溯；最终配置 physicalValidation 保持 pending。</p></html>");
        });
    }

    public void SaveNew(string path)
    {
        Validate(); byte[] Serialize(PhotoTask task)
        {
            using var stream = new MemoryStream(); task.WriteTo(stream);
            if (stream.Length > 350_000_000) throw new ArgumentException("单盘任务超过 350 MB。");
            return stream.ToArray();
        }
        byte[]? coarse = Coarse is null ? null : Serialize(Coarse);
        byte[]? fine = Fine is null ? null : Serialize(Fine);
        var manifest = new TwoDiscManifest(UsesManualParameters ? 2 : 1, "two-disc-task", Id, EvidenceMode, Plan,
            coarse is null ? null : Hash(coarse), fine is null ? null : Hash(fine), "pending");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, RecordJson.Options);
        if (json.Length > RecordJson.MaxBytes) throw new ArgumentException("两盘任务清单超过限制。");
        string target = Path.GetFullPath(path), parent = Path.GetDirectoryName(target)!;
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("两盘任务已存在，请使用新文件名保存修订。");
        Directory.CreateDirectory(parent); string temporary = Path.Combine(parent, ".dvdcal-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Entry(string name, byte[] bytes) { using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open(); entry.Write(bytes); }
                Entry("workflow.json", json);
                if (coarse is not null) Entry("coarse.dvdphoto", coarse);
                if (fine is not null) Entry("fine.dvdphoto", fine);
            }
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static TwoDiscTask Load(string path)
    {
        if (new FileInfo(path).Length > 710_000_000) throw new ArgumentException("两盘任务超过 710 MB。");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count is < 1 or > 3 || zip.Entries.Select(e => e.FullName).Distinct().Count() != zip.Entries.Count
            || zip.Entries.Any(e => e.FullName is not ("workflow.json" or "coarse.dvdphoto" or "fine.dvdphoto"))) throw new ArgumentException("两盘任务包含重复或未定义附件。");
        byte[] Read(string name, int max)
        {
            var entry = zip.GetEntry(name) ?? throw new ArgumentException("两盘任务缺少附件：" + name);
            if (entry.Length > max) throw new ArgumentException("两盘任务解压大小超过限制。");
            using var stream = entry.Open(); using var bytes = new MemoryStream(); var buffer = new byte[65536]; int n;
            while ((n = stream.Read(buffer)) > 0) { if (bytes.Length + n > max) throw new ArgumentException("两盘附件超过限制。"); bytes.Write(buffer, 0, n); }
            return bytes.ToArray();
        }
        var record = RecordJson.Parse<TwoDiscManifest>(Encoding.UTF8.GetString(Read("workflow.json", RecordJson.MaxBytes)));
        PhotoTask? ReadPhoto(string name, string? hash)
        {
            if (zip.GetEntry(name) is null && hash is null) return null;
            byte[] bytes = Read(name, 350_000_000);
            if (Hash(bytes) != hash) throw new ArgumentException("任务照片附件哈希无效。");
            using var stream = new MemoryStream(bytes);
            return PhotoTask.ReadFrom(stream);
        }
        if (record.SchemaVersion != (record.EvidenceMode == "manual-parameters" ? 2 : 1)
            || record.RecordType != "two-disc-task" || record.PhysicalValidation != "pending")
            throw new ArgumentException("标定任务清单版本或状态无效。");
        return new(record.Id, record.EvidenceMode, ReadPhoto("coarse.dvdphoto", record.CoarseTaskSha256), record.Plan,
            ReadPhoto("fine.dvdphoto", record.FineTaskSha256));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

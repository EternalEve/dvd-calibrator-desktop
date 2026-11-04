using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DvdCalibrator.Core;

namespace DvdCalibrator.Imaging.Windows;

public sealed partial class SnapshotBundle : IHalfDiscGeneration
{
    public Dictionary<string, byte[]> Files { get; }
    public string Id { get; }
    public string Hash { get; }
    public GeometryParameters Geometry { get; }
    public GeometryParameters Generated => Geometry;
    public string ContentSha256 => Hash;
    private PatternDefinition? pattern;
    public PatternDefinition Pattern => pattern ?? throw new ArgumentException("当前记录不是已核对源图的黑白半盘靶。");
    IReadOnlyDictionary<string, byte[]> IHalfDiscGeneration.ExportFiles() => Files;
    public void Validate()
    {
        var current = new SnapshotBundle(Files);
        if (current.Hash != Hash || current.Id != Id || current.Geometry != Geometry)
            throw new ArgumentException("导入记录已改变，请重新提交材料。");
    }
    public SnapshotBundle(Dictionary<string, byte[]> files)
    {
        Files = files;
        string json = Encoding.UTF8.GetString(files["generation.json"]).TrimStart('\uFEFF');
        using var document = RecordJson.ParseDocument(json);
        if (!document.RootElement.TryGetProperty("recordType", out var recordType)) throw new ArgumentException(RecordSelectionHelp);
        switch (recordType.GetString())
        {
            case "generation-snapshot":
                var s = RecordJson.Parse<GenerationSnapshot>(json); s.Validate();
                Id = s.Id; Hash = s.ContentSha256; Geometry = s.Generated;
                pattern = s.Pattern;
                if (files.Count != 1) throw new ArgumentException("合成快照包含未定义附件。");
                break;
            case "external-generation-snapshot":
                var e = RecordJson.Parse<ExternalGenerationSnapshot>(json); e.ValidateAttachments(name => files[name]);
                Id = e.Id; Hash = e.ContentSha256; Geometry = e.GenerationRecord.Generated;
                if (e.BinaryHalfDisc)
                {
                    HalfDiscSource.Validate(files[e.SourceImageFile]);
                    string id = e.Declarations.EvidenceSource == GenerationDeclarations.BuiltInHalfDisc ? GenerationDeclarations.BuiltInHalfDisc : "submitted-half-disc";
                    pattern = new(id, "half-disc", .25, "analytic-disc-xy-v1");
                }
                if (files.Count != 3) throw new ArgumentException("外部生成快照包含未定义附件。");
                break;
            case "upstream-calibration-generation":
                var calibration = RecordJson.Parse<CalibrationGeneration>(json); calibration.ValidateAttachments(name => files[name]);
                Id = calibration.Id; Hash = calibration.ContentSha256; Geometry = calibration.Generated;
                if (files.Count != 2) throw new ArgumentException("辅助标定记录包含未定义附件。");
                break;
            case "reference-target-simulation":
                var simulation = RecordJson.Parse<ReferenceTargetSimulation>(json); simulation.Validate();
                Id = simulation.Id; Hash = simulation.ContentSha256; Geometry = simulation.Generated;
                if (files.Count != 1) throw new ArgumentException("参考靶模拟快照包含未定义附件。");
                break;
            default: throw new ArgumentException(RecordSelectionHelp);
        }
    }
    public static SnapshotBundle Load(string path)
    {
        if (new FileInfo(path).Length > RecordJson.MaxBytes) throw new ArgumentException("生成快照过大。");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["generation.json"] = File.ReadAllBytes(path) };
        using var doc = RecordJson.ParseDocument(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("recordType", out var recordType)) throw new ArgumentException(RecordSelectionHelp);
        if (recordType.GetString() == "external-generation-snapshot")
        {
            var e = RecordJson.Read<ExternalGenerationSnapshot>(path);
            e.Validate(Path.GetDirectoryName(Path.GetFullPath(path))!);
            foreach (string name in new[] { e.SourceRecordFile, e.SourceImageFile })
            {
                string asset = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name);
                if (new FileInfo(asset).Length > PhotoCodec.MaxFileBytes) throw new ArgumentException("生成附件超过大小限制。");
                files[name] = File.ReadAllBytes(asset);
            }
        }
        else if (recordType.GetString() == "upstream-calibration-generation")
            files["source.json"] = ReadLimited(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "source.json"), RecordJson.MaxBytes);
        return new(files);
    }
    private const string RecordSelectionHelp = "请选择整理后的 generation.json。原始 .iso.json 需要先按“新手教程”整理；原项目的 .calibration.json 目前尚未支持，不能改名代替。";
}

public sealed class PhotoTask
{
    public SnapshotBundle Snapshot { get; }
    private PhotoSession session;
    public PhotoSession Session
    {
        get => session;
        set { if ((Fit is not null || Dense is not null) && RecordJson.Hash(value) != RecordJson.Hash(session)) { Fit = null; Dense = null; } session = value; }
    }
    public TraceFitReview? Fit { get; private set; }
    public DenseAnalysis? Dense { get; private set; }
    public ImportedPhoto Photo { get; }
    public PhotoTask(SnapshotBundle snapshot, ImportedPhoto photo, PhotoSession? session = null)
    {
        Snapshot = snapshot; Photo = photo;
        this.session = session ?? PhotoSession.Create(snapshot.Id, snapshot.Hash, photo.Metadata);
        Validate();
    }
    public void Validate()
    {
        var frozen = new SnapshotBundle(Snapshot.Files);
        if (frozen.Id != Snapshot.Id || frozen.Hash != Snapshot.Hash || frozen.Geometry != Snapshot.Geometry)
            throw new ArgumentException("生成附件与任务创建时的快照身份不一致。");
        Session.Validate();
        Fit?.Validate(Snapshot.Geometry, Snapshot.Hash, Session);
        if (Session.SnapshotId != Snapshot.Id || Session.SnapshotSha256 != Snapshot.Hash
            || Session.Photo != Photo.Metadata || PhotoCodec.Hash(Photo.OriginalBytes) != Session.Photo.OriginalSha256
            || PhotoCodec.Hash(Photo.OrientedPng) != Session.Photo.OrientedFileSha256
            || Photo.Image.PixelsSha256 != Session.Photo.OrientedPixelsSha256
            || Photo.Image.Width != Session.Photo.OrientedWidth || Photo.Image.Height != Session.Photo.OrientedHeight)
            throw new ArgumentException("照片任务输入身份不一致。");
    }

    public void AttachFit(TraceFitReview review)
    { review.Validate(Snapshot.Geometry, Snapshot.Hash, Session); Fit = review; }
    public void ClearFit() => Fit = null;
    public IHalfDiscGeneration AnalyticSnapshot()
    {
        if (Snapshot.Pattern.Kind != "half-disc") throw new ArgumentException("粗调识别需要已核对的黑白半盘源图。");
        return Snapshot;
    }
    public DenseAnalysis AnalyzeDense(DenseSettings? settings = null, CancellationToken token = default)
    {
        Validate(); var snapshot = AnalyticSnapshot();
        var result = DenseAnalyzer.Analyze(snapshot, Session, Photo.Image, settings, token);
        if (settings is null && !result.Candidates.Any(c => c.Supported)) return WfrAnalyzer.Analyze(snapshot, Session, Photo.Image, token);
        return result;
    }
    public void AttachDense(DenseAnalysis result)
    { result.Verify(AnalyticSnapshot(), Session, Photo.Image); Dense = result; }
    public TraceFitReview FitTrace(TraceFitSettings? settings = null, CancellationToken token = default)
    {
        Validate();
        return FitTrace(Session, settings, token);
    }

    public (PhotoSession Session, TraceFitReview Review) FitCrossAutomatically(TraceFitSettings? settings = null, CancellationToken token = default)
    {
        Validate();
        var traced = Session.WithTraces(AutoCrossTrace.Extract(Photo.Image, Session, Snapshot.Geometry, token));
        var review = FitTrace(traced, settings, token);
        review.Validate(Snapshot.Geometry, Snapshot.Hash, traced);
        return (traced, review);
    }

    private TraceFitReview FitTrace(PhotoSession observations, TraceFitSettings? settings, CancellationToken token)
    {
        var result = TraceFitter.Fit(Snapshot.Geometry, Snapshot.Hash, observations, settings, token);
        return new(result, result.Candidates.FirstOrDefault(c => c.EligibleForReview)?.Id ?? result.Candidates[0].Id, null);
    }

    public void SaveNew(string path)
    {
        string target = Path.GetFullPath(path);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("任务包已存在，请使用新文件名保存修订。");
        string parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, ".dvdphoto-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew)) WriteTo(stream);
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void WriteTo(Stream stream)
    {
        Validate();
        byte[] sessionJson = JsonSerializer.SerializeToUtf8Bytes(Session, RecordJson.Options);
        if (sessionJson.Length > RecordJson.MaxBytes) throw new ArgumentException("照片会话记录超过大小限制。");
        byte[]? fitJson = Fit is null ? null : JsonSerializer.SerializeToUtf8Bytes(Fit, RecordJson.Options);
        if (fitJson is not null && fitJson.Length > RecordJson.MaxBytes) throw new ArgumentException("拟合记录超过大小限制。");
        byte[]? denseJson = Dense is null ? null : JsonSerializer.SerializeToUtf8Bytes(Dense, RecordJson.Options);
        if (denseJson is not null)
        {
            Dense!.Verify(AnalyticSnapshot(), Session, Photo.Image);
            if (denseJson.Length > RecordJson.MaxBytes) throw new ArgumentException("密环记录超过大小限制。");
        }
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        void Entry(string name, byte[] bytes)
        { using var entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open(); entry.Write(bytes); }
        Entry("session.json", sessionJson);
        if (fitJson is not null) Entry("fit.json", fitJson);
        if (denseJson is not null) Entry("dense.json", denseJson);
        foreach (var (name, bytes) in Snapshot.Files) Entry("generation/" + name, bytes);
        Entry("photo/original", Photo.OriginalBytes); Entry("photo/oriented.png", Photo.OrientedPng);
    }

    public static PhotoTask Load(string path)
    {
        if (new FileInfo(path).Length > 350_000_000) throw new ArgumentException("任务包超过 350 MB。");
        using var stream = File.OpenRead(path); return ReadFrom(stream);
    }
    internal static PhotoTask ReadFrom(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 8 || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
            throw new ArgumentException("任务包包含重复或过多条目。");
        byte[] Read(string name, int max)
        {
            var entry = archive.GetEntry(name) ?? throw new ArgumentException($"缺少任务附件：{name}");
            if (entry.Length > max) throw new ArgumentException("解压条目超过大小限制。");
            using var stream = entry.Open(); using var result = new MemoryStream();
            byte[] buffer = new byte[65536]; int n;
            while ((n = stream.Read(buffer)) > 0)
            { if (result.Length + n > max) throw new ArgumentException("任务条目超过限制。"); result.Write(buffer, 0, n); }
            return result.ToArray();
        }
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith("generation/", StringComparison.Ordinal))
            {
                string name = entry.FullName[11..];
                if (name is not ("generation.json" or "source.json" or "assets/source-image.png" or "assets/source-image.bmp"
                    or "assets/source-image.jpg" or "assets/source-image.jpeg" or "assets/source-image.gif"))
                    throw new ArgumentException("不支持的生成附件路径。");
                files[name] = Read(entry.FullName, name.EndsWith(".json", StringComparison.Ordinal) ? RecordJson.MaxBytes : PhotoCodec.MaxFileBytes);
            }
            else if (entry.FullName is not ("session.json" or "photo/original" or "photo/oriented.png" or "fit.json" or "dense.json"))
                throw new ArgumentException("任务包含未定义条目。");
        }
        var session = RecordJson.Parse<PhotoSession>(Encoding.UTF8.GetString(Read("session.json", RecordJson.MaxBytes)));
        byte[] original = Read("photo/original", PhotoCodec.MaxFileBytes), png = Read("photo/oriented.png", 130_000_000);
        var photo = new ImportedPhoto(original, png, session.Photo, PhotoCodec.DecodePng(png));
        var task = new PhotoTask(new(files), photo, session);
        if (archive.GetEntry("fit.json") is not null)
            task.AttachFit(RecordJson.Parse<TraceFitReview>(Encoding.UTF8.GetString(Read("fit.json", RecordJson.MaxBytes))));
        if (archive.GetEntry("dense.json") is not null)
            task.AttachDense(RecordJson.Parse<DenseAnalysis>(Encoding.UTF8.GetString(Read("dense.json", RecordJson.MaxBytes))));
        return task;
    }
}

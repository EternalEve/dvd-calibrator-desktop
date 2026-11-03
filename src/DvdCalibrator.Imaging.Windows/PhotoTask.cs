using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DvdCalibrator.Core;

namespace DvdCalibrator.Imaging.Windows;

public sealed class SnapshotBundle
{
    public Dictionary<string, byte[]> Files { get; }
    public string Id { get; }
    public string Hash { get; }
    public GeometryParameters Geometry { get; }
    public SnapshotBundle(Dictionary<string, byte[]> files)
    {
        Files = files;
        string json = Encoding.UTF8.GetString(files["generation.json"]).TrimStart('\uFEFF');
        using var document = RecordJson.ParseDocument(json);
        switch (document.RootElement.GetProperty("recordType").GetString())
        {
            case "generation-snapshot":
                var s = RecordJson.Parse<GenerationSnapshot>(json); s.Validate();
                Id = s.Id; Hash = s.ContentSha256; Geometry = s.Generated;
                if (files.Count != 1) throw new ArgumentException("合成快照包含未定义附件。");
                break;
            case "external-generation-snapshot":
                var e = RecordJson.Parse<ExternalGenerationSnapshot>(json); e.ValidateAttachments(name => files[name]);
                Id = e.Id; Hash = e.ContentSha256; Geometry = e.GenerationRecord.Generated;
                if (files.Count != 3) throw new ArgumentException("外部生成快照包含未定义附件。");
                break;
            default: throw new ArgumentException("请选择第一阶段冻结的 generation.json。");
        }
    }
    public static SnapshotBundle Load(string path)
    {
        if (new FileInfo(path).Length > RecordJson.MaxBytes) throw new ArgumentException("生成快照过大。");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["generation.json"] = File.ReadAllBytes(path) };
        using var doc = RecordJson.ParseDocument(File.ReadAllText(path));
        if (doc.RootElement.GetProperty("recordType").GetString() == "external-generation-snapshot")
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
        return new(files);
    }
}

public sealed class PhotoTask
{
    public SnapshotBundle Snapshot { get; }
    public PhotoSession Session { get; set; }
    public ImportedPhoto Photo { get; }
    public PhotoTask(SnapshotBundle snapshot, ImportedPhoto photo, PhotoSession? session = null)
    {
        Snapshot = snapshot; Photo = photo;
        Session = session ?? PhotoSession.Create(snapshot.Id, snapshot.Hash, photo.Metadata);
        Validate();
    }
    public void Validate()
    {
        _ = new SnapshotBundle(Snapshot.Files);
        Session.Validate();
        if (Session.SnapshotId != Snapshot.Id || Session.SnapshotSha256 != Snapshot.Hash
            || Session.Photo != Photo.Metadata || PhotoCodec.Hash(Photo.OriginalBytes) != Session.Photo.OriginalSha256
            || PhotoCodec.Hash(Photo.OrientedPng) != Session.Photo.OrientedFileSha256
            || Photo.Image.PixelsSha256 != Session.Photo.OrientedPixelsSha256
            || Photo.Image.Width != Session.Photo.OrientedWidth || Photo.Image.Height != Session.Photo.OrientedHeight)
            throw new ArgumentException("照片任务输入身份不一致。");
    }

    public void SaveNew(string path)
    {
        Validate();
        byte[] sessionJson = JsonSerializer.SerializeToUtf8Bytes(Session, RecordJson.Options);
        if (sessionJson.Length > RecordJson.MaxBytes) throw new ArgumentException("照片会话记录超过大小限制。");
        string target = Path.GetFullPath(path);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("任务包已存在，请使用新文件名保存修订。");
        string parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, ".dvdphoto-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Entry(string name, byte[] bytes)
                { using var entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open(); entry.Write(bytes); }
                Entry("session.json", sessionJson);
                foreach (var (name, bytes) in Snapshot.Files) Entry("generation/" + name, bytes);
                Entry("photo/original", Photo.OriginalBytes); Entry("photo/oriented.png", Photo.OrientedPng);
            }
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static PhotoTask Load(string path)
    {
        if (new FileInfo(path).Length > 350_000_000) throw new ArgumentException("任务包超过 350 MB。");
        using var archive = ZipFile.OpenRead(path);
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
            else if (entry.FullName is not ("session.json" or "photo/original" or "photo/oriented.png"))
                throw new ArgumentException("任务包含未定义条目。");
        }
        var session = RecordJson.Parse<PhotoSession>(Encoding.UTF8.GetString(Read("session.json", RecordJson.MaxBytes)));
        byte[] original = Read("photo/original", PhotoCodec.MaxFileBytes), png = Read("photo/oriented.png", 130_000_000);
        var photo = new ImportedPhoto(original, png, session.Photo, PhotoCodec.DecodePng(png));
        return new(new(files), photo, session);
    }
}

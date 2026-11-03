namespace DvdCalibrator.Core;

/// <summary>A real generation record plus explicit declarations for evidence absent from its sidecar.</summary>
public sealed record GenerationDeclarations(DiscSpec Disc, long ConfirmedWritableSectors, string GeneratorVersion,
    string GeneratorCommit, string EvidenceSource)
{
    public void Validate(ImportedGenerationRecord record)
    {
        ArgumentNullException.ThrowIfNull(Disc); Disc.Validate();
        if (ConfirmedWritableSectors != record.Generated.TotalSectors || string.IsNullOrWhiteSpace(GeneratorVersion)
            || GeneratorCommit is not { Length: 40 } || !GeneratorCommit.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || EvidenceSource != "user-declared")
            throw new ArgumentException("需要确认真实容量、生成器版本、40 位小写提交和声明来源。");
    }
}

public sealed record ExternalGenerationSnapshot(int SchemaVersion, string RecordType, string State,
    string Id, ImportedGenerationRecord GenerationRecord, GenerationDeclarations Declarations,
    string SourceImageFile, string SourceImageSha256, string SourceRecordFile, string ContentSha256)
{
    public static ExternalGenerationSnapshot CreateInFolder(string recordPath, string imagePath,
        GenerationDeclarations declarations, string folder)
    {
        // Freeze from the copies actually included, so input-file changes during copying cannot
        // bind our hashes to different bytes than the published task contains.
        string extension = Path.GetExtension(imagePath).ToLowerInvariant();
        if (extension is not (".png" or ".bmp" or ".jpg" or ".jpeg" or ".gif"))
            throw new ArgumentException("需要 PNG、BMP、JPEG 或 GIF 源图附件。");
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        string imageFile = "assets/source-image" + extension;
        File.Copy(imagePath, Path.Combine(folder, imageFile));
        File.Copy(recordPath, Path.Combine(folder, "source.json"));
        var record = DiscImageRecord.Read(Path.Combine(folder, "source.json"));
        declarations.Validate(record);
        var snapshot = new ExternalGenerationSnapshot(1, "external-generation-snapshot", "frozen",
            Guid.NewGuid().ToString("N"), record, declarations, imageFile,
            RecordJson.FileHash(Path.Combine(folder, imageFile)), "source.json", "");
        snapshot = snapshot with { ContentSha256 = RecordJson.Hash(snapshot) };
        snapshot.Validate(folder);
        return snapshot;
    }

    public void Validate(string folder)
    {
        if (SchemaVersion != 1 || RecordType != "external-generation-snapshot" || State != "frozen"
            || !Guid.TryParseExact(Id, "N", out _) || !RecordJson.IsHash(ContentSha256)
            || ContentSha256 != RecordJson.Hash(this with { ContentSha256 = "" }))
            throw new ArgumentException("外部生成快照版本、类型或冻结哈希无效。");
        ArgumentNullException.ThrowIfNull(GenerationRecord);
        ArgumentNullException.ThrowIfNull(Declarations); Declarations.Validate(GenerationRecord);
        if (SourceRecordFile != "source.json" || SourceImageFile is not ("assets/source-image.png"
            or "assets/source-image.bmp" or "assets/source-image.jpg" or "assets/source-image.jpeg" or "assets/source-image.gif")
            || !RecordJson.IsHash(SourceImageSha256)) throw new ArgumentException("源附件路径或哈希无效。");
        var parsed = DiscImageRecord.Read(Path.Combine(folder, SourceRecordFile));
        if (RecordJson.Hash(parsed) != RecordJson.Hash(GenerationRecord)
            || RecordJson.FileHash(Path.Combine(folder, SourceImageFile)) != SourceImageSha256)
            throw new ArgumentException("快照附件已改变，或派生生成模型与真实记录不一致。");
    }
}

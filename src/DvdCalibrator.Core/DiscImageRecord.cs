using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace DvdCalibrator.Core;

public sealed record ImportedGenerationRecord(string RecordType, string AdapterVersion, string SourceSha256,
    GeometryParameters Generated, double StartAngleDegrees, int LuminanceThreshold, int AlphaThreshold,
    JsonElement OriginalRecord, string QualityStatus, ImmutableArray<string> MissingEvidence);

/// <summary>Read only the pinned engine's fast dispersion full-disc sidecar; never invent absent fields.</summary>
public static class DiscImageRecord
{
    public const string UpstreamCommit = "374c0630326ea61be9adca8d7de3c96f083c00df";
    private static readonly HashSet<string> MappingFields = new(StringComparer.Ordinal)
    {
        "totalSectors", "innerRadiusMm", "outerRadiusMm", "channelBitLengthNm", "startAngleDegrees",
        "clockwise", "luminanceThreshold", "alphaThreshold", "sampleEveryChannelBits",
        "pitchLinear", "pitchQuadratic", "pitchCubic", "tone"
    };

    public static ImportedGenerationRecord Read(string path)
    {
        if (new FileInfo(path).Length > RecordJson.MaxBytes) throw new ArgumentException("记录超过 4 MB 限制。");
        byte[] sourceBytes = File.ReadAllBytes(path);
        return ReadBytes(sourceBytes);
    }

    public static ImportedGenerationRecord ReadBytes(byte[] sourceBytes, bool allowBinaryTone = false)
    {
        if (sourceBytes.Length > RecordJson.MaxBytes) throw new ArgumentException("记录超过 4 MB 限制。");
        using var document = RecordJson.ParseDocument(Encoding.UTF8.GetString(sourceBytes).TrimStart('\uFEFF'));
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "dvd-nrzi-disc-fill-v2"
            || root.GetProperty("algorithm").GetString() != "dispersion" || !root.GetProperty("fastOutput").GetBoolean())
            throw new ArgumentException("仅支持固定上游版本的 dvd-nrzi-disc-fill-v2 快速 dispersion 旁车记录。");
        var mapping = root.GetProperty("imageMapping");
        if (mapping.EnumerateObject().Any(p => !MappingFields.Contains(p.Name)))
            throw new ArgumentException("生成映射含未知字段，需要新的适配器版本。");
        // Require the field even when null: absence is not equivalent to an explicitly recorded legacy tone.
        var tone = mapping.GetProperty("tone");
        if (tone.ValueKind != JsonValueKind.Null && (!allowBinaryTone || !BinaryTonePreservesEndpoints(tone)))
            throw new ArgumentException("首版尚不支持 tone 变换；不能将其静默当作无抖动输入。");
        var parameters = new GeometryParameters(mapping.GetProperty("innerRadiusMm").GetDouble(),
            mapping.GetProperty("outerRadiusMm").GetDouble(), mapping.GetProperty("totalSectors").GetInt64(),
            mapping.GetProperty("channelBitLengthNm").GetDouble(), mapping.GetProperty("pitchLinear").GetDouble(),
            mapping.GetProperty("pitchQuadratic").GetDouble(), mapping.GetProperty("pitchCubic").GetDouble(),
            mapping.GetProperty("clockwise").GetBoolean() ? SpiralDirection.Cw : SpiralDirection.Ccw,
            GeometryParameters.Convention, root.GetProperty("startLba").GetInt64());
        parameters.Validate();
        // Construct as well as validate to catch numerical resolution failures.
        _ = new TrackGeometry(parameters);
        var iso = root.GetProperty("iso");
        if (root.GetProperty("totalSectors").GetInt64() != parameters.TotalSectors
            || root.GetProperty("fillSectors").GetInt64() != parameters.TotalSectors
            || parameters.TotalSectors % 16 != 0 || root.GetProperty("blockCount").GetInt64() != parameters.TotalSectors / 16
            || root.GetProperty("filesAtOuterRing").GetBoolean()
            || iso.GetProperty("mode").GetString() != "raw-sector-image"
            || iso.GetProperty("hybrid").ValueKind != JsonValueKind.Null
            || iso.GetProperty("logicalSectorBytes").GetInt32() != 2048
            || iso.GetProperty("imageSectors").GetInt64() != parameters.TotalSectors)
            throw new ArgumentException("首版仅支持零起点、完整写入、无随盘文件的数据映射。");
        int luminance = mapping.GetProperty("luminanceThreshold").GetInt32();
        int alpha = mapping.GetProperty("alphaThreshold").GetInt32();
        double startAngle = mapping.GetProperty("startAngleDegrees").GetDouble();
        if (luminance is < 0 or > 255 || alpha is < 0 or > 255 || !double.IsFinite(startAngle)
            || mapping.GetProperty("sampleEveryChannelBits").GetInt32() <= 0)
            throw new ArgumentException("阈值、起始角或采样参数无效。");
        return new("imported-generation-record", "discimage-fast-fill-v2-v1", Convert.ToHexStringLower(SHA256.HashData(sourceBytes)),
            parameters, startAngle, luminance, alpha, root.Clone(), "record-incomplete",
            ["media-type-and-real-capacity-confirmation", "source-image-bytes-and-sha256",
             "generator-executable-version-and-commit", "image-preprocessing-and-sampling-confirmation"]);
    }

    // The GUI's default dither/gamma preserve opaque 0/255 endpoints when brightness is zero.
    // This exception is only for a separately verified binary half-disc source.
    private static bool BinaryTonePreservesEndpoints(JsonElement tone)
    {
        var profile = tone.Deserialize<DiscImageStudio.Core.ToneProfile>(RecordJson.Options);
        profile.Validate();
        return profile.Levels == 2 && profile.Brightness == 0;
    }
}

using System.Text;
using System.Text.Json;

namespace DvdCalibrator.Core;

/// <summary>Manual hand-off from the pinned upstream auxiliary calibration workspace.
/// Schema 6 fixes the target to the bundled reference PNG; no user-supplied target is inferred.</summary>
public sealed record CalibrationGeneration(int SchemaVersion, string RecordType, string Id, DiscSpec Disc,
    GeometryParameters Generated, string SourceSha256, string ContentSha256)
{
    public static CalibrationGeneration Import(byte[] bytes, DiscSpec disc)
    {
        var geometry = ReadGeometry(bytes); disc.Validate();
        var result = new CalibrationGeneration(1, "upstream-calibration-generation", Guid.NewGuid().ToString("N"),
            disc, geometry, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), "");
        return result with { ContentSha256 = RecordJson.Hash(result) };
    }

    public void ValidateAttachments(Func<string, byte[]> read)
    {
        Disc.Validate(); Generated.Validate(); byte[] source = read("source.json");
        if (SchemaVersion != 1 || RecordType != "upstream-calibration-generation" || !Guid.TryParseExact(Id, "N", out _)
            || ContentSha256 != RecordJson.Hash(this with { ContentSha256 = "" })
            || SourceSha256 != Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(source))
            || Generated != ReadGeometry(source))
            throw new ArgumentException("辅助标定记录与导入参数不一致，请重新提交本次 .calibration.json。");
    }

    public static GeometryParameters ReadGeometry(byte[] bytes)
    {
        using var doc = RecordJson.ParseDocument(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        var target = doc.RootElement.GetProperty("target");
        if (target.GetProperty("schemaVersion").GetInt32() != 6
            || !Guid.TryParseExact(target.GetProperty("id").GetString(), "N", out _)
            || target.GetProperty("cells").GetArrayLength() != 0)
            throw new ArgumentException("仅支持原项目 schema 6 的十字辅助标定记录。");
        RequireFields(target, ["schemaVersion", "id", "parameters", "options", "cells"]);
        var p = target.GetProperty("parameters");
        RequireFields(p, ["kind", "innerRadiusMm", "outerRadiusMm", "sectors", "linearDensity", "pitchLinear", "pitchQuadratic", "pitchCubic",
            "bluRayInvertMapping", "drawingStartSector", "bluRayIsoTimestamp", "drawingEndSectorExclusive"]);
        if (p.GetProperty("kind").GetString() != "Dvd"
            || p.GetProperty("bluRayInvertMapping").GetBoolean() || p.GetProperty("drawingStartSector").GetInt64() != 0
            || p.GetProperty("drawingEndSectorExclusive").GetInt64() != 0 || p.GetProperty("bluRayIsoTimestamp").ValueKind != JsonValueKind.Null)
            throw new ArgumentException("请选择 12 cm 单层 DVD 的完整辅助标定记录。");
        var options = target.GetProperty("options");
        RequireFields(options, ["largeCellMm", "mediumCellMm", "smallCellMm", "sectorGapMm"]);
        double a = options.GetProperty("largeCellMm").GetDouble(), b = options.GetProperty("mediumCellMm").GetDouble(),
            c = options.GetProperty("smallCellMm").GetDouble(), gap = options.GetProperty("sectorGapMm").GetDouble();
        if (!double.IsFinite(a + b + c + gap) || c < .25 || b <= c || a <= b || a > 100 || gap <= 0 || gap > 10)
            throw new ArgumentException("辅助标定图案选项无效。");
        var geometry = new GeometryParameters(p.GetProperty("innerRadiusMm").GetDouble(), p.GetProperty("outerRadiusMm").GetDouble(),
            p.GetProperty("sectors").GetInt64(), p.GetProperty("linearDensity").GetDouble(), p.GetProperty("pitchLinear").GetDouble(),
            p.GetProperty("pitchQuadratic").GetDouble(), p.GetProperty("pitchCubic").GetDouble(), SpiralDirection.Cw, GeometryParameters.Convention, 0);
        geometry.Validate(); _ = new TrackGeometry(geometry);
        if (geometry.TotalSectors % 16 != 0) throw new ArgumentException("DVD 容量必须为 16 扇区对齐。");
        return geometry;
    }

    private static void RequireFields(JsonElement value, string[] fields)
    {
        if (value.EnumerateObject().Any(p => !fields.Contains(p.Name)) || fields.Any(f => !value.TryGetProperty(f, out _)))
            throw new ArgumentException("辅助标定记录字段不匹配当前适配版本。");
    }
}

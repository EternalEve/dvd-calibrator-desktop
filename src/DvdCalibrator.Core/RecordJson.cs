using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DvdCalibrator.Core;

public static class RecordJson
{
    public const int MaxBytes = 4_000_000;
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<SpiralDirection>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new ArgumentException("记录超过 4 MB 限制。");
        return Parse<T>(File.ReadAllText(path));
    }

    public static T Parse<T>(string json)
    {
        using var document = ParseDocument(json);
        return document.RootElement.Deserialize<T>(Options) ?? throw new ArgumentException("记录不能为空。");
    }

    public static JsonDocument ParseDocument(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new ArgumentException("记录超过 4 MB 限制。");
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
        try { CheckDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    public static void WriteNew<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, value, Options);
    }

    // Canonicalization v1: ordinal property order, array order retained, typed numeric serialization.
    // Hashes identify this project's records, not arbitrary external JSON canonicalization standards.
    public static string Hash<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Canonical(writer, JsonSerializer.SerializeToElement(value, Options));
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static bool IsHash(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException($"重复 JSON 字段：{property.Name}");
                CheckDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }

    private static void Canonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Canonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Canonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

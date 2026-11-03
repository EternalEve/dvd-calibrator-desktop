using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using DvdCalibrator.Core;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var schemaTypes = new (string Name, Type Type)[] { ("geometry", typeof(GeometryParameters)),
    ("generation-snapshot", typeof(GenerationSnapshot)), ("observations", typeof(Observations)),
    ("external-generation-snapshot", typeof(ExternalGenerationSnapshot)),
    ("photo-session-v2", typeof(PhotoSession)),
    ("fit-result", typeof(FitResult)), ("forward-prediction", typeof(PredictionResult)) };
static JsonNode ExportSchema(string name, Type type)
{
    var node = RecordJson.Options.GetJsonSchemaAsNode(type);
    node["type"] = "object";
    node["$schema"] = "https://json-schema.org/draft/2020-12/schema";
    node["$id"] = name == "photo-session-v2" ? "urn:dvd-calibrator:photo-session:v2" : $"urn:dvd-calibrator:{name}:v1";
    return node;
}
if (args is ["--write-schemas"])
{
    foreach (var (name, type) in schemaTypes)
    {
        var node = ExportSchema(name, type);
        File.WriteAllText(Path.Combine(root, "schemas", name + ".schema.json"), node.ToJsonString(RecordJson.Options) + "\n");
    }
    Console.WriteLine("Exported serializer structure schemas. See docs/data-contracts.md for semantic validation.");
    return 0;
}

var nominal = new GeometryParameters(24, 58, 2_295_104, 133.3, 0, 0, 0,
    SpiralDirection.Cw, GeometryParameters.Convention, 0);
var snapshot = GenerationSnapshot.CreateSynthetic("test-g0", new("DVD+R", 120, 15, 1, null), nominal,
    new("half-v1", "half-disc", .3, "analytic-disc-xy-v1"));
int count = 0, failures = 0;
void Test(string name, Action test)
{
    count++;
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL {name}: {e}"); }
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Near(double actual, double expected, double tolerance, string label)
    => Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
        $"{label}: {actual:G17} vs {expected:G17}, tolerance {tolerance:G3}");
static void Reject(Action action)
{
    try { action(); }
    catch (Exception e) when (e is ArgumentException or JsonException or IOException or OperationCanceledException or KeyNotFoundException) { return; }
    throw new Exception("Invalid input was accepted.");
}

Test("vendored source byte hash matches fixed upstream lock", () =>
{
    using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "upstream.lock.json")));
    foreach (var file in data.RootElement.GetProperty("files").EnumerateArray())
        Check(RecordJson.FileHash(Path.Combine(root, file.GetProperty("local").GetString()!)) == file.GetProperty("sha256").GetString(), "vendor hash mismatch");
});
Test("published schemas match the actual serializer contracts", () =>
{
    foreach (var (name, type) in schemaTypes)
        Check(JsonNode.DeepEquals(ExportSchema(name, type), JsonNode.Parse(File.ReadAllText(
            Path.Combine(root, "schemas", name + ".schema.json")))), $"Schema drift: {name}");
});
Test("units and nominal constant pitch against physical scale", () =>
{
    Near(nominal.TrackLengthMm, 2_295_104d * 38688 * 133.3 / 1_000_000, 1e-8, "length mm");
    var g = new TrackGeometry(nominal);
    double areaPitch = Math.PI * (58 * 58 - 24 * 24) / nominal.TrackLengthMm;
    Near(g.PitchBaseMm, areaPitch, 1e-14, "independent thin-track scale check");
    Near(g.PitchBaseMm * 1000, .74, .001, "nominal pitch micrometres");
    Check(g.TotalAngleRadians < 0, "CW must be negative in y-up");
});

double worstAngle = 0, worstRadius = 0;
foreach (var p in new[] { nominal, nominal with { PitchLinear = .02, PitchQuadratic = -.03, PitchCubic = .015 },
    nominal with { PitchLinear = -.15, PitchQuadratic = .2, PitchCubic = -.05, ChannelBitLengthNm = 133.33,
        TotalSectors = 2_298_496, Direction = SpiralDirection.Ccw },
    nominal with { PitchLinear = -1.1, PitchQuadratic = .7, PitchCubic = .1 } })
{
    Test($"independent Simpson convergence / full geometry ({p.PitchLinear}, {p.Direction})", () =>
    {
        var g = new TrackGeometry(p);
        var coarse = new ReferenceGeometry(p, 8192);
        var fine = new ReferenceGeometry(p, 16384);
        Near(fine.Angle(p.OuterRadiusMm), coarse.Angle(p.OuterRadiusMm), 2e-8, "oracle refinement");
        Near(g.PitchBaseMm, fine.Pitch, 2e-17, "p0");
        foreach (double fraction in new[] { 0d, .073, .25, .499, .731, 1d })
        {
            double radius = p.InnerRadiusMm + fraction * (p.OuterRadiusMm - p.InnerRadiusMm);
            double angleError = Math.Abs(g.AngleAtRadius(radius) - fine.Angle(radius));
            worstAngle = Math.Max(worstAngle, angleError);
            Near(g.AngleAtRadius(radius), fine.Angle(radius), 2e-8, "theta(r)");
            Near(g.ProgressAtRadius(radius), fine.Arc(radius) / p.TrackLengthMm, 2e-13, "u(r)");
            Near(g.RadiusAtProgress(g.ProgressAtRadius(radius)), radius, 2e-11, "r-u-r");
        }
        foreach (double progress in new[] { .113, .527, .891 })
        {
            double expected = fine.Radius(progress);
            worstRadius = Math.Max(worstRadius, Math.Abs(expected - g.RadiusAtProgress(progress)));
            Near(g.RadiusAtProgress(progress), expected, 2e-11, "inverse u");
        }
        double previous = -1;
        var identity = new ForwardModel(p, p);
        for (int i = 0; i <= 256; i++)
        {
            double u = i / 256d;
            double r = g.RadiusAtProgress(u);
            Check(r > previous, "inverse table is not strictly monotone");
            Near(g.ProgressAtRadius(r), u, 2e-13, "dense u-r-u");
            Near(identity.AtRadius(r).PhaseRadians, 0, 2e-8, "identity phase including cubic model");
            previous = r;
        }
    });
}
Test("identity forward map and unwrapped direction / rotation", () =>
{
    var f = new ForwardModel(nominal, nominal);
    for (int i = 0; i <= 128; i++)
    {
        double radius = 24 + 34 * i / 128d;
        var p = f.AtRadius(radius, .7, .2);
        Near(p.SourceRadiusMm, radius, 2e-11, "identity r");
        Near(p.PhaseRadians, 0, 2e-8, "identity phase");
        Near(p.ObservedAngleRadians, .9, 2e-8, "rotation");
    }
    var actual = nominal with { OuterRadiusMm = 58.01 };
    var cw = new ForwardModel(nominal, actual).AtRadius(55);
    var ccw = new ForwardModel(nominal with { Direction = SpiralDirection.Ccw }, actual with { Direction = SpiralDirection.Ccw }).AtRadius(55);
    Near(cw.PhaseRadians, -ccw.PhaseRadians, 1e-10, "sign adapter");
    Check(Math.Abs(cw.PhaseRadians) > 2 * Math.PI, "test should retain multiple turns");
});
Test("nonzero G and A prediction against independent data-position oracle", () =>
{
    var g = nominal with { PitchLinear = .02, PitchQuadratic = -.03, PitchCubic = .015 };
    var a = g with { InnerRadiusMm = 24.004, OuterRadiusMm = 58.009, PitchLinear = .021, ChannelBitLengthNm = 133.33 };
    var rg = new ReferenceGeometry(g); var ra = new ReferenceGeometry(a);
    var f = new ForwardModel(g, a);
    foreach (double radius in new[] { 25d, 36.3, 49.1, 57d })
    {
        double sourceRadius = rg.Radius(ra.Arc(radius) / a.TrackLengthMm);
        var p = f.AtRadius(radius);
        Near(p.SourceRadiusMm, sourceRadius, 2e-11, "mapped radius");
        Near(p.PhaseRadians, ra.Angle(radius) - rg.Angle(sourceRadius), 4e-8, "phase difference");
    }
});
Test("invalid pitch including interior negative minimum is rejected", () =>
{
    // q(0)=1 and q(1)=1, but q(.5)=-.25.
    Reject(() => new TrackGeometry(nominal with { PitchLinear = -5, PitchQuadratic = 5 }));
    Reject(() => new TrackGeometry(nominal with { PitchLinear = -4, PitchQuadratic = 4 }));
    Reject(() => new TrackGeometry(nominal with { PitchCubic = double.NaN }));
    Reject(() => new TrackGeometry(nominal with { OuterRadiusMm = 61 }));
    Reject(() => new TrackGeometry(nominal with { ChannelBitLengthNm = double.PositiveInfinity }));
    Reject(() => new TrackGeometry(nominal with { Direction = (SpiralDirection)5 }));
    Reject(() => new TrackGeometry(nominal with { StartLba = 16 }));
});
Test("bounds, nonfinite and incompatible data mappings do not clamp", () =>
{
    var g = new TrackGeometry(nominal);
    Reject(() => g.ProgressAtRadius(23.999)); Reject(() => g.AngleAtRadius(double.NaN));
    Reject(() => g.RadiusAtProgress(1.001)); Reject(() => g.RadiusAtProgress(double.NegativeInfinity));
    Reject(() => new ForwardModel(nominal, nominal with { TotalSectors = 1234 }));
    Reject(() => new ForwardModel(nominal, nominal).AtRadius(40, rotationRadians: double.NaN));
});
Test("strict required JSON, unknown / duplicate fields and enum rules", () =>
{
    string json = JsonSerializer.Serialize(nominal, RecordJson.Options);
    Check(RecordJson.Parse<GeometryParameters>(json) == nominal, "round trip");
    var missing = JsonNode.Parse(json)!.AsObject(); missing.Remove("pitchCubic");
    Reject(() => RecordJson.Parse<GeometryParameters>(missing.ToJsonString()));
    Reject(() => RecordJson.Parse<GeometryParameters>(json.Replace("\"cw\"", "0", StringComparison.Ordinal)));
    Reject(() => RecordJson.Parse<GeometryParameters>(json.Insert(1, "\"unexpected\":1,")));
    Reject(() => RecordJson.Parse<GeometryParameters>(json.Insert(1, "\"innerRadiusMm\":12,")));
    Reject(() => RecordJson.Parse<GenerationSnapshot>("null"));
});
Test("snapshot immutability, serialization and semantic hash ordering", () =>
{
    snapshot.Validate();
    string json = JsonSerializer.Serialize(snapshot, RecordJson.Options);
    Check(RecordJson.Parse<GenerationSnapshot>(json) == snapshot, "snapshot roundtrip");
    Reject(() => (snapshot with { Generated = nominal with { InnerRadiusMm = 24.1 } }).Validate());
    Reject(() => (snapshot with { SchemaVersion = 2 }).Validate());
    Reject(() => (snapshot with { Generator = new("DiscImageStudio", "unknown", "external") }).Validate());
    var obj = JsonNode.Parse(json)!.AsObject();
    var reversed = new JsonObject(obj.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
    RecordJson.Parse<GenerationSnapshot>(reversed.ToJsonString()).Validate();
});
Test("observations and results are bound to exact frozen inputs", () =>
{
    var o = new Observations(1, "observations", snapshot.Id, snapshot.ContentSha256,
        new string('a', 64), "assets/photo.png", 2000, 1500, 6, [], [],
        [new("line-a", [new(300, 300), new(700, 800)])]);
    o.Validate(snapshot);
    var roundtrip = RecordJson.Parse<Observations>(JsonSerializer.Serialize(o, RecordJson.Options));
    roundtrip.Validate(snapshot);
    Check(RecordJson.Hash(o) == RecordJson.Hash(roundtrip), "observation roundtrip hash");
    var result = new FitResult(1, "fit-result", snapshot.ContentSha256, RecordJson.Hash(o), "contract-test-v1",
        "candidate-ambiguous", "pending", [new("a", nominal, .1, .2)]);
    result.Validate(snapshot, o);
    Reject(() => (o with { SnapshotSha256 = new string('b', 64) }).Validate(snapshot));
    Reject(() => (o with { PhotoFile = "../escape.png" }).Validate(snapshot));
    Reject(() => (o with { Traces = [new("bad", [new(2000, 0)])] }).Validate(snapshot));
    Reject(() => result.Validate(snapshot, o with { Traces = [] }));
    Reject(() => (result with { PhysicalValidation = "passed" }).Validate(snapshot, o));
});
Test("undersampled predictions disclose alias risk and preserve raw phase", () =>
{
    var p = Prediction.Calculate(snapshot, nominal with { Direction = SpiralDirection.Ccw }, 0, 3);
    Check(p.Warnings.Contains("curve-undersampled-increase-samples"), "missing sampling warning");
    Check(p.Curve[^1].PhaseRadians > 100_000, "phase was wrapped");
    Check(!Prediction.Svg(snapshot, p).Contains("polyline", StringComparison.Ordinal), "unsafe connected plot");
    Reject(() => Prediction.Calculate(snapshot, nominal, 0, 1));
});
Test("identity pattern raster preserves upper/lower half and disc mask", () =>
{
    string folder = Path.Combine(root, "artifacts"); Directory.CreateDirectory(folder);
    string path = Path.Combine(folder, "raster-" + Guid.NewGuid().ToString("N") + ".pgm");
    PatternRaster.WritePgm(path, snapshot, nominal, 0, 32);
    byte[] bytes = File.ReadAllBytes(path);
    string prefix = System.Text.Encoding.ASCII.GetString(bytes.AsSpan(0, Math.Min(160, bytes.Length)));
    int offset = prefix.IndexOf("255\n", StringComparison.Ordinal) + 4;
    Check(offset >= 4 && bytes.Length == offset + 32 * 32, "invalid P5 payload");
    Check(bytes[offset] == 200, "outside disc mask");
    Check(bytes[offset + 4 * 32 + 16] == 255, "y-up upper half must be white");
    Check(bytes[offset + 27 * 32 + 16] == 0, "y-down image adapter must keep lower half black");
    var cross = snapshot.Pattern with { Kind = "radial-cross" };
    Check(!cross.IsWhite(40, 0) && cross.IsWhite(40, Math.PI / 4), "cross pattern semantics");
});
Test("atomic outputs refuse overwrite, clean failure and cancellation", () =>
{
    string folder = Path.Combine(root, "artifacts", "test-" + Guid.NewGuid().ToString("N"));
    OutputFolder.Publish(folder, dir => RecordJson.WriteNew(Path.Combine(dir, "snapshot.json"), snapshot));
    string hash = RecordJson.FileHash(Path.Combine(folder, "snapshot.json"));
    Reject(() => OutputFolder.Publish(folder, _ => throw new Exception("must not execute")));
    Check(RecordJson.FileHash(Path.Combine(folder, "snapshot.json")) == hash, "overwritten file");
    Reject(() => OutputFolder.Publish(folder + "-failed", _ => throw new ArgumentException("expected failure")));
    Check(!Directory.Exists(folder + "-failed"), "partial output published");
    using var cts = new CancellationTokenSource(); cts.Cancel();
    Reject(() => OutputFolder.Publish(folder + "-cancelled", _ => { }, cts.Token));
    Check(!Directory.Exists(folder + "-cancelled"), "cancelled output published");
});
Test("actual upstream fast-fill fixture and incomplete-evidence diagnostics", () =>
{
    string path = Path.Combine(root, "fixtures", "upstream-fast-fill.iso.json");
    var record = DiscImageRecord.Read(path);
    Check(record.Generated.Direction == SpiralDirection.Cw && record.Generated.TotalSectors == 16, "wrong imported geometry");
    Near(record.Generated.PitchLinear, .02, 0, "nonzero imported coefficient");
    Check(record.QualityStatus == "record-incomplete" && record.MissingEvidence.Length >= 4, "unjustified complete status");
    Check(record.OriginalRecord.GetProperty("format").GetString() == "dvd-nrzi-disc-fill-v2", "raw record lost");
    foreach (string field in new[] { "pitchLinear", "pitchQuadratic", "pitchCubic", "channelBitLengthNm", "clockwise", "tone" })
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["imageMapping"]!.AsObject().Remove(field);
        string temp = Path.Combine(root, "artifacts", "bad-record-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(temp, node.ToJsonString());
        try { DiscImageRecord.Read(temp); throw new Exception($"Missing field {field} accepted"); }
        catch (KeyNotFoundException) { }
    }
    void Bad(Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); edit(node);
        string temp = Path.Combine(root, "artifacts", "bad-record-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(temp, node.ToJsonString());
        Reject(() => DiscImageRecord.Read(temp));
    }
    Bad(n => n["startLba"] = 16);
    Bad(n => n["fillSectors"] = 8);
    Bad(n => n["format"] = "future-format");
    Bad(n => n["iso"]!["hybrid"] = new JsonObject());
    Bad(n => n["imageMapping"]!["unknownMapping"] = true);
    Bad(n => n["imageMapping"]!["tone"] = new JsonObject());
    Bad(n => n["imageMapping"]!["pitchLinear"] = -5);
});
Test("external snapshot freezes copied source bytes and survives relocation", () =>
{
    var declaration = new GenerationDeclarations(new("DVD-R", 120, 15, 1, null), 16, "1.3.6",
        DiscImageRecord.UpstreamCommit, "user-declared");
    string folder = Path.Combine(root, "artifacts", "external-" + Guid.NewGuid().ToString("N"));
    OutputFolder.Publish(folder, dir =>
    {
        var external = ExternalGenerationSnapshot.CreateInFolder(Path.Combine(root, "fixtures/upstream-fast-fill.iso.json"),
            Path.Combine(root, "fixtures/upstream-input.bmp"), declaration, dir);
        RecordJson.WriteNew(Path.Combine(dir, "generation.json"), external);
    });
    string moved = folder + "-moved";
    Directory.Move(folder, moved);
    var loaded = RecordJson.Read<ExternalGenerationSnapshot>(Path.Combine(moved, "generation.json"));
    loaded.Validate(moved);
    Reject(() => (loaded with { Declarations = declaration with { ConfirmedWritableSectors = 32 } }).Validate(moved));
    File.AppendAllText(Path.Combine(moved, loaded.SourceImageFile), "changed");
    Reject(() => loaded.Validate(moved));
});

PhotoChecks.Register(Test);
Console.WriteLine($"Independent oracle max angle error: {worstAngle:G6} rad; max inverse radius error: {worstRadius:G6} mm.");
Console.WriteLine($"{count - failures}/{count} checks passed.");
return failures == 0 ? 0 : 1;

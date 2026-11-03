using System.Globalization;
using System.Text.Json;
using DvdCalibrator.Core;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args.Length == 0 || args is ["--help"] or ["help"])
    {
        Console.WriteLine("""
            DvdCalibrator 0.5.0 — geometry, reference target contracts and trace fitting
            demo --out NEW_FOLDER
            inspect --record SNAPSHOT_OR_DISCIMAGE_SIDECAR.json
            import-discimage --record SIDECAR.json --out NEW_FOLDER
            freeze-discimage --record SIDECAR.json --image ORIGINAL_IMAGE --media DVD-R|DVD+R
                             --confirmed-sectors N --generator-version VERSION
                             --generator-commit COMMIT --out NEW_FOLDER
            predict --generated SNAPSHOT.json --actual MODEL.json --out NEW_FOLDER
                    [--samples 2049] [--rotation-deg 0]
            rectify --session PHOTO_SESSION_V2.json --out NEW_FOLDER
            fit-trace --generated SNAPSHOT.json --session PHOTO_SESSION_V2.json --out NEW_FOLDER [--shape-order 0]
            export-profile --review FIT_REVIEW.json --session PHOTO_SESSION_V2.json --out NEW_FOLDER
                           --confirm-curve true [--candidate candidate-1]
            verify-fine-generation --plan CONFIRMED_FINE_PLAN.json --generated EXTERNAL_SNAPSHOT.json

            Profiles contain equivalent compensation; physical validation remains pending.
            Existing output directories are rejected. Exit codes: 0 OK, 2 invalid input,
            3 I/O failure, 130 cancelled. Ctrl+C cancels before publishing the output folder.
            """);
        return 0;
    }

    string command = args[0];
    string[] allowed = command switch
    {
        "demo" => ["out"],
        "inspect" => ["record"],
        "import-discimage" => ["record", "out"],
        "freeze-discimage" => ["record", "image", "media", "confirmed-sectors", "generator-version", "generator-commit", "out"],
        "predict" => ["generated", "actual", "out", "samples", "rotation-deg"],
        "rectify" => ["session", "out"],
        "fit-trace" => ["generated", "session", "out", "shape-order"],
        "export-profile" => ["review", "session", "out", "candidate", "confirm-curve"],
        "verify-fine-generation" => ["plan", "generated"],
        _ => throw new ArgumentException($"未知命令：{command}，使用 --help 查看帮助。")
    };
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)
            || !allowed.Contains(args[i][2..]) || !options.TryAdd(args[i][2..], args[i + 1]))
            throw new ArgumentException($"无效、重复或缺少值的选项：{args[i]}");
    }
    string Require(string name) => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"缺少 --{name}");

    switch (command)
    {
        case "verify-fine-generation":
        {
            var plan = RecordJson.Read<FineTargetPlan>(Require("plan"));
            string path = Require("generated"), folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var snapshot = RecordJson.Read<ExternalGenerationSnapshot>(path);
            plan.VerifyActualGeneration(snapshot, name => File.ReadAllBytes(Path.Combine(folder, name)));
            Console.WriteLine("Second-disc generation matches frozen G1, exact target and pinned mapping; physical validation pending.");
            break;
        }
        case "fit-trace":
        {
            string path = Require("generated");
            using var doc = RecordJson.ParseDocument(File.ReadAllText(path));
            GeometryParameters g; string hash, id;
            if (doc.RootElement.GetProperty("recordType").GetString() == "generation-snapshot")
            {
                var s = RecordJson.Read<GenerationSnapshot>(path); s.Validate(); g = s.Generated; hash = s.ContentSha256; id = s.Id;
            }
            else if (doc.RootElement.GetProperty("recordType").GetString() == "reference-target-simulation")
            {
                var s = RecordJson.Read<ReferenceTargetSimulation>(path); s.Validate(); g = s.Generated; hash = s.ContentSha256; id = s.Id;
            }
            else
            {
                var s = RecordJson.Read<ExternalGenerationSnapshot>(path); s.Validate(Path.GetDirectoryName(Path.GetFullPath(path))!);
                g = s.GenerationRecord.Generated; hash = s.ContentSha256; id = s.Id;
            }
            var session = RecordJson.Read<PhotoSession>(Require("session"));
            if (session.SnapshotId != id) throw new ArgumentException("照片会话的快照 ID 不匹配。");
            var settings = TraceFitSettings.Default with { ShapeOrder = int.Parse(options.GetValueOrDefault("shape-order", "0"), CultureInfo.InvariantCulture) };
            var result = TraceFitter.Fit(g, hash, session, settings, cancellation.Token);
            var review = new TraceFitReview(result, result.Candidates.FirstOrDefault(c => c.EligibleForReview)?.Id ?? result.Candidates[0].Id, null);
            OutputFolder.Publish(Require("out"), folder =>
            {
                RecordJson.WriteNew(Path.Combine(folder, "fit-review.json"), review);
                RecordJson.WriteNew(Path.Combine(folder, "photo-session.json"), session);
                File.WriteAllText(Path.Combine(folder, "curve.svg"), CalibrationExport.CurveSvg(review, session));
            }, cancellation.Token);
            Console.WriteLine($"Trace fit saved: {result.QualityStatus}; candidate confirmation pending.");
            break;
        }
        case "export-profile":
        {
            var session = RecordJson.Read<PhotoSession>(Require("session"));
            var review = RecordJson.Read<TraceFitReview>(Require("review"));
            if (Require("confirm-curve") != "true") throw new ArgumentException("检查照片完整预测线后，显式传入 --confirm-curve true。");
            if (options.TryGetValue("candidate", out var id)) review = review.Select(id);
            review = review.Confirm(review.Result.Generated, session.SnapshotSha256, session);
            CalibrationExport.Write(Require("out"), review, session, cancellation.Token);
            Console.WriteLine("Profile, residuals, curve and report exported; physical validation pending.");
            break;
        }
        case "rectify":
        {
            var session = RecordJson.Read<PhotoSession>(Require("session"));
            var result = session.Rectify(cancellation.Token);
            OutputFolder.Publish(Require("out"), folder => RecordJson.WriteNew(Path.Combine(folder, "photo-session.json"), result), cancellation.Token);
            Console.WriteLine("Dual-circle rectification saved; manual confirmation pending.");
            break;
        }
        case "inspect":
        {
            string path = Require("record");
            if (new FileInfo(path).Length > RecordJson.MaxBytes) throw new ArgumentException("记录超过 4 MB。");
            using var document = RecordJson.ParseDocument(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("recordType", out var type) && type.GetString() == "reference-target-simulation")
            {
                var snapshot = RecordJson.Read<ReferenceTargetSimulation>(path); snapshot.Validate();
                Print(new { status = "simulation-only", snapshot.Id, snapshot.ContentSha256, snapshot.PlanSha256, snapshot.Generated, snapshot.Target, physicalValidation = "pending" });
            }
            else if (document.RootElement.TryGetProperty("recordType", out type) && type.GetString() == "generation-snapshot")
            {
                var snapshot = RecordJson.Read<GenerationSnapshot>(path); snapshot.Validate();
                var geometry = new TrackGeometry(snapshot.Generated);
                Print(new { status = "snapshot-valid", snapshot.Id, snapshot.ContentSha256, snapshot.Generator.SourceKind,
                    geometry.Parameters, geometry.PitchBaseMm, geometry.TotalAngleRadians, physicalValidation = "pending" });
            }
            else if (document.RootElement.TryGetProperty("recordType", out type) && type.GetString() == "external-generation-snapshot")
            {
                var snapshot = RecordJson.Read<ExternalGenerationSnapshot>(path);
                snapshot.Validate(Path.GetDirectoryName(Path.GetFullPath(path))!);
                Print(new { status = "external-snapshot-valid", snapshot.Id, snapshot.ContentSha256,
                    snapshot.Declarations, snapshot.GenerationRecord.Generated, physicalValidation = "pending",
                    warning = "source-image-and-generator-identity-are-user-declared-not-proven-by-sidecar" });
            }
            else Print(DiscImageRecord.Read(path));
            break;
        }
        case "freeze-discimage":
        {
            string path = Require("record"), image = Require("image");
            var declarations = new GenerationDeclarations(new(Require("media"), 120, 15, 1, null),
                long.Parse(Require("confirmed-sectors"), CultureInfo.InvariantCulture), Require("generator-version"),
                Require("generator-commit"), "user-declared");
            OutputFolder.Publish(Require("out"), folder =>
            {
                var snapshot = ExternalGenerationSnapshot.CreateInFolder(path, image, declarations, folder);
                RecordJson.WriteNew(Path.Combine(folder, "generation.json"), snapshot);
            }, cancellation.Token);
            Console.WriteLine("External generation snapshot frozen with source attachments and explicit user declarations; physical validation pending.");
            break;
        }
        case "import-discimage":
        {
            string path = Require("record");
            OutputFolder.Publish(Require("out"), folder =>
            {
                File.Copy(path, Path.Combine(folder, "source.json"));
                var imported = DiscImageRecord.Read(Path.Combine(folder, "source.json"));
                RecordJson.WriteNew(Path.Combine(folder, "generation-record.json"), imported);
            }, cancellation.Token);
            Console.WriteLine("Imported; status=record-incomplete. See generation-record.json for missing evidence.");
            break;
        }
        case "predict":
        {
            var snapshot = RecordJson.Read<GenerationSnapshot>(Require("generated"));
            var actual = RecordJson.Read<GeometryParameters>(Require("actual"));
            int samples = int.Parse(options.GetValueOrDefault("samples", "2049"), CultureInfo.InvariantCulture);
            double rotation = double.Parse(options.GetValueOrDefault("rotation-deg", "0"), CultureInfo.InvariantCulture);
            var result = Prediction.Calculate(snapshot, actual, rotation * Math.PI / 180, samples, cancellation.Token);
            OutputFolder.Publish(Require("out"), folder =>
            {
                RecordJson.WriteNew(Path.Combine(folder, "generation.json"), snapshot);
                Prediction.WriteArtifacts(folder, snapshot, result);
            }, cancellation.Token);
            Console.WriteLine($"Prediction saved; {samples} samples, physicalValidation=pending.");
            break;
        }
        case "demo":
        {
            var generated = new GeometryParameters(24, 58, 2_295_104, 133.3, .02, -.03, .015,
                SpiralDirection.Cw, GeometryParameters.Convention, 0);
            var actual = generated with { InnerRadiusMm = 24.003, OuterRadiusMm = 58.006,
                PitchLinear = .0204, PitchQuadratic = -.0305, PitchCubic = .0152 };
            var snapshot = GenerationSnapshot.CreateSynthetic("synthetic-coarse-0001",
                new("DVD+R", 120, 15, 1, null), generated, new("half-v1", "half-disc", .3, "analytic-disc-xy-v1"));
            var result = Prediction.Calculate(snapshot, actual, .2, 4097, cancellation.Token);
            OutputFolder.Publish(Require("out"), folder =>
            {
                RecordJson.WriteNew(Path.Combine(folder, "generation.json"), snapshot);
                RecordJson.WriteNew(Path.Combine(folder, "actual.json"), actual);
                Prediction.WriteArtifacts(folder, snapshot, result);
                PatternRaster.WritePgm(Path.Combine(folder, "source.pgm"), snapshot, generated, 0, 384, cancellation.Token);
                PatternRaster.WritePgm(Path.Combine(folder, "predicted.pgm"), snapshot, actual, .2, 384, cancellation.Token);
            }, cancellation.Token);
            Console.WriteLine("Synthetic demo saved: generation.json, actual.json, prediction.json, curve.csv, prediction.svg, source.pgm, predicted.pgm.");
            break;
        }
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
catch (Exception e) when (e is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
{ Console.Error.WriteLine($"Invalid input: {e.Message}"); return 2; }
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{ Console.Error.WriteLine($"I/O failure: {e.Message}"); return 3; }

static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, RecordJson.Options));

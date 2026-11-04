using System.Text.Json;
using System.Text.Json.Nodes;
using DvdCalibrator.Core;

internal static class ReferenceTargetChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid target plan accepted."); }
    public static void Register(Action<string, Action> test, string root, string output)
    {
        test("exact pinned reference image and actual upstream raster samples including subpixel scale", () =>
        {
            Check(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ReferenceTarget.PngBytes())) == ReferenceTargetContract.Default.PngSha256, "PNG changed");
            Check(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ReferenceTarget.GrayPixels())) == "3d351bdfb817b943c1d1b4aedc97fc4f0a111e7e1537748f70cb5b180ba96852", "gray pixels changed");
            using var fixture = RecordJson.ParseDocument(File.ReadAllText(Path.Combine(root, "fixtures/upstream-target-contract.json")));
            Check(fixture.RootElement.GetProperty("upstreamCommit").GetString() == DiscImageRecord.UpstreamCommit, "fixture version differs");
            foreach (var sample in fixture.RootElement.GetProperty("pixelSamples").EnumerateArray())
                Check(ReferenceTarget.Sample(sample.GetProperty("xMm").GetDouble(), sample.GetProperty("yMm").GetDouble(), sample.GetProperty("outerRadiusMm").GetDouble())
                    == sample.GetProperty("luminance").GetByte(), "upstream raster sample differs");
            Check(Math.Abs(ReferenceTarget.RingRadiiMm(58)[0] - 490 * 58d / 1199.5) < 1e-14, "ring uses physical 60 mm canvas instead of generated ro");
        });
        test("complete track and image contract matches recorded upstream CW CCW and nonzero start angle", () =>
        {
            using var fixture = RecordJson.ParseDocument(File.ReadAllText(Path.Combine(root, "fixtures/upstream-target-contract.json")));
            foreach (var sample in fixture.RootElement.GetProperty("progressSamples").EnumerateArray())
            {
                var g = RecordJson.Parse<GeometryParameters>(sample.GetProperty("generated").GetRawText()); var geometry = new TrackGeometry(g);
                double progress = sample.GetProperty("progress").GetDouble(), r = geometry.RadiusAtProgress(progress), start = sample.GetProperty("startAngleDegrees").GetDouble();
                Check(Math.Abs(r - sample.GetProperty("radiusMm").GetDouble()) < 2e-10, "engine inverse differs");
                Check(Math.Abs(geometry.AngleAtRadius(r) + start * Math.PI / 180 - sample.GetProperty("angleRadians").GetDouble()) < 2e-8, "engine angle differs");
                Check(ReferenceTarget.AtProgress(g, progress, start) == sample.GetProperty("luminance").GetByte(), "engine track/raster value differs");
            }
        });
        var g0 = new GeometryParameters(24, 58, 2295104, 133.3, .02, -.03, .015, SpiralDirection.Cw, GeometryParameters.Convention, 0);
        var p1 = g0 with { InnerRadiusMm = 24.003, OuterRadiusMm = 58.006 };
        var evidence = new CoarsePlanEvidence("trace", new string('a', 64), new string('b', 64), new string('c', 64), "candidate-1", p1, [new("candidate-1", p1)]);
        test("fine plan freezes all P1 fields and requires a separate hash-bound review", () =>
        {
            var plan = FineTargetPlan.Create(new("DVD+R", 120, 15, 1, null), evidence, 8);
            Check(!plan.IsConfirmed && plan.Generated == p1 && plan.Generated != g0, "G1 not frozen from P1");
            plan = plan.Confirm(); Check(plan.IsConfirmed && plan.Forecast.Status == "plan-review", "plan not reviewable");
            var copy = RecordJson.Parse<FineTargetPlan>(JsonSerializer.Serialize(plan, RecordJson.Options)); copy.Validate();
            Reject(() => (copy with { Generated = g0 }).Validate());
            Reject(() => (copy with { ExpectedPhotoPixelsPerMm = 9 }).Validate());
            Reject(() => (copy with { Forecast = copy.Forecast with { MinimumLineWidthPixels = 100 } }).Validate());
            var low = FineTargetPlan.Create(plan.Disc, evidence, 1); Reject(() => low.Confirm());
            Reject(() => (low with { ConfirmedPlanSha256 = low.ContentSha256 }).Validate());
        });
        test("conflicting supported candidates cannot confirm the next target", () =>
        {
            var competing = p1 with { InnerRadiusMm = 24.004, OuterRadiusMm = 58.008 };
            var e = evidence with { Alternatives = [new("candidate-1", p1), new("candidate-2", competing)] };
            var plan = FineTargetPlan.Create(new("DVD+R", 120, 15, 1, null), e, 20);
            Check(plan.Forecast.MaximumCandidateDifferenceMm > .2, "conflict fixture did not diverge"); Reject(() => plan.Confirm());
        });
        test("actual upstream reference sidecar freezes and verifies exact target contract with mismatch rejection", () =>
        {
            var record = DiscImageRecord.Read(Path.Combine(root, "fixtures/upstream-reference-fast-fill.iso.json"));
            var e = evidence with { Parameters = record.Generated, Alternatives = [new("candidate-1", record.Generated)] };
            var plan = FineTargetPlan.Create(new("DVD+R", 120, 15, 1, null), e, 8).Confirm();
            Directory.CreateDirectory(output);
            string png = Path.Combine(output, "target_slim.png"); File.WriteAllBytes(png, ReferenceTarget.PngBytes());
            string frozen = Path.Combine(output, "frozen"); Directory.CreateDirectory(frozen);
            var snapshot = ExternalGenerationSnapshot.CreateInFolder(Path.Combine(root, "fixtures/upstream-reference-fast-fill.iso.json"), png,
                new(plan.Disc, 16, "1.3.6", DiscImageRecord.UpstreamCommit, "user-declared"), frozen);
            plan.VerifyActualGeneration(snapshot, name => File.ReadAllBytes(Path.Combine(frozen, name)));
            var unspecifiedDiscPlan = FineTargetPlan.Create(DiscSpec.UnspecifiedDvd, e, 8).Confirm();
            unspecifiedDiscPlan.VerifyActualGeneration(snapshot, name => File.ReadAllBytes(Path.Combine(frozen, name)));
            RecordJson.WriteNew(Path.Combine(frozen, "generation.json"), snapshot);
            var mismatchedPlan = FineTargetPlan.Create(plan.Disc, e with { Parameters = record.Generated with { OuterRadiusMm = record.Generated.OuterRadiusMm + .001 },
                Alternatives = [new("candidate-1", record.Generated with { OuterRadiusMm = record.Generated.OuterRadiusMm + .001 })] }, 8).Confirm();
            Reject(() => mismatchedPlan.VerifyActualGeneration(snapshot, name => File.ReadAllBytes(Path.Combine(frozen, name))));
            var changedTarget = plan with { Target = plan.Target with { StartAngleDegrees = 17 } }; Reject(changedTarget.Validate);
            foreach (var (field, value) in new[] { ("startAngleDegrees", 17), ("luminanceThreshold", 129), ("alphaThreshold", 1), ("sampleEveryChannelBits", 2) })
            {
                var altered = JsonNode.Parse(record.OriginalRecord.GetRawText())!; altered["imageMapping"]![field] = value;
                string changedRecord = Path.Combine(output, field + ".json"); File.WriteAllText(changedRecord, altered.ToJsonString());
                string changedFolder = Path.Combine(output, field); Directory.CreateDirectory(changedFolder);
                var changed = ExternalGenerationSnapshot.CreateInFolder(changedRecord, png,
                    new(plan.Disc, 16, "1.3.6", DiscImageRecord.UpstreamCommit, "user-declared"), changedFolder);
                Reject(() => plan.VerifyActualGeneration(changed, name => File.ReadAllBytes(Path.Combine(changedFolder, name))));
            }
            var psnRecord = JsonNode.Parse(record.OriginalRecord.GetRawText())!; psnRecord["psnOffset"] = "0x0";
            string psnPath = Path.Combine(output, "wrong-psn.json"); File.WriteAllText(psnPath, psnRecord.ToJsonString());
            string psnFolder = Path.Combine(output, "wrong-psn"); Directory.CreateDirectory(psnFolder);
            var psnSnapshot = ExternalGenerationSnapshot.CreateInFolder(psnPath, png, new(plan.Disc, 16, "1.3.6", DiscImageRecord.UpstreamCommit, "user-declared"), psnFolder);
            Reject(() => plan.VerifyActualGeneration(psnSnapshot, name => File.ReadAllBytes(Path.Combine(psnFolder, name))));
            FineTargetExport.Write(Path.Combine(output, "target-job"), plan);
            Reject(() => FineTargetExport.Write(Path.Combine(output, "unconfirmed"), plan with { ConfirmedPlanSha256 = null }));
            var simulation = ReferenceTargetSimulation.Create("fine-simulation", plan.ContentSha256, plan.Disc, plan.Generated); simulation.Validate();
            Check(simulation.EvidenceSource == "simulation", "simulation presented as actual generation");
        });
    }
}

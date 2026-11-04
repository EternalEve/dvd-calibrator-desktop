using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DvdCalibrator.Core;

if (args is not ["--engine", var engineArgument, "--out", var output]) throw new ArgumentException("--engine DLL --out NEW_FOLDER");
string engine = Path.GetFullPath(engineArgument), directory = Path.GetDirectoryName(engine)!;
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(directory, name.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(engine);
const BindingFlags allInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
const BindingFlags allStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
OutputFolder.Publish(output, folder =>
{
    // Use the actual upstream serializer, rather than inventing our own calibration fixture.
    var upstreamCore = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "DiscImageStudio.Core.dll"));
    var parameterType = upstreamCore.GetType("DiscImageStudio.Core.Calibration.CalibrationParameters", true)!;
    var kindType = upstreamCore.GetType("DiscImageStudio.Core.Calibration.CalibrationDiscKind", true)!;
    object parameters = Activator.CreateInstance(parameterType, [Enum.Parse(kindType, "Dvd"), 24.003, 58.006, 2295104L,
        133.3, .02, -.03, .015, false, 0L, null, 0L])!;
    var targetType = upstreamCore.GetType("DiscImageStudio.Core.Calibration.CalibrationTarget", true)!;
    object target = targetType.GetMethod("Create")!.Invoke(null, [parameters, null])!;
    var observationType = upstreamCore.GetType("DiscImageStudio.Core.Calibration.CalibrationObservation", true)!;
    object observations = Activator.CreateInstance(typeof(List<>).MakeGenericType(observationType))!;
    var sessionType = upstreamCore.GetType("DiscImageStudio.Core.Calibration.CalibrationSession", true)!;
    object session = Activator.CreateInstance(sessionType, [target, observations, null, true, null])!;
    string calibrationJson = (string)upstreamCore.GetType("DiscImageStudio.Core.Calibration.CalibrationSessionJson", true)!
        .GetMethod("Serialize")!.Invoke(null, [session])!;
    File.WriteAllText(Path.Combine(folder, "upstream-calibration.calibration.json"), calibrationJson);
    var calibration = CalibrationGeneration.Import(System.Text.Encoding.UTF8.GetBytes(calibrationJson), new("DVD+R", 120, 15, 1, null));
    calibration.ValidateAttachments(_ => System.Text.Encoding.UTF8.GetBytes(calibrationJson));
    string image = Path.Combine(folder, "target_slim.png"); File.WriteAllBytes(image, ReferenceTarget.PngBytes());
    var rasterType = assembly.GetType("DvdImageSolver.Solver.RasterImage", throwOnError: true)!;
    object raster = rasterType.GetMethod("Load", allStatic)!.Invoke(null, [image])!;
    var sampleMethod = rasterType.GetMethod("SampleDisc", allInstance)!;
    byte ActualSample(double x, double y, double ro)
    {
        object pixel = sampleMethod.Invoke(raster, [x, y, ro, false])!;
        return (byte)pixel.GetType().GetProperty("Luminance", allInstance)!.GetValue(pixel)!;
    }
    var random = new Random(19790417); var pixelSamples = new List<object>();
    for (int i = 0; i < 20000; i++)
    {
        double outer = i % 2 == 0 ? 58 : 57.456789, r = (0.35 + .65 * random.NextDouble()) * outer, a = random.NextDouble() * 2 * Math.PI;
        double x = r * Math.Cos(a), y = r * Math.Sin(a); byte actual = ActualSample(x, y, outer);
        if (ReferenceTarget.Sample(x, y, outer) != actual) throw new Exception("Actual upstream RasterImage sample mismatch.");
        if (i < 128) pixelSamples.Add(new { xMm = x, yMm = y, outerRadiusMm = outer, luminance = actual });
    }
    var spiralType = assembly.GetType("DvdImageSolver.Solver.ArchimedeanSpiral", true)!;
    var create = spiralType.GetMethods(allStatic).Single(m => m.Name == "Create" && m.GetParameters().Length == 6);
    var progressSamples = new List<object>();
    foreach (var direction in new[] { SpiralDirection.Cw, SpiralDirection.Ccw })
    {
        var g = new GeometryParameters(24.00123, 58.00678, 2295104, 133.33, .02, -.03, .015, direction, GeometryParameters.Convention, 0);
        object spiral = create.Invoke(null, [g.InnerRadiusMm, g.OuterRadiusMm, g.TrackLengthMm, g.PitchLinear, g.PitchQuadratic, g.PitchCubic])!;
        for (int i = 0; i <= 32; i++)
        {
            double progress = i / 32d;
            double r = (double)spiralType.GetMethod("RadiusAtArcLength", allInstance)!.Invoke(spiral, [progress * g.TrackLengthMm])!;
            double angle = (double)spiralType.GetMethod("AngleAtRadius", allInstance)!.Invoke(spiral, [r])! * (direction == SpiralDirection.Cw ? -1 : 1) + 17 * Math.PI / 180;
            byte value = ActualSample(r * Math.Cos(angle), r * Math.Sin(angle), g.OuterRadiusMm);
            if (ReferenceTarget.AtProgress(g, progress, 17) != value) throw new Exception("Actual upstream track/image mapping mismatch.");
            progressSamples.Add(new { generated = g, progress, startAngleDegrees = 17, radiusMm = r, angleRadians = angle, luminance = value });
        }
    }
    RecordJson.WriteNew(Path.Combine(folder, "contract-samples.json"), new { upstreamCommit = DiscImageRecord.UpstreamCommit,
        pngSha256 = ReferenceTargetContract.Default.PngSha256, testedPixels = 20000, pixelSamples, progressSamples });
    var small = new GeometryParameters(24.00123, 58.00678, 16, 133.33, .02, -.03, .015, SpiralDirection.Cw, GeometryParameters.Convention, 0);
    var evidence = new CoarsePlanEvidence("trace", new string('a', 64), new string('b', 64), new string('c', 64), "candidate-1", small, [new("candidate-1", small)]);
    var plan = FineTargetPlan.Create(new("DVD+R", 120, 15, 1, null), evidence, 8).Confirm();
    string job = Path.Combine(folder, "generation-job"); FineTargetExport.Write(job, plan);
    var arguments = RecordJson.Read<string[]>(Path.Combine(job, "generator-arguments.json"));
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = job, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.ArgumentList.Add(engine); foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Could not start upstream.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit(); string log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
    File.WriteAllText(Path.Combine(folder, "engine-generation.log"), log);
    if (process.ExitCode != 0) throw new Exception("Actual upstream generation failed: " + log);
    string frozen = Path.Combine(folder, "actual-generation"); Directory.CreateDirectory(frozen);
    var snapshot = ExternalGenerationSnapshot.CreateInFolder(Path.Combine(job, "fine.iso.json"), Path.Combine(job, "target_slim.png"),
        new(plan.Disc, 16, "1.3.6", DiscImageRecord.UpstreamCommit, "user-declared"), frozen);
    plan.VerifyActualGeneration(snapshot, name => File.ReadAllBytes(Path.Combine(frozen, name)));
    RecordJson.WriteNew(Path.Combine(frozen, "generation.json"), snapshot);
    Console.WriteLine("PASS actual upstream: 20000 raster samples, 66 complete track mappings, exact PNG, structured CLI generation, sidecar freeze/readback (16-sector software fixture).");
});

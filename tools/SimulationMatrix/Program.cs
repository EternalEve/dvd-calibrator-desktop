using System.Collections.Immutable;
using System.IO;
using DvdCalibrator.Core;
using SimulationMatrix;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is not ["--out", var output, "--suite", var suite] || suite is not ("quick" or "full"))
        { Console.Error.WriteLine("--out NEW_FOLDER --suite quick|full"); return 2; }
        using var cancellation = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        bool pass = true;
        try
        {
            OutputFolder.Publish(output, folder =>
            {
                OracleTrack.Verify(cancellation.Token); Console.WriteLine("PASS independent oracle refinement (Simpson tables vs full integral/bisection)");
                var results = ImmutableArray.CreateBuilder<CaseResult>();
                foreach (var scenario in Scenario.All(suite == "quick"))
                {
                    cancellation.Token.ThrowIfCancellationRequested(); var result = MatrixRun.Run(scenario, folder, cancellation.Token); results.Add(result);
                    string label = result.KnownLimitation ? "LIMIT" : result.MeetsExpectation ? "PASS" : "FAIL";
                    Console.WriteLine($"{label} {scenario.Id}: {result.Outcome} / {result.Stage} / {result.Status}; prediction max {result.Metrics.PredictionMaxMm:G5} mm");
                    pass &= result.MeetsExpectation;
                }
                var report = new MatrixReport(1, "two-disc-simulation-matrix", "0.6.0", suite,
                    "independent-simpson-cells-bisection-v1", "xorshift32-box-muller-v1", "pending",
                    MatrixRun.RadiusBudget, MatrixRun.PredictionRmsBudget, MatrixRun.PredictionMaxBudget, MatrixRun.PhaseBudget, results.ToImmutable());
                Reports.Write(folder, report);
                var graded = results.Where(c => c.Scenario.Expectation != "characterize").ToArray();
                Console.WriteLine($"Matrix: {graded.Count(c => c.MeetsExpectation)}/{graded.Length} graded cases; {results.Count(c => c.Scenario.Expectation == "characterize")} separate boundary probes; {results.Count(c => c.KnownLimitation)} truth-budget violations among probes. Not a physical-disc success rate.");
            }, cancellation.Token);
            Console.WriteLine("Matrix artifacts: " + Path.GetFullPath(output)); return pass ? 0 : 1;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Matrix cancelled; unpublished staging output cleaned."); return 130; }
        catch (Exception error) when (error is ArgumentException or IOException) { Console.Error.WriteLine(error.Message); return 2; }
    }
}

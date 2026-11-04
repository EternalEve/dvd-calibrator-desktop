// GPA / wfr3 adapted from pyGPA, Copyright (c) 2021 Tobias A. de Jong, MIT.
// Original source and license: third_party/pyGPA. FFTs are provided by Math.NET Numerics.
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace DvdCalibrator.Core;

public sealed record FourierRidgeField(double[] Amplitude, double[] Phase, double[] Frequency, double[] Coherence, double[] Coverage);

public static class WindowedFourierRidges
{
    // This is the author's spatial lock-in + maximum-amplitude ridge selection,
    // restricted to radial carriers in an already rectified polar image.
    public static FourierRidgeField Analyze(double[] image, int rows, int columns,
        double[] frequencies, double radialSigma = 9, double angularSigma = 1,
        CancellationToken token = default)
    {
        if (rows < 32 || columns < 8 || image.Length != rows * columns || frequencies.Length is < 1 or > 64
            || frequencies.Any(f => !double.IsFinite(f) || f <= 0 || f >= .5)
            || !double.IsFinite(radialSigma) || !double.IsFinite(angularSigma)
            || radialSigma <= 0 || angularSigma <= 0) throw new ArgumentException("WFR 输入或频率范围无效。");
        var valid = image.Select(v => double.IsFinite(v) ? 1d : 0d).ToArray();
        var data = image.Select(v => double.IsFinite(v) ? v : 0d).ToArray();
        var baseline = Smooth(data, rows, columns, 20, 0, token);
        var baselineMass = Smooth(valid, rows, columns, 20, 0, token);
        for (int i = 0; i < data.Length; i++) data[i] = valid[i] == 0 ? 0 : data[i] - baseline[i] / Math.Max(.01, baselineMass[i]);
        var power = Smooth(data.Select(v => v * v).ToArray(), rows, columns, radialSigma, angularSigma, token);
        var coverage = Smooth(valid, rows, columns, radialSigma, angularSigma, token);
        var amplitude = new double[data.Length]; var phase = new double[data.Length]; var frequency = new double[data.Length];
        foreach (double f in frequencies)
        {
            token.ThrowIfCancellationRequested(); var samples = new Complex[data.Length];
            for (int r = 0; r < rows; r++)
            {
                var carrier = Complex.FromPolarCoordinates(1, 2 * Math.PI * f * r);
                for (int c = 0; c < columns; c++) samples[r * columns + c] = data[r * columns + c] * carrier;
            }
            Transform(samples, rows, columns, false, token);
            ApplyGaussian(samples, rows, columns, radialSigma, angularSigma);
            Transform(samples, rows, columns, true, token);
            for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++)
            {
                int i = r * columns + c; double a = samples[i].Magnitude;
                if (a <= amplitude[i]) continue;
                amplitude[i] = a; frequency[i] = f;
                // Restore absolute positive-carrier phase from pyGPA's reference-relative lock-in.
                phase[i] = Math.Atan2(Math.Sin(2 * Math.PI * f * r - samples[i].Phase), Math.Cos(2 * Math.PI * f * r - samples[i].Phase));
            }
        }
        var coherence = amplitude.Select((a, i) => 2 * a * a / Math.Max(1e-12, power[i])).ToArray();
        return new(amplitude, phase, frequency, coherence, coverage);
    }

    private static double[] Smooth(double[] data, int rows, int columns, double sr, double sc, CancellationToken token)
    {
        var samples = data.Select(v => new Complex(v, 0)).ToArray();
        Transform(samples, rows, columns, false, token); ApplyGaussian(samples, rows, columns, sr, sc);
        Transform(samples, rows, columns, true, token); return samples.Select(v => v.Real).ToArray();
    }
    private static void ApplyGaussian(Complex[] samples, int rows, int columns, double sr, double sc)
    {
        for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++)
        {
            double fr = (r <= rows / 2 ? r : r - rows) / (double)rows, fc = (c <= columns / 2 ? c : c - columns) / (double)columns;
            samples[r * columns + c] *= Math.Exp(-2 * Math.PI * Math.PI * (sr * sr * fr * fr + sc * sc * fc * fc));
        }
    }
    private static void Transform(Complex[] samples, int rows, int columns, bool inverse, CancellationToken token)
    {
        // The managed Math.NET provider supplies the 1D transforms; separability supplies the layout.
        var row = new Complex[columns]; var column = new Complex[rows];
        void Run(Complex[] values) { if (inverse) Fourier.Inverse(values, FourierOptions.Matlab); else Fourier.Forward(values, FourierOptions.Matlab); }
        for (int r = 0; r < rows; r++) { token.ThrowIfCancellationRequested(); Array.Copy(samples, r * columns, row, 0, columns); Run(row); Array.Copy(row, 0, samples, r * columns, columns); }
        for (int c = 0; c < columns; c++) { token.ThrowIfCancellationRequested(); for (int r = 0; r < rows; r++) column[r] = samples[r * columns + c]; Run(column); for (int r = 0; r < rows; r++) samples[r * columns + c] = column[r]; }
    }
}

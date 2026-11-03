using System.Text;

namespace DvdCalibrator.Core;

public static class PatternRaster
{
    // Portable Graymap P5 avoids platform image dependencies; this is point sampling, not optical rendering.
    public static void WritePgm(string path, GenerationSnapshot snapshot, GeometryParameters actual,
        double rotationRadians, int size, CancellationToken cancellationToken = default)
    {
        snapshot.Validate();
        if (size is < 32 or > 2048) throw new ArgumentException("栅格尺寸要求 32..2048。");
        if (!double.IsFinite(rotationRadians)) throw new ArgumentException("转角必须有限。");
        var model = new ForwardModel(snapshot.Generated, actual);
        using var output = new FileStream(path, FileMode.CreateNew);
        output.Write(Encoding.ASCII.GetBytes($"P5\n# Synthetic point samples; aliasing possible; not optical brightness\n{size} {size}\n255\n"));
        byte[] row = new byte[size];
        for (int y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double ym = 60 - (y + .5) * 120 / size;
            for (int x = 0; x < size; x++)
            {
                double xm = (x + .5) * 120 / size - 60;
                double radius = Math.Sqrt(xm * xm + ym * ym);
                row[x] = 200;
                if (radius < actual.InnerRadiusMm || radius > actual.OuterRadiusMm) continue;
                var point = model.AtRadius(radius, rotationRadians: rotationRadians);
                double sourceAngle = Math.Atan2(ym, xm) - point.PhaseRadians - rotationRadians;
                row[x] = snapshot.Pattern.IsWhite(point.SourceRadiusMm, sourceAngle) ? (byte)255 : (byte)0;
            }
            output.Write(row);
        }
    }
}

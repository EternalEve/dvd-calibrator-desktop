using System.Collections.Immutable;
using System.Text.Json;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;

namespace SimulationMatrix;

internal static class Scene
{
    public static PhotoTask Coarse(Scenario s, CancellationToken token)
    {
        var snapshot = GenerationSnapshot.CreateSynthetic(s.Id + "-g0", new("DVD+R", 120, 15, 1, null), s.Generated,
            new("matrix-half-disc-v1", "half-disc", .3, "analytic-disc-xy-v1"));
        var bundle = new SnapshotBundle(new() { ["generation.json"] = JsonSerializer.SerializeToUtf8Bytes(snapshot, RecordJson.Options) });
        return Create(s, bundle, s.FirstActual, false, token);
    }
    public static PhotoTask Fine(Scenario s, FineTargetPlan plan, CancellationToken token)
    {
        var snapshot = ReferenceTargetSimulation.Create(s.Id + "-g1", plan.ContentSha256, plan.Disc, s.Fault == "wrong-g0" ? s.Generated : plan.Generated);
        var bundle = new SnapshotBundle(new() { ["generation.json"] = JsonSerializer.SerializeToUtf8Bytes(snapshot, RecordJson.Options) });
        return Create(s, bundle, s.SecondActual, true, token, plan.Generated);
    }
    private static PhotoTask Create(Scenario s, SnapshotBundle bundle, GeometryParameters actual, bool fine, CancellationToken token, GeometryParameters? renderingGeneration = null)
    {
        int size = fine ? s.FinePixels : 900; double scale = size / 900d;
        ImmutableArray<double> matrix = s.Perspective
            ? [6.7 * scale, 1.2 * scale, 450 * scale, .25 * scale, 5.1 * scale, 450 * scale, .001, -.0014, 1]
            : [6.5 * scale, .2 * scale, 450 * scale, -.1 * scale, 6.4 * scale, 450 * scale, .0001, -.0001, 1];
        var h = new DiscPhotoTransform(matrix); var g = renderingGeneration ?? bundle.Geometry;
        var truth = new TruthMapping(g, actual, token); double beta = fine ? -.47 : .41;
        var noise = new Noise((uint)(s.Seed + (fine ? 104729 : 13))); byte[] pixels = new byte[size * size * 3];
        bool Hidden(double r, double a) => s.MissingBand && r is > 37 and < 41 || s.MaskDegrees > 0 && ((a % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI)) < s.MaskDegrees * Math.PI / 180;
        for (int y = 0; y < size; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < size; x++)
            {
                var d = h.SourceToDisc(new(x, y)); double r = Math.Sqrt(d.X * d.X + d.Y * d.Y), a = Math.Atan2(d.Y, d.X), value = r is >= 7.5 and <= 60 ? 210 : 35;
                if (r >= actual.InnerRadiusMm && r <= actual.OuterRadiusMm)
                {
                    var sample = truth.Interpolate(r); double source = a - sample.Phase - beta;
                    double gray = fine ? ReferenceTarget.Sample(sample.SourceRadius * Math.Cos(source), sample.SourceRadius * Math.Sin(source), g.OuterRadiusMm)
                        : Math.Sin(source) >= 0 ? 255 : 0;
                    value = s.Fault == "blank" && !fine ? 128 : 60 + 136 * gray / 255;
                    if (Hidden(r, a)) value = 255;
                }
                int at = (y * size + x) * 3;
                for (int c = 0; c < 3; c++) pixels[at + c] = (byte)Math.Clamp(Math.Round(128 + (value - 128) * (c == 1 ? 1 : c == 0 ? .5 : .7) + (s.PixelNoiseSigmaDn == 0 ? 0 : s.PixelNoiseSigmaDn * noise.Normal())), 0, 255);
            }
        }
        var image = new RgbImage(size, size, pixels); byte[] png = PhotoCodec.Encode(image);
        var photo = new ImportedPhoto(png, png, new(PhotoCodec.Hash(png), PhotoCodec.Hash(png), image.PixelsSha256, size, size, size, size, 1), image);
        var contours = new Noise((uint)(s.Seed + (fine ? 65537 : 257)));
        ImmutableArray<PixelPoint> Circle(double r) => Enumerable.Range(0, 32).Select(i =>
        {
            double annotated = r * (1 + s.SharedContourScaleBias), a = i * Math.PI / 16;
            var p = h.DiscToSource(new(annotated * Math.Cos(a), annotated * Math.Sin(a)));
            return new PixelPoint(p.X + s.ContourSigmaPx * contours.Normal(), p.Y + s.ContourSigmaPx * contours.Normal());
        }).ToImmutableArray();
        var session = PhotoSession.Create(bundle.Id, bundle.Hash, photo.Metadata).WithBoundaries(Circle(60), Circle(!fine && s.Fault == "wrong-hole" ? 12 : 7.5));
        if (s.MaskDegrees > 0)
        {
            var vertices = new List<PixelPoint> { h.DiscToSource(new(0, 0)) };
            vertices.AddRange(Enumerable.Range(0, 33).Select(i => { double a = s.MaskDegrees * Math.PI / 180 * i / 32; return h.DiscToSource(new(60.2 * Math.Cos(a), 60.2 * Math.Sin(a))); }));
            session = session.WithMasks([new(vertices.ToImmutableArray())]);
        }
        session = session.Rectify(token).Confirm();
        if (fine)
        {
            var clicks = new Noise((uint)(s.Seed + 99991)); double lo = Math.Max(g.InnerRadiusMm, actual.InnerRadiusMm) + .6, hi = Math.Min(g.OuterRadiusMm, actual.OuterRadiusMm) - .6;
            ImmutableArray<PixelPoint> Trace(double offset, int count) => Enumerable.Range(0, count).Select(i =>
            {
                double r = lo + (hi - lo) * i / (count - 1), a = truth.At(r).Phase + beta + offset;
                if (offset == 0 && s.Fault == "branch-switch" && i >= count / 2) a += Math.PI;
                var p = h.DiscToSource(new(r * Math.Cos(a), r * Math.Sin(a)));
                return (R: r, A: a, Point: new PixelPoint(p.X + s.ClickSigmaPx * clicks.Normal(), p.Y + s.ClickSigmaPx * clicks.Normal()));
            }).Where(p => !Hidden(p.R, p.A)).Select(p => p.Point).ToImmutableArray();
            var main = Trace(0, 96); var validation = Trace(s.Fault == "wrong-arm" ? Math.PI / 2 : Math.PI, 48);
            if (s.Fault == "reverse-order") main = main.Reverse().ToImmutableArray();
            if (s.Fault == "short-coverage") main = main.Skip(30).Take(35).ToImmutableArray();
            if (s.Fault == "masked-point")
            {
                var p = main[20]; session = session.WithMasks(session.Masks.Add(new([new(p.X - 3, p.Y - 3), new(p.X + 3, p.Y - 3), new(p.X + 3, p.Y + 3), new(p.X - 3, p.Y + 3)])));
            }
            session = session.WithTraces([new("line-1", main), new("validation-180", validation)]);
        }
        return new(bundle, photo, session);
    }
}

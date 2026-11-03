using System.Collections.Immutable;
using DvdCalibrator.Core;
using System.Text.Json;

namespace DvdCalibrator.Imaging.Windows;

public static class SyntheticPhoto
{
    public static PhotoTask Create()
        => CreatePhoto(false);
    public static PhotoTask CreateTraceDemo()
        => CreatePhoto(true);
    public static PhotoTask CreateDenseDemo()
    {
        var g = new GeometryParameters(24, 58, 2295104, 133.3, .02, -.03, .015, SpiralDirection.Cw, GeometryParameters.Convention, 0);
        var snapshot = GenerationSnapshot.CreateSynthetic("synthetic-dense-mixed-demo", new("DVD+R", 120, 15, 1, null), g,
            new("half-disc-dense-demo", "half-disc", .3, "analytic-disc-xy-v1"));
        var bundle = new SnapshotBundle(new() { ["generation.json"] = JsonSerializer.SerializeToUtf8Bytes(snapshot, RecordJson.Options) });
        var actual = g with { InnerRadiusMm = 24.003, OuterRadiusMm = 58.006 };
        var model = new ForwardModel(g, actual);
        var table = Enumerable.Range(0, 4097).Select(i => model.AtRadius(actual.InnerRadiusMm + (actual.OuterRadiusMm - actual.InnerRadiusMm) * i / 4096).PhaseRadians + .41).ToArray();
        double Phase(double r)
        {
            double x = (r - actual.InnerRadiusMm) / (actual.OuterRadiusMm - actual.InnerRadiusMm) * 4096;
            int i = Math.Clamp((int)x, 0, 4095); return table[i] + (table[i + 1] - table[i]) * (x - i);
        }
        var h = new DiscPhotoTransform([7.5, .2, 500, -.1, 7.4, 500, .0001, -.0001, 1]);
        byte[] pixels = new byte[1000 * 1000 * 3];
        for (int y = 0; y < 1000; y++) for (int x = 0; x < 1000; x++)
        {
            var d = h.SourceToDisc(new(x, y)); double r = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            byte v = 130;
            if (r >= actual.InnerRadiusMm && r <= actual.OuterRadiusMm && r is < 38 or > 41)
                v = Math.Sin(Math.Atan2(d.Y, d.X) - Phase(r)) >= 0 ? (byte)195 : (byte)65;
            int at = (y * 1000 + x) * 3;
            pixels[at] = r is >= 7.5 and <= 60 ? (byte)120 : (byte)35;
            pixels[at + 1] = r is >= 7.5 and <= 60 ? v : (byte)35;
            pixels[at + 2] = r is >= 7.5 and <= 60 ? (byte)(130 + (v - 130) * .6) : (byte)35;
        }
        var image = new RgbImage(1000, 1000, pixels); var png = PhotoCodec.Encode(image);
        var photo = new ImportedPhoto(png, png, new(PhotoCodec.Hash(png), PhotoCodec.Hash(png), image.PixelsSha256, 1000, 1000, 1000, 1000, 1), image);
        ImmutableArray<PixelPoint> Circle(double r) => Enumerable.Range(0, 32).Select(i => h.DiscToSource(new(r * Math.Cos(i * Math.PI / 16), r * Math.Sin(i * Math.PI / 16)))).ToImmutableArray();
        var trace = Enumerable.Range(0, 28).Select(i => { double r = 29 + 4d * i / 27, a = Phase(r); return h.DiscToSource(new(r * Math.Cos(a), r * Math.Sin(a))); }).ToImmutableArray();
        var session = PhotoSession.Create(snapshot.Id, snapshot.ContentSha256, photo.Metadata).WithBoundaries(Circle(60), Circle(7.5)).Rectify().Confirm()
            .WithTraces([new("line-1", trace)]);
        return new(bundle, photo, session);
    }
    private static PhotoTask CreatePhoto(bool withTraces)
    {
        var p = new GeometryParameters(24, 58, 2295104, 133.3, 0, 0, 0, SpiralDirection.Cw, GeometryParameters.Convention, 0);
        var snapshot = GenerationSnapshot.CreateSynthetic("synthetic-photo-demo", new("DVD+R", 120, 15, 1, null), p,
            new("synthetic-cross-demo", "radial-cross", withTraces ? 1 : .3, "analytic-disc-xy-v1"));
        var bundle = new SnapshotBundle(new() { ["generation.json"] = JsonSerializer.SerializeToUtf8Bytes(snapshot, RecordJson.Options) });
        var transform = new DiscPhotoTransform([4, .45, 320, .25, 3.4, 260, .001, -.0008, 1]);
        var actual = p with { InnerRadiusMm = 24.0002, OuterRadiusMm = 58.0004 };
        var model = new ForwardModel(p, actual);
        byte[] pixels = new byte[640 * 520 * 3];
        for (int y = 0; y < 520; y++)
        for (int x = 0; x < 640; x++)
        {
            var disc = transform.SourceToDisc(new(x, y));
            double r = Math.Sqrt(disc.X * disc.X + disc.Y * disc.Y);
            byte value = 35;
            if (r is >= 7.5 and <= 60)
            {
                value = 210;
                if (r >= actual.InnerRadiusMm && r <= actual.OuterRadiusMm)
                {
                    var point = withTraces ? model.AtRadius(r) : null;
                    double phase = point is not null ? point.PhaseRadians + .4 : .0015 * (r - 24) * (r - 24);
                    double alpha = Math.Atan2(disc.Y, disc.X) - phase;
                    value = snapshot.Pattern.IsWhite(point?.SourceRadiusMm ?? r, alpha) ? (byte)195 : (byte)65;
                }
            }
            int i = (y * 640 + x) * 3;
            pixels[i] = value; pixels[i + 1] = value; pixels[i + 2] = value;
        }
        var image = new RgbImage(640, 520, pixels);
        var png = PhotoCodec.Encode(image);
        var photo = new ImportedPhoto(png, png, new(PhotoCodec.Hash(png), PhotoCodec.Hash(png), image.PixelsSha256, 640, 520, 640, 520, 1), image);
        ImmutableArray<PixelPoint> Circle(double radius) => Enumerable.Range(0, 16).Select(i =>
            transform.DiscToSource(new(radius * Math.Cos(i * Math.PI / 8), radius * Math.Sin(i * Math.PI / 8)))).ToImmutableArray();
        var session = PhotoSession.Create(snapshot.Id, snapshot.ContentSha256, photo.Metadata).WithBoundaries(Circle(60), Circle(7.5));
        if (withTraces)
        {
            ImmutableArray<PixelPoint> Trace(double offset, int count) => Enumerable.Range(0, count).Select(i =>
            {
                double r = 25 + 32d * i / (count - 1), a = model.AtRadius(r).PhaseRadians + .4 + offset;
                return transform.DiscToSource(new(r * Math.Cos(a), r * Math.Sin(a)));
            }).ToImmutableArray();
            session = session.Rectify().Confirm().WithTraces([new("line-1", Trace(0, 64)), new("validation-180", Trace(Math.PI, 48))]);
        }
        return new(bundle, photo, session);
    }
}

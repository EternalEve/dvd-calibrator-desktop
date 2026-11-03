using System.Collections.Immutable;
using DvdCalibrator.Core;
using System.Text.Json;

namespace DvdCalibrator.Imaging.Windows;

public static class SyntheticPhoto
{
    public static PhotoTask Create()
    {
        var p = new GeometryParameters(24, 58, 2295104, 133.3, 0, 0, 0, SpiralDirection.Cw, GeometryParameters.Convention, 0);
        var snapshot = GenerationSnapshot.CreateSynthetic("synthetic-photo-demo", new("DVD+R", 120, 15, 1, null), p,
            new("half-demo", "half-disc", .3, "analytic-disc-xy-v1"));
        var bundle = new SnapshotBundle(new() { ["generation.json"] = JsonSerializer.SerializeToUtf8Bytes(snapshot, RecordJson.Options) });
        var transform = new DiscPhotoTransform([4, .45, 320, .25, 3.4, 260, .001, -.0008, 1]);
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
                if (r is >= 24 and <= 58)
                {
                    double alpha = Math.Atan2(disc.Y, disc.X) - .0015 * (r - 24) * (r - 24);
                    value = Math.Abs(Math.Sin(alpha * 2)) < .04 || Math.Abs(r % 5) < .16 ? (byte)65 : (byte)195;
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
        return new(bundle, photo, session);
    }
}

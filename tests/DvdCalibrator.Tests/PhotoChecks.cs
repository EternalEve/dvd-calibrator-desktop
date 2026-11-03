using System.Collections.Immutable;
using System.Text.Json;
using DvdCalibrator.Core;

internal static class PhotoChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double x, double y, double tol) => Check(Math.Abs(x - y) <= tol, $"{x:G17} != {y:G17}");
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException or OperationCanceledException) { return; }
        throw new Exception("Invalid input accepted.");
    }
    private static PixelPoint Project(double x, double y)
    {
        double d = 1 + .0014 * x - .001 * y;
        return new((5.3 * x + .8 * y + 500) / d, (.3 * x + 4.4 * y + 400) / d);
    }
    private static ImmutableArray<PixelPoint> Circle(double radius) => Enumerable.Range(0, 24)
        .Select(i => Project(radius * Math.Cos(i * Math.PI / 12), radius * Math.Sin(i * Math.PI / 12))).ToImmutableArray();
    private static PhotoSession Session() => PhotoSession.Create("g0", new string('a', 64),
        new(new string('b', 64), new string('c', 64), new string('d', 64), 1000, 800, 1000, 800, 1)).WithBoundaries(Circle(60), Circle(7.5));

    public static void Register(Action<string, Action> test)
    {
        test("joint perspective recovery with independent circle projection", () =>
        {
            var s = Session().Rectify(); var h = s.GetTransform();
            Check(s.Rectification!.OuterRmsPixels < 1e-5 && s.Rectification.HoleRmsPixels < 1e-5, "perspective fit residual");
            foreach (var p in new[] { (0d, 0d), (14d, 26d), (-31d, -15d), (51d, 11d) })
            {
                var source = Project(p.Item1, p.Item2); var disc = h.SourceToDisc(source);
                Near(Math.Sqrt(disc.X * disc.X + disc.Y * disc.Y), Math.Sqrt(p.Item1 * p.Item1 + p.Item2 * p.Item2), 2e-5);
                var back = h.DiscToSource(disc); Near(back.X, source.X, 1e-8); Near(back.Y, source.Y, 1e-8);
            }
            var a = h.SourceToDisc(Project(31, -9)); var b = h.SourceToDisc(Project(-18, 14));
            Near(Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)), Math.Sqrt(49 * 49 + 23 * 23), 2e-5);
        });
        test("disc y-up adapter and projective horizon rejection", () =>
        {
            var h = new DiscPhotoTransform([5, 0, 400, 0, 5, 400, 0, 0, 1]);
            Near(h.SourceToDisc(new(400, 350)).Y, 10, 1e-10);
            Near(h.DiscToSource(new(0, 10)).Y, 350, 1e-10);
            Reject(() => new DiscPhotoTransform([1, 0, 0, 0, 1, 0, .1, 0, 1]));
            Reject(() => new DiscPhotoTransform([0, 0, 0, 0, 0, 0, 0, 0, 0]));
        });
        test("subpixel contour noise yields finite jointly consistent recovery", () =>
        {
            var s = Session();
            ImmutableArray<PixelPoint> Perturb(ImmutableArray<PixelPoint> points) => points.Select((p, i) =>
                new PixelPoint(p.X + .25 * Math.Sin(i * 2.4), p.Y + .25 * Math.Cos(i * 1.7))).ToImmutableArray();
            var result = s.WithBoundaries(Perturb(s.OuterBoundary), Perturb(s.HoleBoundary)).Rectify();
            Check(result.Rectification!.OuterRmsPixels < 1 && result.Rectification.HoleRmsPixels < 1, "noise fit residual");
            var recovered = result.GetTransform().SourceToDisc(Project(35, 0));
            Near(Math.Sqrt(recovered.X * recovered.X + recovered.Y * recovered.Y), 35, .3);
        });
        test("bad contours / partial arcs / cancellation cannot produce rectification", () =>
        {
            var s = Session();
            Reject(() => s.WithBoundaries(s.OuterBoundary[..4], s.HoleBoundary).Rectify());
            Reject(() => s.WithBoundaries(Enumerable.Range(0, 12).Select(i => new PixelPoint(100 + i * 10, 100)).ToImmutableArray(), s.HoleBoundary).Rectify());
            Reject(() => s.WithBoundaries(s.OuterBoundary, Circle(20)).Rectify());
            Reject(() => s.WithBoundaries(s.OuterBoundary[..7], s.HoleBoundary[..7]).Rectify());
            using var c = new CancellationTokenSource(); c.Cancel(); Reject(() => s.Rectify(c.Token));
        });
        test("boundary edits invalidate transform and confirmation but retain source traces", () =>
        {
            var s = Session().Rectify().Confirm().WithTraces([new("line-1", [new(500, 420)])]);
            s.Validate(); Check(s.IsConfirmed, "confirmation missing");
            var changed = s.WithBoundaries(s.OuterBoundary.SetItem(0, new(700, 400)), s.HoleBoundary);
            changed.Validate(); Check(changed.Rectification is null && !changed.IsConfirmed, "stale rectification kept");
            Check(changed.Traces[0].Points[0] == s.Traces[0].Points[0], "source annotations were rewritten");
            Reject(() => (s with { OuterBoundary = changed.OuterBoundary }).Validate());
            Reject(() => (s with { ConfirmedRectificationSha256 = new string('e', 64) }).Validate());
            var restored = RecordJson.Parse<PhotoSession>(JsonSerializer.Serialize(s, RecordJson.Options));
            restored.Validate(); Check(restored.IsConfirmed, "confirmation not restored");
        });
        test("all eight EXIF orientation transforms have independent expected pixels", () =>
        {
            byte[] rgb = Enumerable.Range(1, 6).SelectMany(i => new byte[] { (byte)i, 0, 0 }).ToArray();
            var image = new RgbImage(3, 2, rgb);
            byte[][] expected = [[1, 2, 3, 4, 5, 6], [3, 2, 1, 6, 5, 4], [6, 5, 4, 3, 2, 1], [4, 5, 6, 1, 2, 3],
                [1, 4, 2, 5, 3, 6], [4, 1, 5, 2, 6, 3], [6, 3, 5, 2, 4, 1], [3, 6, 2, 5, 1, 4]];
            for (int i = 1; i <= 8; i++)
            {
                var result = image.Orient(i);
                Check(result.Width == (i >= 5 ? 2 : 3) && result.Height == (i >= 5 ? 3 : 2), "orientation dimensions");
                Check(Enumerable.Range(0, 6).Select(p => result.Pixels[p * 3]).SequenceEqual(expected[i - 1]), $"EXIF {i} pixel order");
            }
            Reject(() => image.Orient(0));
        });
        test("inverse raster sampling, polar mapping and source-pixel masks", () =>
        {
            byte[] bytes = new byte[64 * 64 * 3];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) { bytes[(y * 64 + x) * 3] = (byte)x; bytes[(y * 64 + x) * 3 + 1] = (byte)y; }
            var image = new RgbImage(64, 64, bytes);
            var s = PhotoSession.Create("g", new string('a', 64), new(new string('b', 64), new string('c', 64), image.PixelsSha256, 64, 64, 64, 64, 1));
            ImmutableArray<PixelPoint> CirclePixels(double r) => Enumerable.Range(0, 16).Select(i => new PixelPoint(32 + r * .5 * Math.Cos(i * Math.PI / 8), 32 + r * .5 * Math.Sin(i * Math.PI / 8))).ToImmutableArray();
            s = s.WithBoundaries(CirclePixels(60), CirclePixels(7.5)).Rectify();
            var polar = PhotoRaster.Render(image, s, 64, 32, true, 24, 58);
            Near(polar.Pixels[0], 44, 1); Near(polar.Pixels[1], 32, 1);
            // At pi/2 in y-up, the source is above the centre.
            Near(polar.Pixels[16 * 3], 32, 1); Near(polar.Pixels[16 * 3 + 1], 20, 1);
            s = s.WithMasks([new([new(40, 28), new(48, 28), new(48, 36), new(40, 36)])]);
            var masked = PhotoRaster.Render(image, s, 64, 32, true, 24, 58);
            Check(masked.Pixels[0] == 210 && masked.Pixels[1] == 214, "source mask not applied");
            Reject(() => PhotoRaster.Render(image.Orient(3), s, 64, 32, false));
            Near(image.Sample(10.5, 20.5)!.Value.R, 10, 1);
            Check(image.Sample(-.1, 0) is null, "out of image sample clamped");
        });
    }
}

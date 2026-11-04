using System.IO;
using System.Windows.Media.Imaging;
using DvdCalibrator.Core;
using DvdCalibrator.Imaging.Windows;

internal static class PhotoOrientationChecks
{
    public static void Register(Action<string, Action> test, string output)
    {
        var image = new RgbImage(32, 16, Enumerable.Range(0, 32 * 16 * 3).Select(i => (byte)(i % 239)).ToArray());
        byte[] Encode(ushort? orientation)
        {
            var metadata = new BitmapMetadata("jpg");
            if (orientation is { } value) metadata.SetQuery("/app1/ifd/{ushort=274}", value);
            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(PhotoCodec.Bitmap(image), null, metadata, null));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }

        test("JPEG absent or invalid EXIF orientation imports stored pixels and preserves original bytes", () =>
        {
            foreach (ushort? value in new ushort?[] { null, 0, 9, ushort.MaxValue })
            {
                byte[] bytes = Encode(value);
                var imported = PhotoCodec.ImportBytes(bytes);
                if (imported.Metadata.AppliedExifOrientation != 1 || imported.Image.Width != 32 || imported.Image.Height != 16
                    || !imported.OriginalBytes.SequenceEqual(bytes)) throw new Exception("Invalid orientation changed photo geometry or source bytes.");
                // Compare to the same JPEG decoded with its metadata removed, avoiding JPEG loss as a confounder.
                using var stream = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(decoder.Frames[0], null, null, null));
                using var clean = new MemoryStream(); png.Save(clean);
                if (PhotoCodec.ImportBytes(clean.ToArray()).Image.PixelsSha256 != imported.Image.PixelsSha256)
                    throw new Exception("Malformed EXIF rotated or mirrored stored pixels.");
                string path = Path.Combine(output, "invalid-exif-" + (value?.ToString() ?? "absent") + ".dvdphoto");
                new PhotoTask(SyntheticPhoto.Create().Snapshot, imported).SaveNew(path);
                if (PhotoTask.Load(path).Photo.Metadata.AppliedExifOrientation != 1) throw new Exception("Fallback orientation did not restore.");
            }
        });
        test("all eight valid JPEG EXIF orientations still apply exactly once", () =>
        {
            var normal = PhotoCodec.ImportBytes(Encode(1));
            for (ushort orientation = 1; orientation <= 8; orientation++)
            {
                var imported = PhotoCodec.ImportBytes(Encode(orientation));
                if (imported.Metadata.AppliedExifOrientation != orientation
                    || imported.Image.PixelsSha256 != normal.Image.Orient(orientation).PixelsSha256)
                    throw new Exception("Valid EXIF orientation mapping changed.");
                var again = PhotoCodec.ImportBytes(imported.OrientedPng);
                if (again.Metadata.AppliedExifOrientation != 1 || again.Image.PixelsSha256 != imported.Image.PixelsSha256)
                    throw new Exception("EXIF orientation applied twice.");
            }
        });
    }
}

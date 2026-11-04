using System.IO;
using System.Text.Json;
using DvdCalibrator.Core;

namespace DvdCalibrator.Imaging.Windows;

public sealed partial class SnapshotBundle
{
    public static byte[] HalfDiscPng()
    {
        const int size = 2400;
        byte[] pixels = new byte[size * size * 3];
        Array.Fill(pixels, (byte)255, 0, pixels.Length / 2);
        return PhotoCodec.Encode(new(size, size, pixels));
    }

    internal static byte[] ReadLimited(string path, int maximum)
    {
        if (new FileInfo(path).Length > maximum) throw new ArgumentException("提交文件超过大小限制。");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length > maximum) throw new ArgumentException("提交文件超过大小限制。");
        return bytes;
    }

    public static SnapshotBundle ImportIsoRecord(string recordPath, string sourceImagePath, DiscSpec disc, bool halfDisc = true)
    {
        byte[] recordBytes = ReadLimited(recordPath, RecordJson.MaxBytes), imageBytes = ReadLimited(sourceImagePath, PhotoCodec.MaxFileBytes);
        var record = DiscImageRecord.ReadBytes(recordBytes, allowBinaryTone: halfDisc);
        var declarations = new GenerationDeclarations(disc, record.Generated.TotalSectors, "unknown", "", "manual-import");
        string extension = Path.GetExtension(sourceImagePath).ToLowerInvariant();
        if (extension is not (".png" or ".bmp" or ".jpg" or ".jpeg" or ".gif")) throw new ArgumentException("生成源图需要 PNG、BMP、JPEG 或 GIF。");
        return FreezeIsoRecord(recordBytes, record, declarations, imageBytes, extension, halfDisc);
    }

    public static SnapshotBundle ImportHalfDiscRecord(byte[] recordBytes)
    {
        var record = DiscImageRecord.ReadBytes(recordBytes, allowBinaryTone: true);
        var declarations = new GenerationDeclarations(DiscSpec.UnspecifiedDvd, 0, "unknown", "", GenerationDeclarations.BuiltInHalfDisc);
        return FreezeIsoRecord(recordBytes, record, declarations, HalfDiscPng(), ".png", halfDisc: true);
    }

    private static SnapshotBundle FreezeIsoRecord(byte[] recordBytes, ImportedGenerationRecord record,
        GenerationDeclarations declarations, byte[] imageBytes, string extension, bool halfDisc)
    {
        string asset = "assets/source-image" + extension;
        var snapshot = new ExternalGenerationSnapshot(1, "external-generation-snapshot", "frozen", Guid.NewGuid().ToString("N"),
            record, declarations, asset, PhotoCodec.Hash(imageBytes), "source.json", "", halfDisc);
        snapshot = snapshot with { ContentSha256 = RecordJson.Hash(snapshot) };
        return new(new(StringComparer.Ordinal)
        {
            ["generation.json"] = JsonSerializer.SerializeToUtf8Bytes(snapshot, RecordJson.Options),
            ["source.json"] = recordBytes, [asset] = imageBytes
        });
    }

    public static SnapshotBundle ImportCalibrationRecord(string path, DiscSpec disc)
    {
        byte[] bytes = ReadLimited(path, RecordJson.MaxBytes);
        var snapshot = CalibrationGeneration.Import(bytes, disc);
        return new(new(StringComparer.Ordinal)
        {
            ["generation.json"] = JsonSerializer.SerializeToUtf8Bytes(snapshot, RecordJson.Options), ["source.json"] = bytes
        });
    }
}

internal static class HalfDiscSource
{
    // Only certify a centred straight split. Its axis and polarity become a free photo rotation.
    // Reject transparency, shifted boundaries, extra marks and arbitrary black/white pictures.
    public static void Validate(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        int size = frame.PixelWidth;
        if (size != frame.PixelHeight || size < 64 || size > 8192 || size % 2 != 0 || decoder.Frames.Count != 1)
            throw new ArgumentException("粗调源图须为偶数边长、64–8192 像素的正方形黑白半盘图。");
        if (frame.Metadata is System.Windows.Media.Imaging.BitmapMetadata metadata)
            foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                if (metadata.ContainsQuery(query) && Convert.ToInt32(metadata.GetQuery(query)) != 1)
                    throw new ArgumentException("生成源图不能带有 EXIF 旋转。");
        var rgba = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[size * 4];
        bool horizontal = true, vertical = true; byte first = 0;
        for (int y = 0; y < size; y++)
        {
            rgba.CopyPixels(new System.Windows.Int32Rect(0, y, size, 1), pixels, size * 4, 0);
            if (y == 0) first = pixels[0];
            for (int x = 0; x < size; x++)
            {
                int i = x * 4; byte v = pixels[i];
                if (pixels[i + 3] != 255 || pixels[i + 1] != v || pixels[i + 2] != v || (first != 0 && first != 255))
                    throw new ArgumentException("粗调源图必须是不透明的黑白半盘图；请提交生成时实际使用的源图。");
                // Permit only the two-pixel interpolation fringe made by GUI export resizing.
                horizontal &= Math.Abs(y + .5 - size / 2d) <= 2 || v == (y < size / 2 ? first : 255 - first);
                vertical &= Math.Abs(x + .5 - size / 2d) <= 2 || v == (x < size / 2 ? first : 255 - first);
            }
        }
        if (!horizontal && !vertical) throw new ArgumentException("源图不是从中心平分的黑白半盘图，不能进行半盘粗调识别。");
    }
}

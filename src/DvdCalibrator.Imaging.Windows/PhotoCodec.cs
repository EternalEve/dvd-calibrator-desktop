using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DvdCalibrator.Core;

namespace DvdCalibrator.Imaging.Windows;

public sealed record ImportedPhoto(byte[] OriginalBytes, byte[] OrientedPng, PhotoMetadata Metadata, RgbImage Image);

public static class PhotoCodec
{
    public const int MaxFileBytes = 100_000_000;
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static ImportedPhoto Import(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new ArgumentException("照片文件超过 100 MB。");
        byte[] bytes = File.ReadAllBytes(path);
        return ImportBytes(bytes);
    }

    public static ImportedPhoto ImportBytes(byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new ArgumentException("照片文件超过 100 MB。");
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder or BmpBitmapDecoder or GifBitmapDecoder or TiffBitmapDecoder))
            throw new ArgumentException("请使用 PNG、JPEG、BMP、GIF 或 TIFF 照片。");
        var frame = decoder.Frames[0];
        int orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata)
        {
            foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is { } value)
                { orientation = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); break; }
            }
        }
        var original = DecodeFrame(frame);
        var oriented = original.Orient(orientation);
        byte[] png = Encode(oriented);
        return new(bytes, png, new(Hash(bytes), Hash(png), oriented.PixelsSha256,
            original.Width, original.Height, oriented.Width, oriented.Height, orientation), oriented);
    }

    public static RgbImage DecodePng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return DecodeFrame(decoder.Frames[0]);
    }

    private static RgbImage DecodeFrame(BitmapSource source)
    {
        if (source.PixelWidth is < 1 or > 30000 || source.PixelHeight is < 1 or > 30000
            || (long)source.PixelWidth * source.PixelHeight > 40_000_000)
            throw new ArgumentException("照片超过 4000 万像素或边长限制，请选择原始尺寸较小的照片。");
        var rgb = new FormatConvertedBitmap(source, PixelFormats.Rgb24, null, 0);
        int stride = checked(rgb.PixelWidth * 3);
        byte[] pixels = new byte[checked(stride * rgb.PixelHeight)];
        rgb.CopyPixels(pixels, stride, 0);
        return new(rgb.PixelWidth, rgb.PixelHeight, pixels);
    }

    public static BitmapSource Bitmap(RgbImage image)
    {
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Rgb24, null,
            image.Pixels.ToArray(), image.Width * 3);
        source.Freeze();
        return source;
    }

    public static byte[] Encode(RgbImage image)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Bitmap(image)));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
}

using System.Collections.Immutable;
using System.Security.Cryptography;

namespace DvdCalibrator.Core;

public sealed class RgbImage
{
    private readonly byte[] pixels;
    public int Width { get; }
    public int Height { get; }
    public ReadOnlySpan<byte> Pixels => pixels;
    public string PixelsSha256 => Convert.ToHexStringLower(SHA256.HashData(pixels));
    public RgbImage(int width, int height, byte[] rgb)
    {
        if (width is < 1 or > 30000 || height is < 1 or > 30000 || (long)width * height > 40_000_000
            || rgb.Length != (long)width * height * 3) throw new ArgumentException("图像尺寸或 RGB 缓冲区无效。");
        Width = width; Height = height; pixels = (byte[])rgb.Clone();
    }

    public RgbImage Orient(int orientation)
    {
        if (orientation is < 1 or > 8) throw new ArgumentException("不支持的 EXIF 方向。");
        bool swap = orientation >= 5;
        int w = swap ? Height : Width, h = swap ? Width : Height;
        byte[] output = new byte[pixels.Length];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            (int u, int v) = orientation switch
            {
                1 => (x, y), 2 => (Width - 1 - x, y), 3 => (Width - 1 - x, Height - 1 - y),
                4 => (x, Height - 1 - y), 5 => (y, x), 6 => (Height - 1 - y, x),
                7 => (Height - 1 - y, Width - 1 - x), 8 => (y, Width - 1 - x),
                _ => throw new ArgumentException("EXIF 方向无效。")
            };
            Array.Copy(pixels, (y * Width + x) * 3, output, (v * w + u) * 3, 3);
        }
        return new(w, h, output);
    }

    public (byte R, byte G, byte B)? Sample(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > Width - 1 || y > Height - 1) return null;
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        double fx = x - x0, fy = y - y0;
        byte Channel(int c) => (byte)Math.Clamp(Math.Round(
            (1 - fy) * ((1 - fx) * pixels[(y0 * Width + x0) * 3 + c] + fx * pixels[(y0 * Width + x1) * 3 + c])
            + fy * ((1 - fx) * pixels[(y1 * Width + x0) * 3 + c] + fx * pixels[(y1 * Width + x1) * 3 + c])), 0, 255);
        return (Channel(0), Channel(1), Channel(2));
    }
}

public static class PhotoRaster
{
    public static bool IsMasked(double x, double y, ImmutableArray<MaskPolygon> masks)
    {
        foreach (var mask in masks)
        {
            bool inside = false;
            var v = mask.Vertices;
            for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
                if ((v[i].Y > y) != (v[j].Y > y) && x < (v[j].X - v[i].X) * (y - v[i].Y) / (v[j].Y - v[i].Y) + v[i].X)
                    inside = !inside;
            if (inside) return true;
        }
        return false;
    }

    // Direct inverse sampling of oriented original pixels. No intermediate rectified image is reused.
    public static RgbImage Render(RgbImage original, PhotoSession session, int width, int height,
        bool polar, double innerRadius = 24, double outerRadius = 58, CancellationToken token = default)
    {
        session.Validate();
        if (original.Width != session.Photo.OrientedWidth || original.Height != session.Photo.OrientedHeight
            || original.PixelsSha256 != session.Photo.OrientedPixelsSha256)
            throw new ArgumentException("采样图像与会话原图不一致。");
        if (width is < 32 or > 2048 || height is < 32 or > 2048
            || !double.IsFinite(innerRadius) || !double.IsFinite(outerRadius) || innerRadius < 7.5 || outerRadius > 60 || innerRadius >= outerRadius)
            throw new ArgumentException("输出尺寸或极坐标范围无效。");
        var transform = session.GetTransform();
        byte[] pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                double r = polar ? innerRadius + (outerRadius - innerRadius) * y / (height - 1) : 0;
                double a = 2 * Math.PI * x / width;
                double xm = polar ? r * Math.Cos(a) : (x + .5) * 120 / width - 60;
                double ym = polar ? r * Math.Sin(a) : 60 - (y + .5) * 120 / height;
                var color = ((byte)210, (byte)214, (byte)220);
                if (polar || (xm * xm + ym * ym >= 7.5 * 7.5 && xm * xm + ym * ym <= 3600))
                {
                    var p = transform.DiscToSource(new(xm, ym));
                    if (!IsMasked(p.X, p.Y, session.Masks)) color = original.Sample(p.X, p.Y) ?? color;
                }
                int offset = (y * width + x) * 3;
                pixels[offset] = color.Item1; pixels[offset + 1] = color.Item2; pixels[offset + 2] = color.Item3;
            }
        }
        return new(width, height, pixels);
    }
}

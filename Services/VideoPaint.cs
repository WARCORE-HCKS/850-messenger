using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SIPSorceryMedia.Abstractions;

namespace Area850.Services;

internal static class VideoPaint
{
    public static void ToImage(System.Windows.Controls.Image target, byte[] sample, int width, int height, VideoPixelFormatsEnum fmt)
    {
        if (target is null || sample is null || width <= 0 || height <= 0) return;
        byte[] bgr = sample;
        int stride = width * 3;
        PixelFormat pf = PixelFormats.Bgr24;
        try
        {
            switch (fmt)
            {
                case VideoPixelFormatsEnum.Bgr:
                    stride = Math.Max(width * 3, sample.Length / Math.Max(height, 1));
                    pf = PixelFormats.Bgr24;
                    break;
                case VideoPixelFormatsEnum.Bgra:
                    pf = PixelFormats.Bgra32;
                    stride = Math.Max(width * 4, sample.Length / Math.Max(height, 1));
                    break;
                case VideoPixelFormatsEnum.Rgb:
                    pf = PixelFormats.Rgb24;
                    stride = Math.Max(width * 3, sample.Length / Math.Max(height, 1));
                    break;
                default:
                    if (sample.Length < width * height * 3) return;
                    stride = width * 3;
                    pf = PixelFormats.Bgr24;
                    break;
            }
            var copy = new byte[sample.Length];
            Buffer.BlockCopy(sample, 0, copy, 0, sample.Length);
            var bmp = new WriteableBitmap(width, height, 96, 96, pf, null);
            var aligned = (stride + 3) & ~3;
            if (aligned != stride && pf == PixelFormats.Bgr24)
            {
                var padded = new byte[aligned * height];
                for (int y = 0; y < height; y++)
                    Buffer.BlockCopy(copy, y * stride, padded, y * aligned, Math.Min(stride, width * 3));
                copy = padded;
                stride = aligned;
            }
            bmp.WritePixels(new Int32Rect(0, 0, width, height), copy, stride, 0);
            bmp.Freeze();
            target.Source = bmp;
        }
        catch { }
    }
}

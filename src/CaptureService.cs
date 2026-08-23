using System.Drawing.Imaging;

namespace RmsLink;

public static class CaptureService
{
    /// <summary>화면의 지정 영역을 캡처 (물리 픽셀 좌표, 다중 모니터 지원)</summary>
    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>변화 감지용 고속 해시 (FNV-1a, 4픽셀 간격 샘플링)</summary>
    public static ulong QuickHash(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            ulong hash = 14695981039346656037UL;
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int y = 0; y < data.Height; y += 2)
                {
                    byte* row = basePtr + (long)y * data.Stride;
                    for (int x = 0; x < data.Width * 4; x += 16)
                    {
                        hash ^= row[x];
                        hash *= 1099511628211UL;
                    }
                }
            }
            return hash;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>OCR 정확도를 위한 업스케일 (작은 폰트 대응)</summary>
    public static Bitmap Upscale(Bitmap src, int scale)
    {
        if (scale <= 1) return (Bitmap)src.Clone();
        var dst = new Bitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height));
        return dst;
    }
}

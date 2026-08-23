using System.Drawing.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace RmsLink;

public sealed class OcrService
{
    private readonly OcrEngine _engine;
    public string LanguageTag { get; }

    private OcrService(OcrEngine engine, string tag)
    {
        _engine = engine;
        LanguageTag = tag;
    }

    /// <summary>한국어 우선으로 Windows 내장 OCR 엔진 생성. 실패 시 null.</summary>
    public static OcrService Create()
    {
        OcrEngine e = null;
        string tag = "";
        try
        {
            e = OcrEngine.TryCreateFromLanguage(new Language("ko"));
            if (e != null) tag = "ko";
        }
        catch { }

        if (e == null)
        {
            try
            {
                e = OcrEngine.TryCreateFromUserProfileLanguages();
                if (e != null) tag = e.RecognizerLanguage?.LanguageTag ?? "profile";
            }
            catch { }
        }

        if (e == null)
        {
            try
            {
                e = OcrEngine.TryCreateFromLanguage(new Language("en"));
                if (e != null) tag = "en";
            }
            catch { }
        }

        return e == null ? null : new OcrService(e, tag);
    }

    /// <summary>진단용: 이 PC에 설치된 OCR 인식 가능 언어 목록</summary>
    public static List<string> AvailableLanguages()
    {
        var list = new List<string>();
        try
        {
            foreach (var l in OcrEngine.AvailableRecognizerLanguages)
                list.Add(l.LanguageTag);
        }
        catch { }
        return list;
    }

    /// <summary>비트맵을 OCR하여 화면 위->아래 순서의 텍스트 줄 목록 반환.</summary>
    public async Task<List<string>> ReadLinesAsync(Bitmap bmp, int scale)
    {
        uint maxDim = OcrEngine.MaxImageDimension;
        while (scale > 1 && (long)Math.Max(bmp.Width, bmp.Height) * scale > maxDim)
            scale--;

        using var scaled = CaptureService.Upscale(bmp, scale);

        // GDI Bitmap -> PNG 바이트 -> WinRT SoftwareBitmap
        byte[] pngBytes;
        using (var ms = new MemoryStream())
        {
            scaled.Save(ms, ImageFormat.Png);
            pngBytes = ms.ToArray();
        }

        SoftwareBitmap soft;
        using (var ras = new InMemoryRandomAccessStream())
        {
            using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            ras.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(ras);
            soft = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        try
        {
            var result = await _engine.RecognizeAsync(soft);

            var lines = new List<(double Top, string Text)>();
            foreach (var line in result.Lines)
            {
                double top = double.MaxValue;
                var words = new List<string>();
                foreach (var w in line.Words)
                {
                    words.Add(w.Text);
                    if (w.BoundingRect.Top < top) top = w.BoundingRect.Top;
                }
                if (words.Count > 0)
                    lines.Add((top, string.Join(" ", words)));
            }
            lines.Sort((a, b) => a.Top.CompareTo(b.Top));
            return lines.Select(l => l.Text).ToList();
        }
        finally
        {
            soft.Dispose();
        }
    }
}

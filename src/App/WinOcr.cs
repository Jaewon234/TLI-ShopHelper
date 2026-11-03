using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ShopHelper;

/// <summary>Windows 내장 OCR (한국어).</summary>
sealed class WinOcr : IOcr
{
    readonly OcrEngine engine;
    public int MaxDim { get; }

    public WinOcr()
    {
        engine = OcrEngine.TryCreateFromLanguage(new Language("ko"))
                 ?? throw new InvalidOperationException(
                     "Windows 한국어 OCR이 없습니다.\n\n관리자 PowerShell에서 아래 명령을 실행한 뒤 다시 켜 주세요:\n\n" +
                     "Add-WindowsCapability -Online -Name \"Language.OCR~~~ko-KR~0.0.1.0\"");
        MaxDim = (int)OcrEngine.MaxImageDimension;
    }

    public List<List<OcrWord>> Recognize(Frame img, Box src, double scale)
    {
        var buf = CryptographicBuffer.CreateFromByteArray(img.Px);
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(buf, BitmapPixelFormat.Bgra8, img.W, img.H, BitmapAlphaMode.Ignore);
        var res = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();
        var lines = new List<List<OcrWord>>();
        foreach (var line in res.Lines)
        {
            var ws = new List<OcrWord>();
            foreach (var w in line.Words)
                ws.Add(new OcrWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height));
            lines.Add(ws);
        }
        return lines;
    }
}

static class ImageFile
{
    public static Frame Load(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
        using var s = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult();
        var dec = BitmapDecoder.CreateAsync(s).AsTask().GetAwaiter().GetResult();
        using var sb = dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore).AsTask().GetAwaiter().GetResult();
        var buf = new Windows.Storage.Streams.Buffer((uint)(sb.PixelWidth * sb.PixelHeight * 4));
        sb.CopyToBuffer(buf);
        CryptographicBuffer.CopyToByteArray(buf, out byte[] px);
        return new Frame(px, sb.PixelWidth, sb.PixelHeight);
    }

    public static void SavePng(Frame f, string path)
    {
        using var ms = new InMemoryRandomAccessStream();
        var enc = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms).AsTask().GetAwaiter().GetResult();
        var px = (byte[])f.Px.Clone();
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)f.W, (uint)f.H, 96, 96, px);
        enc.FlushAsync().AsTask().GetAwaiter().GetResult();
        var reader = new DataReader(ms.GetInputStreamAt(0));
        reader.LoadAsync((uint)ms.Size).AsTask().GetAwaiter().GetResult();
        var bytes = new byte[ms.Size];
        reader.ReadBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }
}

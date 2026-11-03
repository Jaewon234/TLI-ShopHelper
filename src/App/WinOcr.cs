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
    readonly OcrEngine? engineEn;   // 숫자용 (한글을 모르니 숫자를 숫자로 읽음)
    public int MaxDim { get; }
    public bool HasDigits => engineEn != null;

    public WinOcr()
    {
        engine = OcrEngine.TryCreateFromLanguage(new Language("ko"))
                 ?? throw new InvalidOperationException(
                     "Windows 한국어 OCR이 없습니다.\n\n관리자 PowerShell에서 아래 명령을 실행한 뒤 다시 켜 주세요:\n\n" +
                     "Add-WindowsCapability -Online -Name \"Language.OCR~~~ko-KR~0.0.1.0\"");
        MaxDim = (int)OcrEngine.MaxImageDimension;
        try { engineEn = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromLanguage(new Language("en")); } catch { }
    }

    public List<List<OcrWord>>? RecognizeDigits(Frame img, Box src, double scale) =>
        engineEn == null ? null : Run(engineEn, img);

    public List<List<OcrWord>> Recognize(Frame img, Box src, double scale) => Run(engine, img);

    static List<List<OcrWord>> Run(OcrEngine engine, Frame img)
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

/// <summary>
/// OCR 고르기: Windows OCR 이 있으면 그걸(빠름), 없으면 프로그램에 넣은 OCR(PaddleOCR) 을 씀.
/// 숫자용 영어 OCR 도 Windows 에 없으면 내장 영어 모델을 씀. settings.json 의 ocr_engine 으로 고정 가능.
/// </summary>
sealed class OcrStack : IOcr
{
    readonly WinOcr? win;
    readonly Lazy<PaddleOcr> paddle = new(() => { var sw = System.Diagnostics.Stopwatch.StartNew(); var p = new PaddleOcr(); App.Log($"내장 OCR 준비됨 ({sw.ElapsedMilliseconds}ms)"); return p; });
    readonly bool builtinOnly;
    public string Describe { get; }

    public OcrStack(Config cfg)
    {
        builtinOnly = cfg.OcrEngine.Equals("builtin", StringComparison.OrdinalIgnoreCase);
        if (!builtinOnly)
            try { win = new WinOcr(); } catch (Exception e) { App.Log("Windows 한국어 OCR 없음 → 내장 OCR 사용: " + e.Message.Split('\n')[0]); }
        bool winOnly = cfg.OcrEngine.Equals("windows", StringComparison.OrdinalIgnoreCase);
        if (win == null && winOnly) throw new InvalidOperationException("Windows 한국어 OCR 이 없습니다. settings.json 의 ocr_engine 을 auto 로 바꾸면 내장 OCR 을 씁니다.");
        Describe = win == null ? "내장 OCR (한국어+영어)"
            : win.HasDigits ? "Windows OCR (한국어+영어)" : "Windows 한국어 OCR + 내장 영어 OCR (숫자)";
        if (win == null) _ = paddle.Value;   // 처음 상점을 열 때 기다리지 않게 미리 불러둠
    }

    public int MaxDim => win?.MaxDim ?? 6000;
    public List<List<OcrWord>> Recognize(Frame img, Box src, double scale) =>
        win != null ? win.Recognize(img, src, scale) : paddle.Value.Recognize(img, src, scale);
    public List<List<OcrWord>>? RecognizeDigits(Frame img, Box src, double scale) =>
        win is { HasDigits: true } ? win.RecognizeDigits(img, src, scale) : paddle.Value.RecognizeDigits(img, src, scale);
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

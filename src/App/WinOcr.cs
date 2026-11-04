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
        OcrResult res;
        lock (engine) res = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();   // 같은 엔진을 동시에 부르지 않게 (자동 읽기 동시 실행)
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
/// OCR 고르기: 어느 PC 에서나 같은 결과가 나오도록 프로그램에 넣은 OCR(PaddleOCR) 만 씀.
/// (Windows OCR 은 PC 마다 설치 여부와 인식률이 달라서 기본으로 쓰지 않음. settings.json 의 ocr_engine 을 "windows" 로 적을 때만 사용)
/// </summary>
sealed class OcrStack : IOcr
{
    readonly WinOcr? win;
    readonly Lazy<PaddleOcr> paddle = new(() => { var sw = System.Diagnostics.Stopwatch.StartNew(); var p = new PaddleOcr(); App.Log($"내장 OCR 준비됨 ({sw.ElapsedMilliseconds}ms)"); return p; });
    public string Describe { get; }

    public OcrStack(Config cfg)
    {
        if (cfg.OcrEngine.Equals("windows", StringComparison.OrdinalIgnoreCase))
            win = new WinOcr();   // 없으면 오류 (직접 고른 경우만)
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

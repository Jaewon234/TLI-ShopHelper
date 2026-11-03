using System.Runtime.InteropServices;

namespace ShopHelper;

/// <summary>
/// GDI+ (Windows 기본 그래픽) 로 반투명·둥근 모서리·부드러운 글자를 그림.
/// 오버레이는 32비트 알파 비트맵에, 시세 창은 일반 HDC 에 그림.
/// </summary>
sealed class Canvas : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct StartupInput { public int Version; public IntPtr Callback; public int NoThread, NoCodecs; }
    [StructLayout(LayoutKind.Sequential)] public struct RectF { public float X, Y, W, H; public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; } }

    const string G = "gdiplus.dll";
    [DllImport(G)] static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);
    [DllImport(G)] static extern int GdipCreateFromHDC(IntPtr hdc, out IntPtr g);
    [DllImport(G)] static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, IntPtr scan0, out IntPtr bmp);
    [DllImport(G)] static extern int GdipGetImageGraphicsContext(IntPtr img, out IntPtr g);
    [DllImport(G)] static extern int GdipDisposeImage(IntPtr img);
    [DllImport(G)] static extern int GdipDeleteGraphics(IntPtr g);
    [DllImport(G)] static extern int GdipSetSmoothingMode(IntPtr g, int mode);
    [DllImport(G)] static extern int GdipSetTextRenderingHint(IntPtr g, int hint);
    [DllImport(G)] static extern int GdipSetPixelOffsetMode(IntPtr g, int mode);
    [DllImport(G)] static extern int GdipGraphicsClear(IntPtr g, uint argb);
    [DllImport(G)] static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);
    [DllImport(G)] static extern int GdipDeleteBrush(IntPtr brush);
    [DllImport(G)] static extern int GdipCreatePen1(uint argb, float width, int unit, out IntPtr pen);
    [DllImport(G)] static extern int GdipDeletePen(IntPtr pen);
    [DllImport(G)] static extern int GdipCreatePath(int fill, out IntPtr path);
    [DllImport(G)] static extern int GdipDeletePath(IntPtr path);
    [DllImport(G)] static extern int GdipAddPathArc(IntPtr path, float x, float y, float w, float h, float start, float sweep);
    [DllImport(G)] static extern int GdipClosePathFigure(IntPtr path);
    [DllImport(G)] static extern int GdipFillPath(IntPtr g, IntPtr brush, IntPtr path);
    [DllImport(G)] static extern int GdipDrawPath(IntPtr g, IntPtr pen, IntPtr path);
    [DllImport(G)] static extern int GdipFillRectangle(IntPtr g, IntPtr brush, float x, float y, float w, float h);
    [DllImport(G)] static extern int GdipDrawLine(IntPtr g, IntPtr pen, float x1, float y1, float x2, float y2);
    [DllImport(G, CharSet = CharSet.Unicode)] static extern int GdipCreateFontFamilyFromName(string name, IntPtr coll, out IntPtr fam);
    [DllImport(G)] static extern int GdipDeleteFontFamily(IntPtr fam);
    [DllImport(G)] static extern int GdipCreateFont(IntPtr fam, float size, int style, int unit, out IntPtr font);
    [DllImport(G)] static extern int GdipDeleteFont(IntPtr font);
    [DllImport(G)] static extern int GdipStringFormatGetGenericTypographic(out IntPtr fmt);
    [DllImport(G)] static extern int GdipCloneStringFormat(IntPtr fmt, out IntPtr clone);
    [DllImport(G)] static extern int GdipSetStringFormatFlags(IntPtr fmt, int flags);
    [DllImport(G)] static extern int GdipDeleteStringFormat(IntPtr fmt);
    [DllImport(G, CharSet = CharSet.Unicode)] static extern int GdipDrawString(IntPtr g, string s, int len, IntPtr font, ref RectF rect, IntPtr fmt, IntPtr brush);
    [DllImport(G, CharSet = CharSet.Unicode)] static extern int GdipMeasureString(IntPtr g, string s, int len, IntPtr font, ref RectF layout, IntPtr fmt, out RectF bound, out int chars, out int lines);

    static IntPtr token;
    static IntPtr family, format;
    static readonly Dictionary<(float, bool), IntPtr> fonts = new();

    public static void Startup()
    {
        if (token != IntPtr.Zero) return;
        var si = new StartupInput { Version = 1 };
        GdiplusStartup(out token, ref si, IntPtr.Zero);
        if (GdipCreateFontFamilyFromName("Malgun Gothic", IntPtr.Zero, out family) != 0)
            GdipCreateFontFamilyFromName("Segoe UI", IntPtr.Zero, out family);
        GdipStringFormatGetGenericTypographic(out var generic);
        GdipCloneStringFormat(generic, out format);
        GdipSetStringFormatFlags(format, 0x800 | 0x1000); // MeasureTrailingSpaces | NoWrap
    }

    static IntPtr Font(float px, bool bold)
    {
        px = MathF.Round(Math.Clamp(px, 8, 80));
        if (!fonts.TryGetValue((px, bold), out var f))
        {
            GdipCreateFont(family, px, bold ? 1 : 0, 2 /*pixel*/, out f);
            fonts[(px, bold)] = f;
        }
        return f;
    }

    readonly IntPtr g, bitmap;

    Canvas(IntPtr g, IntPtr bitmap)
    {
        this.g = g; this.bitmap = bitmap;
        GdipSetSmoothingMode(g, 4);       // AntiAlias
        GdipSetPixelOffsetMode(g, 4);     // Half
        GdipSetTextRenderingHint(g, 4);   // AntiAlias (알파에서도 깔끔)
    }

    /// <summary>32비트 premultiplied ARGB 비트(DIB) 위에 그리기 (오버레이용).</summary>
    public static Canvas OnBits(IntPtr bits, int w, int h)
    {
        GdipCreateBitmapFromScan0(w, h, w * 4, 0xE200B /*PARGB*/, bits, out var bmp);
        GdipGetImageGraphicsContext(bmp, out var gr);
        return new Canvas(gr, bmp);
    }

    public static Canvas OnDc(IntPtr hdc)
    {
        GdipCreateFromHDC(hdc, out var gr);
        var c = new Canvas(gr, IntPtr.Zero);
        GdipSetTextRenderingHint(gr, 5);  // ClearType (불투명 바탕)
        return c;
    }

    public void Dispose()
    {
        GdipDeleteGraphics(g);
        if (bitmap != IntPtr.Zero) GdipDisposeImage(bitmap);
    }

    public static uint Argb(int a, int r, int g, int b) => (uint)((a << 24) | (r << 16) | (g << 8) | b);
    public static uint WithAlpha(uint argb, int a) => (argb & 0x00FFFFFF) | ((uint)a << 24);

    public void Clear(uint argb) => GdipGraphicsClear(g, argb);

    IntPtr RoundPath(float x, float y, float w, float h, float r)
    {
        GdipCreatePath(0, out var p);
        r = Math.Max(0.5f, Math.Min(r, Math.Min(w, h) / 2));
        float d = r * 2;
        GdipAddPathArc(p, x, y, d, d, 180, 90);
        GdipAddPathArc(p, x + w - d, y, d, d, 270, 90);
        GdipAddPathArc(p, x + w - d, y + h - d, d, d, 0, 90);
        GdipAddPathArc(p, x, y + h - d, d, d, 90, 90);
        GdipClosePathFigure(p);
        return p;
    }

    public void FillRound(double x, double y, double w, double h, double r, uint argb)
    {
        var p = RoundPath((float)x, (float)y, (float)w, (float)h, (float)r);
        GdipCreateSolidFill(argb, out var b);
        GdipFillPath(g, b, p);
        GdipDeleteBrush(b); GdipDeletePath(p);
    }

    public void StrokeRound(double x, double y, double w, double h, double r, uint argb, double width)
    {
        var p = RoundPath((float)x, (float)y, (float)w, (float)h, (float)r);
        GdipCreatePen1(argb, (float)width, 2, out var pen);
        GdipDrawPath(g, pen, p);
        GdipDeletePen(pen); GdipDeletePath(p);
    }

    public void FillRect(double x, double y, double w, double h, uint argb)
    {
        GdipCreateSolidFill(argb, out var b);
        GdipFillRectangle(g, b, (float)x, (float)y, (float)w, (float)h);
        GdipDeleteBrush(b);
    }

    public void Line(double x1, double y1, double x2, double y2, uint argb, double width = 1)
    {
        GdipCreatePen1(argb, (float)width, 2, out var pen);
        GdipDrawLine(g, pen, (float)x1, (float)y1, (float)x2, (float)y2);
        GdipDeletePen(pen);
    }

    /// <summary>빛나는 테두리: 바깥으로 갈수록 옅어지는 선을 여러 겹.</summary>
    public void Glow(double x, double y, double w, double h, double r, uint argb, int spread)
    {
        for (int i = spread; i >= 1; i--)
        {
            int a = (int)(70.0 * (spread - i + 1) / spread / spread * 2.2);
            StrokeRound(x - i, y - i, w + i * 2, h + i * 2, r + i, WithAlpha(argb, Math.Clamp(a, 4, 90)), 2);
        }
    }

    public (double W, double H) Measure(string text, double px, bool bold)
    {
        var layout = new RectF(0, 0, 10000, 10000);
        GdipMeasureString(g, text, text.Length, Font((float)px, bold), ref layout, format, out var bound, out _, out _);
        return (bound.W, bound.H);
    }

    public void Text(double x, double y, string text, double px, bool bold, uint argb)
    {
        var rect = new RectF((float)x, (float)y, 10000, 10000);
        GdipCreateSolidFill(argb, out var b);
        GdipDrawString(g, text, text.Length, Font((float)px, bold), ref rect, format, b);
        GdipDeleteBrush(b);
    }

    /// <summary>알약 모양 글자 상자. anchorY: 0=위, 0.5=가운데, 1=아래 기준. 상자 크기를 돌려줌.</summary>
    public (double X, double Y, double W, double H) Pill(double cx, double y, string text, double px, bool bold,
        uint bg, uint fg, double anchorY = 0.5, uint border = 0)
    {
        var (tw, th) = Measure(text, px, bold);
        double padX = px * 0.6, padY = px * 0.28;
        double w = tw + padX * 2, h = px * 1.25 + padY * 2;
        double x0 = cx - w / 2, y0 = y - h * anchorY;
        FillRound(x0, y0, w, h, h / 2, bg);
        if (border != 0) StrokeRound(x0, y0, w, h, h / 2, border, 1);
        Text(x0 + padX, y0 + (h - th) / 2, text, px, bold, fg);
        return (x0, y0, w, h);
    }
}

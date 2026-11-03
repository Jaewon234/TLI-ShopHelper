using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>
/// 업데이트 진행 창: 화면 가운데 작은 어두운 창에 단계와 진행 막대를 보여줌.
/// 다른 스레드에서 Set() 으로 바꾸면 UI 스레드에서 다시 그림.
/// </summary>
sealed class ProgressWindow
{
    const int WM_PAINT_ = 0xF, WM_ERASEBKGND = 0x14, WM_LBUTTONUP = 0x202, WM_CLOSE = 0x10, MSG_REDRAW = 0x8000 + 20;
    static readonly uint Bg = Canvas.Argb(255, 14, 16, 22), Border = Canvas.Argb(255, 44, 49, 61), Txt = Canvas.Argb(255, 232, 234, 238),
        Muted = Canvas.Argb(255, 150, 155, 165), Gold = Canvas.Argb(255, 234, 179, 8), Track = Canvas.Argb(255, 34, 38, 48),
        Red = Canvas.Argb(255, 239, 68, 68), Green = Canvas.Argb(255, 34, 197, 94);

    readonly object gate = new();
    string title = "", stage = "", detail = "";
    double fraction = -1;     // 0~1, 음수면 막대 없음
    bool failed, done;
    public IntPtr Hwnd { get; private set; }
    double scale = 1;
    int w, h;

    int S(double v) => (int)Math.Round(v * scale);

    /// <summary>UI 스레드에서 호출.</summary>
    public void Show(string cls, IntPtr inst, string title)
    {
        lock (gate) { this.title = title; stage = "준비 중..."; detail = ""; fraction = -1; failed = done = false; }
        if (Hwnd != IntPtr.Zero && IsWindow(Hwnd)) { Redraw(); return; }
        scale = GetDpiForSystem() / 96.0;
        w = S(420); h = S(150);
        int sx = (GetSystemMetrics(0) - w) / 2, sy = (GetSystemMetrics(1) - h) / 2;
        Hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, cls, "업데이트", WS_POPUP, sx, sy, w, h,
            IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        SetWindowRgn(Hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, S(24), S(24)), true);
        ShowWindow(Hwnd, 5);
    }

    /// <summary>아무 스레드에서나: 단계/진행률 갱신.</summary>
    public void Set(string stage, string detail = "", double fraction = -1, bool failed = false, bool done = false)
    {
        lock (gate) { this.stage = stage; this.detail = detail; this.fraction = fraction; this.failed = failed; this.done = done; }
        Redraw();
    }

    void Redraw() { if (Hwnd != IntPtr.Zero) PostMessageW(Hwnd, MSG_REDRAW, IntPtr.Zero, IntPtr.Zero); }

    public void Close()
    {
        if (Hwnd != IntPtr.Zero && IsWindow(Hwnd)) DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
    }

    void Paint()
    {
        string t, st, de; double fr; bool fail, ok;
        lock (gate) { t = title; st = stage; de = detail; fr = fraction; fail = failed; ok = done; }
        var dc = BeginPaint(Hwnd, out var ps);
        var mem = CreateCompatibleDC(dc);
        var bmp = CreateCompatibleBitmap(dc, w, h);
        var old = SelectObject(mem, bmp);
        using (var c = Canvas.OnDc(mem))
        {
            c.Clear(Bg);
            c.StrokeRound(0.5, 0.5, w - 1, h - 1, S(12), Border, 1);
            c.FillRect(S(20), S(22), S(4), S(20), fail ? Red : ok ? Green : Gold);
            c.Text(S(32), S(17), t, S(18), true, Txt);
            c.Text(S(22), S(56), st, S(15), true, fail ? Red : ok ? Green : Txt);
            if (fr >= 0)
            {
                double bw = w - S(44);
                c.FillRound(S(22), S(88), bw, S(10), S(5), Track);
                c.FillRound(S(22), S(88), Math.Max(S(10), bw * Math.Clamp(fr, 0, 1)), S(10), S(5), ok ? Green : Gold);
            }
            c.Text(S(22), S(108), de, S(12), false, Muted);
            if (fail) c.Text(S(22), S(128), "클릭하면 닫힙니다", S(12), false, Muted);
        }
        BitBlt(dc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem);
        EndPaint(Hwnd, ref ps);
    }

    public IntPtr? Handle(IntPtr hw, uint msg, IntPtr wp, IntPtr lp)
    {
        if (hw != Hwnd || Hwnd == IntPtr.Zero) return null;
        switch ((int)msg)
        {
            case MSG_REDRAW: InvalidateRect(Hwnd, IntPtr.Zero, false); return IntPtr.Zero;
            case WM_PAINT_: Paint(); return IntPtr.Zero;
            case WM_ERASEBKGND: return new IntPtr(1);
            case WM_LBUTTONUP:
                bool fail; lock (gate) fail = failed;
                if (fail) Close();
                return IntPtr.Zero;
            case WM_CLOSE: Close(); return IntPtr.Zero;
        }
        return null;
    }
}

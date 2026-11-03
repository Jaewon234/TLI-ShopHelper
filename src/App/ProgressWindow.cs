using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>
/// 업데이트 진행 창: 화면 가운데 작은 어두운 창에 단계와 진행 막대를 보여줌.
/// 다른 스레드에서 Set() 으로 바꾸면 UI 스레드에서 다시 그림.
/// ShowMessage() 로 소리 없는 알림/질문 창(버튼, 저절로 닫힘)으로도 씀 (MessageBox 대신).
/// </summary>
sealed class ProgressWindow
{
    const int WM_PAINT_ = 0xF, WM_ERASEBKGND = 0x14, WM_LBUTTONUP = 0x202, WM_MOUSEMOVE = 0x200, WM_KEYDOWN = 0x100, WM_SETCURSOR = 0x20,
        WM_CLOSE = 0x10, MSG_REDRAW = 0x8000 + 20, MSG_AUTOCLOSE = 0x8000 + 21;
    static readonly uint Bg = Canvas.Argb(255, 14, 16, 22), Border = Canvas.Argb(255, 44, 49, 61), Txt = Canvas.Argb(255, 232, 234, 238),
        Muted = Canvas.Argb(255, 150, 155, 165), Gold = Canvas.Argb(255, 234, 179, 8), Track = Canvas.Argb(255, 34, 38, 48),
        Red = Canvas.Argb(255, 239, 68, 68), Green = Canvas.Argb(255, 34, 197, 94);

    readonly object gate = new();
    string title = "", stage = "", detail = "";
    double fraction = -1;     // 0~1, 음수면 막대 없음
    bool failed, done, clickClose;
    string[] buttons = Array.Empty<string>();
    Action<int>? onButton;
    int hover = -1, gen;
    public IntPtr Hwnd { get; private set; }
    double scale = 1;
    int w, h;

    int S(double v) => (int)Math.Round(v * scale);

    /// <summary>UI 스레드에서 호출.</summary>
    public void Show(string cls, IntPtr inst, string title)
    {
        lock (gate)
        {
            this.title = title; stage = "준비 중..."; detail = ""; fraction = -1; failed = done = clickClose = false;
            buttons = Array.Empty<string>(); onButton = null; hover = -1; gen++;
        }
        Open(cls, inst, 150);
    }

    /// <summary>
    /// UI 스레드에서 호출: 소리 없는 알림 창. buttons 가 있으면 누른 버튼 번호로 onButton 을 부름 (창은 먼저 닫힘).
    /// autoCloseMs &gt; 0 이면 그만큼 뒤에 저절로 닫힘. 버튼이 없으면 클릭해도 닫힘.
    /// </summary>
    public void ShowMessage(string cls, IntPtr inst, string title, string stage, string detail,
        bool failed = false, bool done = false, string[]? buttons = null, Action<int>? onButton = null, int autoCloseMs = 0)
    {
        int g;
        lock (gate)
        {
            this.title = title; this.stage = stage; this.detail = detail; fraction = -1;
            this.failed = failed; this.done = done;
            this.buttons = buttons ?? Array.Empty<string>(); this.onButton = onButton;
            clickClose = this.buttons.Length == 0; hover = -1;
            g = ++gen;
        }
        Open(cls, inst, this.buttons.Length > 0 ? 176 : 150);
        Redraw();
        if (autoCloseMs > 0)
        {
            var hw = Hwnd;
            new Thread(() => { Thread.Sleep(autoCloseMs); PostMessageW(hw, MSG_AUTOCLOSE, (IntPtr)g, IntPtr.Zero); }) { IsBackground = true }.Start();
        }
    }

    void Open(string cls, IntPtr inst, int height)
    {
        scale = GetDpiForSystem() / 96.0;
        w = S(420); h = S(height);
        int sx = (GetSystemMetrics(0) - w) / 2, sy = (GetSystemMetrics(1) - h) / 2;
        if (Hwnd != IntPtr.Zero && IsWindow(Hwnd))
        {
            SetWindowPos(Hwnd, new IntPtr(-1) /*TOPMOST*/, sx, sy, w, h, 0x40 /*SHOWWINDOW*/);
            SetWindowRgn(Hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, S(24), S(24)), true);
            Redraw();
            return;
        }
        Hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, cls, "업데이트", WS_POPUP, sx, sy, w, h,
            IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        SetWindowRgn(Hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, S(24), S(24)), true);
        ShowWindow(Hwnd, 5);
    }

    /// <summary>버튼 i 의 자리 (오른쪽부터, 첫 버튼이 가장 오른쪽 = 기본 버튼).</summary>
    (int X, int Y, int W, int H) ButtonRect(int i)
    {
        int bw = S(92), bh = S(32), gap = S(10);
        return (w - S(22) - bw - i * (bw + gap), h - S(22) - bh, bw, bh);
    }

    int HitButton(IntPtr lp)
    {
        int x = (short)(lp.ToInt64() & 0xFFFF), y = (short)((lp.ToInt64() >> 16) & 0xFFFF);
        string[] b; lock (gate) b = buttons;
        for (int i = 0; i < b.Length; i++)
        {
            var r = ButtonRect(i);
            if (x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H) return i;
        }
        return -1;
    }

    void Press(int i)
    {
        Action<int>? cb; lock (gate) { cb = onButton; onButton = null; }
        Close();
        cb?.Invoke(i);
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
        string t, st, de; double fr; bool fail, ok, cc; string[] bs; int hv;
        lock (gate) { t = title; st = stage; de = detail; fr = fraction; fail = failed; ok = done; cc = clickClose; bs = buttons; hv = hover; }
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
            c.Text(S(22), fr >= 0 ? S(108) : S(86), de, S(12), false, Muted);
            for (int i = 0; i < bs.Length; i++)
            {
                var r = ButtonRect(i);
                bool primary = i == 0;
                uint bg = primary ? Gold : Track;
                if (i == hv) bg = primary ? Canvas.Argb(255, 250, 204, 21) : Canvas.Argb(255, 48, 53, 66);
                c.FillRound(r.X, r.Y, r.W, r.H, S(8), bg);
                var (tw, th) = c.Measure(bs[i], S(14), true);
                c.Text(r.X + (r.W - tw) / 2, r.Y + (r.H - th) / 2, bs[i], S(14), true, primary ? Bg : Txt);
            }
            if (bs.Length == 0 && (fail || cc)) c.Text(S(22), h - S(24), "클릭하면 닫힙니다", S(12), false, Muted);
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
            {
                bool fail, cc; int n; lock (gate) { fail = failed; cc = clickClose; n = buttons.Length; }
                if (n > 0) { int i = HitButton(lp); if (i >= 0) Press(i); }
                else if (fail || cc) Close();
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                int i = HitButton(lp); bool ch;
                lock (gate) { ch = i != hover; hover = i; }
                if (ch) Redraw();
                return IntPtr.Zero;
            }
            case WM_SETCURSOR:
                SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)32512 /*IDC_ARROW*/));
                return new IntPtr(1);
            case WM_KEYDOWN:
            {
                int n; lock (gate) n = buttons.Length;
                int k = (int)wp.ToInt64();
                if (n > 0 && k == 0x0D) Press(0);                 // Enter = 첫 버튼
                else if (n > 1 && k == 0x1B) Press(n - 1);        // Esc = 마지막 버튼 (나중에)
                else if (n == 0 && k is 0x0D or 0x1B) Close();
                return IntPtr.Zero;
            }
            case MSG_AUTOCLOSE:
            {
                int g; lock (gate) g = gen;
                if ((int)wp.ToInt64() == g) Close();
                return IntPtr.Zero;
            }
            case WM_CLOSE: Close(); return IntPtr.Zero;
        }
        return null;
    }
}

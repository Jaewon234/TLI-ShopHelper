using System.Text;
using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>
/// 트레이에서 여는 작은 계산기 창 (제작비 계산 / 스킬 레벨업) 의 공통 부분.
///  - 열면 닫을 때까지 항상 위. 처음엔 게임 화면 오른쪽 위, 제목 줄을 끌어 옮기면 위치를 기억
///  - 입력칸을 누를 때만 키보드가 이 창으로 옴. 그 밖의 곳을 눌러도 게임 포커스를 뺏지 않음
///  - 화면 캡처(상점 OCR)에 안 찍힘
///  - ▁ 로 제목 줄만 남기고 접기, ✕ 로 닫기
/// 모양은 시세 입력 창과 같음 (색·글꼴·입력칸).
/// </summary>
abstract class CalcWindow
{
    protected const int WM_PAINT_ = 0xF, WM_ERASEBKGND = 0x14, WM_COMMAND_ = 0x111, WM_CTLCOLOREDIT = 0x133,
        WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSEMOVE = 0x200, WM_MOUSEACTIVATE = 0x21, WM_SETFONT = 0x30,
        WM_SETCURSOR = 0x20, WM_CLOSE = 0x10, EN_CHANGE = 0x300, EN_SETFOCUS = 0x100, EM_SETSEL = 0xB1, EM_LIMITTEXT = 0xC5,
        MSG_SELALL = 0x8000 + 30;
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x10000, ES_AUTOHSCROLL = 0x80,
        ES_CENTER = 0x1, ES_RIGHT = 0x2, WS_EX_CONTROLPARENT = 0x10000, WS_CLIPCHILDREN = 0x02000000;

    // 색 (시세 입력 창과 같음)
    protected static readonly uint FieldRgb = Rgb(26, 29, 37), TextRgb = Rgb(240, 240, 240);
    protected static readonly uint Bg = Canvas.Argb(255, 14, 16, 22), Field = Canvas.Argb(255, 26, 29, 37),
        FieldOff = Canvas.Argb(255, 20, 22, 29), Line_ = Canvas.Argb(255, 34, 38, 48), Border = Canvas.Argb(255, 44, 49, 61),
        Txt = Canvas.Argb(255, 225, 227, 232), Muted = Canvas.Argb(255, 150, 155, 165), Dim = Canvas.Argb(255, 105, 110, 120),
        Gold = Canvas.Argb(255, 234, 179, 8), GoldLine = Canvas.Argb(90, 234, 179, 8), Green = Canvas.Argb(255, 34, 197, 94),
        Warn = Canvas.Argb(255, 239, 68, 68);

    protected const int W = 400, TitleH = 56, FH = 30;
    protected abstract int H { get; }
    protected abstract string Title { get; }
    protected abstract string Subtitle { get; }
    protected abstract CalcState.WinPos Pos { get; }

    readonly string cls;
    readonly IntPtr inst;
    readonly Func<(int X, int Y, int W, int H)?> gameRect;
    protected readonly Action save;
    public IntPtr Hwnd { get; private set; }
    protected double scale = 1;
    IntPtr font, fieldBrush;
    bool dragging; int dragDx, dragDy;
    protected bool loading;

    /// <summary>입력칸 하나: 자리(논리 좌표), 정렬, 금색 테두리(가끔 바꾸는 값)</summary>
    protected sealed class Input
    {
        public IntPtr Edit; public int X, Y, Wd; public bool Accent, Center; public bool Off;
    }
    protected readonly List<Input> boxes = new();

    protected CalcWindow(string cls, IntPtr inst, Func<(int X, int Y, int W, int H)?> gameRect, Action save)
    {
        this.cls = cls; this.inst = inst; this.gameRect = gameRect; this.save = save;
    }

    protected int S(double v) => (int)Math.Round(v * scale);
    public bool IsOpen => Hwnd != IntPtr.Zero && IsWindow(Hwnd);

    // ---------------- 열기 / 닫기 ----------------
    public void Toggle() { if (IsOpen) Close(); else Open(); }

    public void Open()
    {
        if (IsOpen) { SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x1 | 0x2 | SWP_NOACTIVATE | SWP_SHOWWINDOW); return; }
        scale = GetDpiForSystem() / 96.0;
        var (x, y) = StartPos();
        Hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_CONTROLPARENT, cls, Title, WS_POPUP | WS_CLIPCHILDREN,
            x, y, S(W), CurH, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) { App.Log($"{Title} 창 생성 실패"); return; }
        Shape();
        SetWindowDisplayAffinity(Hwnd, 0x11);   // 화면 캡처(상점 OCR)에 안 찍히게
        font = CreateFontW(-S(15), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Malgun Gothic");
        fieldBrush = CreateSolidBrush(FieldRgb);
        loading = true;
        boxes.Clear();
        Build();
        if (Pos.Collapsed) foreach (var b in boxes) ShowWindow(b.Edit, SW_HIDE);
        loading = false;
        Recalc();
        ShowWindow(Hwnd, SW_SHOWNOACTIVATE);   // 열 때도 게임 포커스를 뺏지 않음
        App.Log($"{Title} 열림");
    }

    public void Close()
    {
        if (!IsOpen) return;
        SavePos();
        DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
        if (font != IntPtr.Zero) { DeleteObject(font); font = IntPtr.Zero; }
        if (fieldBrush != IntPtr.Zero) { DeleteObject(fieldBrush); fieldBrush = IntPtr.Zero; }
        boxes.Clear();
    }

    int CurH => Pos.Collapsed ? S(TitleH) : S(H);

    void Shape() => SetWindowRgn(Hwnd, CreateRoundRectRgn(0, 0, S(W) + 1, CurH + 1, S(24), S(24)), true);

    /// <summary>기억한 위치 (화면 밖이면 무시) → 없으면 게임 화면 오른쪽 위</summary>
    (int, int) StartPos()
    {
        int w = S(W);
        if (Pos.X is int px && Pos.Y is int py)
        {
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(MonitorFromPoint(new POINT { X = px + w / 2, Y = py + S(20) }, 0 /*NULL*/), ref mi)) return (px, py);
        }
        var g = gameRect();
        (int X, int Y, int W, int H) area;
        if (g is { } gr) area = gr;
        else
        {
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(MonitorFromPoint(new POINT(), 1 /*PRIMARY*/), ref mi);
            area = (mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
        }
        return DefaultPos(area, w);
    }

    /// <summary>처음 위치: 화면 오른쪽 위 (거래소 오른쪽 위 버튼과 겹치지 않게 조금 내림)</summary>
    protected virtual (int, int) DefaultPos((int X, int Y, int W, int H) a, int w) => (a.X + a.W - w - S(16), a.Y + S(90));

    void SavePos()
    {
        if (!IsOpen) return;
        GetWindowRect(Hwnd, out var r);
        Pos.X = r.Left; Pos.Y = r.Top;
        save();
    }

    // ---------------- 입력칸 ----------------
    /// <summary>논리 좌표 (x, y, 폭) 의 칸 배경 안쪽에 테두리 없는 EDIT 를 넣음</summary>
    protected Input AddBox(int x, int y, int w, string text, bool accent, bool center, int? decimals, string? digits = null, int limit = 0)
    {
        int eh = S(20);
        uint style = (uint)(WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL | (center ? ES_CENTER : ES_RIGHT));
        var e = CreateWindowExW(0, "EDIT", text, unchecked((int)style), S(x) + S(10), S(y) + (S(FH) - eh) / 2, S(w) - S(20), eh,
            Hwnd, new IntPtr(1000 + boxes.Count), IntPtr.Zero, IntPtr.Zero);
        SendMessageW(e, WM_SETFONT, font, new IntPtr(1));
        if (limit > 0) SendMessageW(e, EM_LIMITTEXT, new IntPtr(limit), IntPtr.Zero);
        NumericEdit.Attach(e, decimals, digits);
        var b = new Input { Edit = e, X = x, Y = y, Wd = w, Accent = accent, Center = center };
        boxes.Add(b);
        return b;
    }

    /// <summary>쓸 수 없는 칸 (86레벨 0T 등): 입력칸을 숨기고 '불가' 로 그림. 넣어 둔 값은 지우지 않음</summary>
    protected void SetOff(Input b, bool off)
    {
        if (b.Off == off) return;
        b.Off = off;
        ShowWindow(b.Edit, off ? SW_HIDE : SW_SHOWNOACTIVATE);
    }

    protected static string Text(IntPtr h)
    {
        int n = GetWindowTextLengthW(h);
        var sb = new StringBuilder(n + 1);
        GetWindowTextW(h, sb, n + 1);
        return sb.ToString();
    }

    protected static double? Num(Input b) => PriceInput.TryParse(Text(b.Edit), out var v) ? v : null;
    protected static int Int(Input b) => (int)Math.Round(Num(b) ?? 0);
    protected static string Fmt4(double? v) => v is double d ? Math.Round(d, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : "";
    protected static string Won(double v) => Math.Round(v).ToString("#,##0");
    /// <summary>천 단위 쉼표 + 소수점 4자리까지 (개당 가격 표시용)</summary>
    protected static string Price(double v) => Math.Round(v, 4).ToString("#,##0.####", System.Globalization.CultureInfo.InvariantCulture);

    protected void SetBoxText(Input b, string text)
    {
        if (Text(b.Edit) == text) return;
        bool was = loading; loading = true;
        SetWindowTextW(b.Edit, text);
        loading = was;
    }

    // ---------------- 하위 창이 채우는 부분 ----------------
    protected abstract void Build();
    /// <summary>입력이 바뀜: 상태에 옮겨 담고 저장 (그리기는 Recalc 뒤 자동)</summary>
    protected abstract void ReadInputs();
    protected abstract void Compute();
    protected abstract void DrawBody(Canvas c);
    /// <summary>입력칸 말고 직접 그린 곳을 눌렀을 때 (드롭다운 등). 처리했으면 true</summary>
    protected virtual bool Click(int x, int y) => false;

    protected void Recalc()
    {
        Compute();
        if (IsOpen) InvalidateRect(Hwnd, IntPtr.Zero, false);
    }

    // ---------------- 그리기 ----------------
    (int x, int y, int w, int h) CloseRect => (S(W - 44), S(16), S(28), S(28));
    (int x, int y, int w, int h) FoldRect => (S(W - 78), S(16), S(28), S(28));
    static bool In((int x, int y, int w, int h) r, int px, int py) => px >= r.x && px < r.x + r.w && py >= r.y && py < r.y + r.h;

    void Draw(Canvas c, bool withTexts)
    {
        int h = CurH;
        c.Clear(Bg);
        c.StrokeRound(0.5, 0.5, S(W) - 1, h - 1, S(14), Border, 1);
        c.FillRect(S(18), S(18), S(4), S(20), Gold);
        c.Text(S(30), S(13), Title, S(20), true, Txt);
        var (tw, _) = c.Measure(Title, S(20), true);
        c.Text(S(30) + tw + S(12), S(20), Subtitle, S(13), false, Dim);
        {
            var (x, y, w, hh) = FoldRect;   // ▁ / ▢
            if (Pos.Collapsed) c.StrokeRound(x + S(8), y + S(8), w - S(16), hh - S(16), S(2), Muted, 1.4);
            else c.Line(x + S(8), y + hh - S(9), x + w - S(8), y + hh - S(9), Muted, 1.6);
            (x, y, w, hh) = CloseRect;
            c.Line(x + S(8), y + S(8), x + w - S(8), y + hh - S(8), Muted, 1.6);
            c.Line(x + w - S(8), y + S(8), x + S(8), y + hh - S(8), Muted, 1.6);
        }
        if (Pos.Collapsed) return;
        foreach (var b in boxes)
        {
            if (b.Off)
            {
                c.FillRound(S(b.X), S(b.Y), S(b.Wd), S(FH), S(8), FieldOff);
                CenterText(c, S(b.X) + S(b.Wd) / 2.0, S(b.Y) + S(FH) / 2.0, "불가", S(12), false, Dim);
                continue;
            }
            c.FillRound(S(b.X), S(b.Y), S(b.Wd), S(FH), S(8), Field);
            if (b.Accent) c.StrokeRound(S(b.X) + 0.5, S(b.Y) + 0.5, S(b.Wd) - 1, S(FH) - 1, S(8), GoldLine, 1);
            if (withTexts)   // 화면 그대로 저장용 (평소엔 입력칸이 직접 그림)
            {
                var t = Text(b.Edit);
                if (b.Center) CenterText(c, S(b.X) + S(b.Wd) / 2.0, S(b.Y) + S(FH) / 2.0, t, S(15), false, Txt);
                else RightText(c, S(b.X + b.Wd - 10), S(b.Y) + S(FH) / 2.0, t, S(15), false, Txt);
            }
        }
        DrawBody(c);
    }

    // 글자 도우미 (y 는 가운데 기준)
    protected void LeftText(Canvas c, double x, double cy, string t, double px, bool bold, uint col)
    { var (_, th) = c.Measure(t.Length > 0 ? t : " ", px, bold); c.Text(x, cy - th / 2, t, px, bold, col); }
    protected void RightText(Canvas c, double xr, double cy, string t, double px, bool bold, uint col)
    { var (tw, th) = c.Measure(t.Length > 0 ? t : " ", px, bold); c.Text(xr - tw, cy - th / 2, t, px, bold, col); }
    protected void CenterText(Canvas c, double cx, double cy, string t, double px, bool bold, uint col)
    { var (tw, th) = c.Measure(t.Length > 0 ? t : " ", px, bold); c.Text(cx - tw / 2, cy - th / 2, t, px, bold, col); }
    protected void Sep(Canvas c, int y) => c.Line(S(18), S(y), S(W - 18), S(y), Line_);

    /// <summary>드롭다운 모양 (누르면 다음 값으로 바뀜: 포커스를 뺏는 메뉴 대신)</summary>
    protected void DrawCombo(Canvas c, int x, int y, int w, string text)
    {
        c.FillRound(S(x), S(y), S(w), S(FH), S(8), Field);
        LeftText(c, S(x + 10), S(y) + S(FH) / 2.0, text, S(15), false, Txt);
        double cx = S(x + w - 16), cy = S(y) + S(FH) / 2.0;
        c.Line(cx - S(5), cy - S(2), cx, cy + S(3), Muted, 1.8);
        c.Line(cx, cy + S(3), cx + S(5), cy - S(2), Muted, 1.8);
    }
    protected bool InLogical(int x, int y, int w, int h, int px, int py) => In((S(x), S(y), S(w), S(h)), px, py);

    void Paint()
    {
        var dc = BeginPaint(Hwnd, out var ps);
        int w = S(W), h = CurH;
        var mem = CreateCompatibleDC(dc);
        var bmp = CreateCompatibleBitmap(dc, w, h);
        var old = SelectObject(mem, bmp);
        using (var c = Canvas.OnDc(mem)) Draw(c, withTexts: false);
        BitBlt(dc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem);
        EndPaint(Hwnd, ref ps);
    }

    /// <summary>화면 그대로 저장용: 지금 모습 그대로 그림 (이 창은 일반 캡처에 안 찍혀서)</summary>
    public (Frame img, IntPtr hwnd)? RenderVisible()
    {
        if (!IsOpen || !IsWindowVisible(Hwnd)) return null;
        int w = S(W), h = CurH;
        var bits = System.Runtime.InteropServices.Marshal.AllocHGlobal(w * h * 4);
        try
        {
            using (var c = Canvas.OnBits(bits, w, h)) Draw(c, withTexts: true);
            var f = new Frame(w, h);
            System.Runtime.InteropServices.Marshal.Copy(bits, f.Px, 0, f.Px.Length);
            return (f, Hwnd);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(bits); }
    }

    // ---------------- 메시지 ----------------
    public IntPtr? Handle(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        if (h != Hwnd || Hwnd == IntPtr.Zero) return null;
        int mx = (short)(lp.ToInt64() & 0xFFFF), my = (short)((lp.ToInt64() >> 16) & 0xFFFF);
        switch ((int)msg)
        {
            case WM_PAINT_: Paint(); return IntPtr.Zero;
            case WM_ERASEBKGND: return new IntPtr(1);
            case WM_MOUSEACTIVATE:
            {
                // 입력칸을 누를 때만 이 창이 앞으로 옴 (키보드 입력). 그 밖엔 게임 포커스 유지
                GetCursorPos(out var p);
                foreach (var b in boxes)
                {
                    if (b.Off || !IsWindowVisible(b.Edit)) continue;
                    GetWindowRect(b.Edit, out var r);
                    if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top - S(5) && p.Y < r.Bottom + S(5)) return new IntPtr(1);   // MA_ACTIVATE
                }
                return new IntPtr(3);   // MA_NOACTIVATE
            }
            case WM_LBUTTONDOWN:
                if (my < S(TitleH) && !In(CloseRect, mx, my) && !In(FoldRect, mx, my))
                {
                    dragging = true; dragDx = mx; dragDy = my; SetCapture(Hwnd);
                    return IntPtr.Zero;
                }
                // 입력칸 배경(글자 바깥 여백)을 눌러도 그 칸으로
                foreach (var b in boxes)
                    if (!b.Off && InLogical(b.X, b.Y, b.Wd, FH, mx, my)) { SetForegroundWindow(Hwnd); SetFocus(b.Edit); return IntPtr.Zero; }
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                if (dragging)
                {
                    GetCursorPos(out var p);
                    SetWindowPos(Hwnd, HWND_TOPMOST, p.X - dragDx, p.Y - dragDy, 0, 0, 0x1 /*NOSIZE*/ | SWP_NOACTIVATE);
                }
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (dragging) { dragging = false; ReleaseCapture(); SavePos(); return IntPtr.Zero; }
                if (In(CloseRect, mx, my)) { Close(); return IntPtr.Zero; }
                if (In(FoldRect, mx, my))
                {
                    Pos.Collapsed = !Pos.Collapsed;
                    SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, S(W), CurH, 0x2 /*NOMOVE*/ | SWP_NOACTIVATE);
                    Shape();
                    foreach (var b in boxes) if (!b.Off) ShowWindow(b.Edit, Pos.Collapsed ? SW_HIDE : SW_SHOWNOACTIVATE);
                    save();
                    InvalidateRect(Hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }
                if (!Pos.Collapsed && Click(mx, my)) { ReadInputs(); Recalc(); }
                return IntPtr.Zero;
            case WM_CTLCOLOREDIT:
                SetTextColor(wp, TextRgb);
                SetBkColor(wp, FieldRgb);
                return fieldBrush;
            case WM_COMMAND_:
            {
                int code = (int)((wp.ToInt64() >> 16) & 0xFFFF);
                if (code == EN_CHANGE && !loading) { ReadInputs(); Recalc(); }
                // 개수처럼 짧은 칸은 누르면 전체 선택 → 바로 새 숫자를 치면 바뀜
                if (code == EN_SETFOCUS && boxes.FirstOrDefault(b => b.Edit == lp) is { Center: true } bx)
                    PostMessageW(Hwnd, MSG_SELALL, bx.Edit, IntPtr.Zero);
                return IntPtr.Zero;
            }
            case MSG_SELALL:
                SendMessageW(wp, EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
                return IntPtr.Zero;
            case WM_CLOSE: Close(); return IntPtr.Zero;
        }
        return null;
    }
}

/// <summary>장비 제작비 계산: 등급별 1T/0T 옵션 개수 → 기대 제작비 (양손 ×2, 보정 %), + 베이스 비용</summary>
sealed class CraftWindow : CalcWindow
{
    readonly CalcState state;
    readonly Func<double?> getAbsEmber;      // 절대자 엠버 = 상점 시세표 값
    readonly Action<double?> setAbsEmber;
    CalcState.CraftState St => state.Craft;
    protected override int H => 560;
    protected override string Title => "제작비 계산";
    protected override string Subtitle => "결정 기준 · 1회 레시피";
    protected override CalcState.WinPos Pos => St.Window;

    Input corr = null!, fossil = null!, baseB = null!;
    readonly Input[] ember = new Input[3], t1 = new Input[3], t0 = new Input[3];
    CraftCalc.Result? res;

    public CraftWindow(string cls, IntPtr inst, Func<(int X, int Y, int W, int H)?> gameRect, CalcState state, Action save,
        Func<double?> getAbsEmber, Action<double?> setAbsEmber) : base(cls, inst, gameRect, save)
    { this.state = state; this.getAbsEmber = getAbsEmber; this.setAbsEmber = setAbsEmber; }

    // 배치 (논리 좌표, 창 너비 400)
    const int RowY = 64, MatY = 138, TableY = 254, RowStep = 38;
    static (int x, int y) MatPos(int i) => (18 + (i % 2) * 186 + 92, MatY + (i / 2) * RowStep);

    protected override void Build()
    {
        corr = AddBox(258, RowY, 64, Fmt4(St.Correction), true, false, 0, limit: 4);
        // 재료 시세: 진귀한 엠버, 희귀한 엠버, 절대자 엠버, 신성한 화석 (소수점 4자리까지)
        double?[] mats = { St.EmberPrices[0], St.EmberPrices[1], getAbsEmber() ?? St.EmberPrices[2], St.FossilPrice };
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = MatPos(i);
            var b = AddBox(x, y, 80, Fmt4(mats[i]), true, false, 4, limit: 12);
            if (i < 3) ember[i] = b; else fossil = b;
        }
        for (int g = 0; g < 3; g++)
        {
            int y = TableY + g * RowStep;
            t1[g] = AddBox(118, y, 44, St.T1[g] > 0 ? St.T1[g].ToString() : "", false, true, 0, "01234", 1);
            t0[g] = AddBox(190, y, 44, St.T0[g] > 0 ? St.T0[g].ToString() : "", false, true, 0, "01234", 1);
        }
        baseB = AddBox(W - 130, 410, 110, Fmt4(St.Base), true, false, 4, limit: 14);
        ApplyLevel();
    }

    CraftGrade[] Recipe => state.Recipes.TryGetValue(St.Level, out var r) ? r : state.Recipes[state.Recipes.Keys.Max()];

    /// <summary>레벨에 따라 막히는 칸 (86: 0T 전체·절대자)</summary>
    void ApplyLevel()
    {
        var rec = Recipe;
        for (int g = 0; g < 3; g++)
        {
            var r = g < rec.Length ? rec[g] : new CraftGrade();
            SetOff(t1[g], r.T1 == null);
            SetOff(t0[g], r.T1 == null || r.T0 == null);
        }
    }

    /// <summary>시세표에서 절대자 엠버를 바꿨을 때 (창이 열려 있으면 칸도 바꿈)</summary>
    public void RefreshShared()
    {
        if (!IsOpen) return;
        SetBoxText(ember[2], Fmt4(getAbsEmber() ?? St.EmberPrices[2]));
        Recalc();
    }

    protected override void ReadInputs()
    {
        St.Correction = Num(corr) ?? 100;
        St.EmberPrices[0] = Num(ember[0]); St.EmberPrices[1] = Num(ember[1]);
        var abs = Num(ember[2]);
        if (abs != St.EmberPrices[2] || abs != getAbsEmber()) { St.EmberPrices[2] = abs; setAbsEmber(abs); }
        St.FossilPrice = Num(fossil);
        for (int g = 0; g < 3; g++) { St.T1[g] = Int(t1[g]); St.T0[g] = Int(t0[g]); }
        St.Base = Num(baseB);
        save();
    }

    protected override void Compute()
    {
        double[] em = { St.EmberPrices[0] ?? 0, St.EmberPrices[1] ?? 0, getAbsEmber() ?? St.EmberPrices[2] ?? 0 };
        res = CraftCalc.Compute(Recipe, St.TwoHand, Num(corr) ?? St.Correction, em, St.FossilPrice ?? 0, St.T1, St.T0, St.Base ?? 0);
    }

    protected override bool Click(int x, int y)
    {
        if (InLogical(18, RowY, 96, FH, x, y))
        {
            // 레벨: 100 → 86 → (82 …) → 100 (레시피에 있는 레벨 순서)
            var levels = state.Recipes.Keys.OrderByDescending(k => k).ToList();
            St.Level = levels[(levels.IndexOf(St.Level) + 1) % levels.Count];
            ApplyLevel();
            return true;
        }
        if (InLogical(122, RowY, 80, FH, x, y)) { St.TwoHand = !St.TwoHand; return true; }
        return false;
    }

    protected override void DrawBody(Canvas c)
    {
        DrawCombo(c, 18, RowY, 96, $"{St.Level}레벨");
        DrawCombo(c, 122, RowY, 80, St.TwoHand ? "양손" : "한손");
        LeftText(c, S(222), S(RowY) + S(FH) / 2.0, "보정", S(15), false, Muted);
        LeftText(c, S(330), S(RowY) + S(FH) / 2.0, "%", S(15), false, Muted);
        Sep(c, 106);

        LeftText(c, S(20), S(124), "재료 시세", S(13), false, Dim);
        RightText(c, S(W - 20), S(124), "1개당 결정", S(12), false, Dim);
        string[] names = { CraftCalc.Embers[0], CraftCalc.Embers[1], CraftCalc.Embers[2], CraftCalc.Fossil };
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = MatPos(i);
            LeftText(c, S(x - 90), S(y) + S(FH) / 2.0, names[i], S(14), false, Muted);
        }
        Sep(c, 222);

        int hy = 238;
        LeftText(c, S(20), S(hy), "등급", S(13), false, Dim);
        CenterText(c, S(140), S(hy), "1T", S(13), false, Dim);
        CenterText(c, S(212), S(hy), "0T", S(13), false, Dim);
        RightText(c, S(W - 20), S(hy), "소계", S(13), false, Dim);
        for (int g = 0; g < 3; g++)
        {
            int y = TableY + g * RowStep;
            double cy = S(y) + S(FH) / 2.0;
            var gc = res?.Grades[g];
            bool none = gc is { T1Possible: false };
            LeftText(c, S(20), cy, CraftCalc.Grades[g], S(15), false, none ? Dim : Txt);
            if (none) RightText(c, S(W - 20), cy, $"{St.Level}레벨 불가", S(12), false, Dim);
            else
            {
                double sub = gc?.Subtotal ?? 0;
                RightText(c, S(W - 20), cy, Won(sub), S(15), false, sub > 0 ? Txt : Dim);
            }
        }
        Sep(c, 374);

        double corrV = Num(corr) ?? St.Correction;
        LeftText(c, S(20), S(392), "제작비", S(15), false, Muted);
        var (tw, _) = c.Measure("제작비", S(15), false);
        string note = (St.TwoHand ? "양손 ×2 · " : "") + $"보정 {Fmt4(corrV)}%";
        LeftText(c, S(20) + tw + S(8), S(393), note, S(12), false, Dim);
        RightText(c, S(W - 20), S(392), Won(res?.Craft ?? 0) + "결", S(15), false, Txt);
        LeftText(c, S(20), S(410) + S(FH) / 2.0, "베이스 비용", S(15), false, Muted);
        (tw, _) = c.Measure("베이스 비용", S(15), false);
        LeftText(c, S(20) + tw + S(8), S(410) + S(FH) / 2.0 + S(1), "보정 미적용", S(12), false, Dim);
        Sep(c, 452);
        LeftText(c, S(20), S(476), "총 비용", S(17), true, Txt);
        RightText(c, S(W - 20), S(476), Won(res?.Total ?? 0) + "결", S(22), true, Gold);
        LeftText(c, S(20), S(512), Recipe.Any(r => r.T0 != null) ? "0T = 1T 붙이기 + 승급 (실패해도 1T 유지)" : $"{St.Level}레벨: 1T 만 가능 (0T·절대자 불가)", S(12), false, Dim);
        LeftText(c, S(20), S(530), "개수는 0~4 · 양손은 재료 2배 · 레벨·한손은 눌러서 바꿈", S(12), false, Dim);
    }
}

/// <summary>부귀·숭고 스킬 레벨업: 재료 스킬 매물로 상승치 50 을 가장 싸게 채우는 조합 + 영감 재료 ×500 + 베이스</summary>
sealed class SkillWindow : CalcWindow
{
    readonly CalcState state;
    CalcState.SkillState St => state.Skill;
    protected override int H => 520;
    protected override string Title => "스킬 레벨업";
    protected override string Subtitle => $"부귀·숭고 · 상승치 {SkillCalc.Need}";
    protected override CalcState.WinPos Pos => St.Window;

    readonly Input[] price = new Input[3], count = new Input[3];
    Input insp = null!, baseB = null!;
    SkillCalc.Result? res;

    public SkillWindow(string cls, IntPtr inst, Func<(int X, int Y, int W, int H)?> gameRect, CalcState state, Action save)
        : base(cls, inst, gameRect, save) { this.state = state; }

    /// <summary>처음 위치: 제작비 계산 창 왼쪽 (둘 다 열어도 안 겹치게)</summary>
    protected override (int, int) DefaultPos((int X, int Y, int W, int H) a, int w) => (a.X + a.W - w * 2 - S(28), a.Y + S(90));

    const int RowsY = 88, RowStep = 38;

    protected override void Build()
    {
        for (int i = 0; i < 3; i++)
        {
            int y = RowsY + i * RowStep;
            price[i] = AddBox(140, y, 112, Fmt4(St.Prices[i]), true, false, 4, limit: 12);
            count[i] = AddBox(266, y, 48, St.Counts[i] > 0 ? St.Counts[i].ToString() : "", false, true, 0, limit: 2);
        }
        insp = AddBox(196, 318, 90, Fmt4(St.Inspiration), true, false, 4, limit: 12);
        baseB = AddBox(196, 356, 90, Fmt4(St.Base), true, false, 4, limit: 14);
    }

    protected override void ReadInputs()
    {
        for (int i = 0; i < 3; i++) { St.Prices[i] = Num(price[i]); St.Counts[i] = Int(count[i]); }
        St.Inspiration = Num(insp); St.Base = Num(baseB);
        save();
    }

    protected override void Compute() =>
        res = SkillCalc.Compute(St.Prices, St.Counts, St.Inspiration ?? 0, St.Base ?? 0);

    protected override void DrawBody(Canvas c)
    {
        int hy = 72;
        LeftText(c, S(20), S(hy), "재료 스킬", S(13), false, Dim);
        CenterText(c, S(196), S(hy), "개당 가격", S(13), false, Dim);
        CenterText(c, S(290), S(hy), "매물 수", S(13), false, Dim);
        RightText(c, S(W - 20), S(hy), "구매", S(13), false, Dim);
        for (int i = 0; i < 3; i++)
        {
            int y = RowsY + i * RowStep;
            double cy = S(y) + S(FH) / 2.0;
            LeftText(c, S(20), cy, SkillCalc.Tiers[i], S(15), false, Txt);
            var (tw, _) = c.Measure(SkillCalc.Tiers[i], S(15), false);
            LeftText(c, S(20) + tw + S(6), cy + S(1), $"+{SkillCalc.Gain[i]}", S(12), false, Dim);
            int buy = res?.Buy[i] ?? 0;
            RightText(c, S(W - 20), cy, buy > 0 ? $"{buy}개" : "-", S(15), false, buy > 0 ? Gold : Dim);
        }
        Sep(c, 210);

        LeftText(c, S(20), S(230), "최저 조합", S(15), false, Muted);
        bool anyInput = St.Prices.Any(p => p != null) && St.Counts.Any(n => n > 0);
        if (res is { } r && anyInput)
        {
            if (r.Enough) RightText(c, S(W - 20), S(230), $"상승치 {r.Points} / {SkillCalc.Need}", S(13), false, Green);
            else RightText(c, S(W - 20), S(230), $"상승치 {SkillCalc.Need - r.Points} 부족 ({r.Points} / {SkillCalc.Need})", S(13), false, Warn);
            var parts = Enumerable.Range(0, 3).Where(i => r.Buy[i] > 0)
                .Select(i => $"{SkillCalc.Tiers[i]} {Price(St.Prices[i] ?? 0)} × {r.Buy[i]}").ToList();
            LeftText(c, S(20), S(254), parts.Count > 0 ? string.Join("  +  ", parts) : "-", S(14), false, Txt);
        }
        else LeftText(c, S(20), S(254), "티어별 개당 가격과 매물 수를 넣으세요", S(13), false, Dim);
        LeftText(c, S(20), S(282), "스킬 재료비", S(15), false, Muted);
        RightText(c, S(W - 20), S(282), Won(res?.SkillCost ?? 0) + "결", S(15), false, Txt);
        Sep(c, 302);

        double cy1 = S(318) + S(FH) / 2.0, cy2 = S(356) + S(FH) / 2.0;
        LeftText(c, S(20), cy1, "영감 재료", S(15), false, Muted);
        var (w1, _) = c.Measure("영감 재료", S(15), false);
        LeftText(c, S(20) + w1 + S(8), cy1 + S(1), $"× {SkillCalc.InspirationCount}", S(12), false, Dim);
        RightText(c, S(W - 20), cy1, Won((St.Inspiration ?? 0) * SkillCalc.InspirationCount) + "결", S(15), false, Txt);
        LeftText(c, S(20), cy2, "베이스 비용", S(15), false, Muted);
        RightText(c, S(W - 20), cy2, Won(St.Base ?? 0) + "결", S(15), false, Txt);
        Sep(c, 400);
        LeftText(c, S(20), S(424), "총 비용", S(17), true, Txt);
        RightText(c, S(W - 20), S(424), Won(res?.Total ?? 0) + "결", S(22), true, Gold);
        LeftText(c, S(20), S(462), $"있는 매물 안에서 {SkillCalc.Need}을 가장 싸게 채우는 조합", S(12), false, Dim);
        LeftText(c, S(20), S(480), "매물이 모자라면 '상승치 N 부족' 으로 표시", S(12), false, Dim);
    }
}

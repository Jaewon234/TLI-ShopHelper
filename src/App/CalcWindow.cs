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
        public IntPtr Edit; public int X, Y, Wd; public bool Accent, Center; public bool Off, Hidden;
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
        foreach (var b in boxes) if (Pos.Collapsed || b.Hidden) ShowWindow(b.Edit, SW_HIDE);
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
    protected Input AddBox(int x, int y, int w, string text, bool accent, bool center, int? decimals, string? digits = null, int limit = 0, bool plainText = false)
    {
        int eh = S(20);
        uint style = (uint)(WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL | (plainText ? 0 : center ? ES_CENTER : ES_RIGHT));
        var e = CreateWindowExW(0, "EDIT", text, unchecked((int)style), S(x) + S(10), S(y) + (S(FH) - eh) / 2, S(w) - S(20), eh,
            Hwnd, new IntPtr(1000 + boxes.Count), IntPtr.Zero, IntPtr.Zero);
        SendMessageW(e, WM_SETFONT, font, new IntPtr(1));
        if (limit > 0) SendMessageW(e, EM_LIMITTEXT, new IntPtr(limit), IntPtr.Zero);
        if (!plainText) NumericEdit.Attach(e, decimals, digits);
        var b = new Input { Edit = e, X = x, Y = y, Wd = w, Accent = accent, Center = center };
        boxes.Add(b);
        return b;
    }

    /// <summary>쓸 수 없는 칸 (86레벨 0T 등): 입력칸을 숨기고 '불가' 로 그림. 넣어 둔 값은 지우지 않음</summary>
    protected void SetOff(Input b, bool off)
    {
        if (b.Off == off) return;
        b.Off = off;
        ShowWindow(b.Edit, off || b.Hidden || Pos.Collapsed ? SW_HIDE : SW_SHOWNOACTIVATE);
    }

    /// <summary>칸을 통째로 숨김 (배경도 안 그림)</summary>
    protected void SetHidden(Input b, bool hidden)
    {
        if (b.Hidden == hidden) return;
        b.Hidden = hidden;
        ShowWindow(b.Edit, hidden || Pos.Collapsed ? SW_HIDE : SW_SHOWNOACTIVATE);
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
        if (Subtitle.Length > 0)
        {
            var (tw, _) = c.Measure(Title, S(20), true);
            c.Text(S(30) + tw + S(12), S(20), Subtitle, S(13), false, Dim);
        }
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
            if (b.Hidden) continue;
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
                    if (!b.Off && !b.Hidden && InLogical(b.X, b.Y, b.Wd, FH, mx, my)) { SetForegroundWindow(Hwnd); SetFocus(b.Edit); return IntPtr.Zero; }
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
                    foreach (var b in boxes) if (!b.Off && !b.Hidden) ShowWindow(b.Edit, Pos.Collapsed ? SW_HIDE : SW_SHOWNOACTIVATE);
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
    readonly Func<double?> getAbsEmber, getDust;      // 절대자 엠버·결정 가루 = 상점 시세표 값
    readonly Action<double?> setAbsEmber, setDust;
    CalcState.CraftState St => state.Craft;
    protected override int H => 560;   // 레벨이 바뀌어도 창 크기·칸 위치는 그대로 (안 쓰는 칸은 여백)
    protected override string Title => "제작비 계산";
    protected override string Subtitle => "";
    protected override CalcState.WinPos Pos => St.Window;

    Input corr = null!, fossil = null!, dust = null!, baseB = null!;
    readonly Input[] ember = new Input[3], t1 = new Input[3], t0 = new Input[3];
    CraftCalc.Result? res;

    public CraftWindow(string cls, IntPtr inst, Func<(int X, int Y, int W, int H)?> gameRect, CalcState state, Action save,
        Func<double?> getAbsEmber, Action<double?> setAbsEmber, Func<double?> getDust, Action<double?> setDust) : base(cls, inst, gameRect, save)
    {
        this.state = state; this.getAbsEmber = getAbsEmber; this.setAbsEmber = setAbsEmber; this.getDust = getDust; this.setDust = setDust;
    }

    // 배치 (논리 좌표, 창 너비 400). 재료 자리 ①진귀 ②희귀 ③절대자 엠버/결정 가루 ④신성한 화석
    const int RowY = 64, MatY = 138, TableY = 254, RowStep = 38;
    static (int x, int y) MatPos(int i) => (18 + (i % 2) * 186 + 92, MatY + (i / 2) * RowStep);

    double? AbsEmber => getAbsEmber() ?? St.EmberPrices[2];
    double? DustPrice => getDust() ?? St.DustPrice;

    protected override void Build()
    {
        corr = AddBox(258, RowY, 64, Fmt4(St.Correction), true, false, 0, limit: 4);
        // 재료 시세 (소수점 4자리까지). ③ 자리는 레벨에 따라 절대자 엠버 또는 결정 가루
        double?[] mats = { St.EmberPrices[0], St.EmberPrices[1], AbsEmber, St.FossilPrice };
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = MatPos(i);
            var b = AddBox(x, y, 80, Fmt4(mats[i]), true, false, 4, limit: 12);
            if (i < 3) ember[i] = b; else fossil = b;
        }
        { var (x, y) = MatPos(2); dust = AddBox(x, y, 80, Fmt4(DustPrice), true, false, 4, limit: 12); }
        for (int g = 0; g < 3; g++)
        {
            int y = TableY + g * RowStep;
            t1[g] = AddBox(118, y, 44, "", false, true, 0, "01234", 1);
            t0[g] = AddBox(190, y, 44, "", false, true, 0, "01234", 1);
        }
        baseB = AddBox(W - 130, 410, 110, "", true, false, 4, limit: 14);
        LoadLevel();
        ApplyLevel();
    }

    CraftGrade[] Recipe => state.Recipes.TryGetValue(St.Level, out var r) ? r : state.Recipes[state.Recipes.Keys.Max()];
    /// <summary>지금 레벨의 개수·베이스 비용</summary>
    CalcState.LevelVals Cur => St.For(St.Level);

    /// <summary>레벨을 바꿨을 때: 그 레벨에 넣어 둔 개수·베이스 비용을 칸에 채움</summary>
    void LoadLevel()
    {
        var v = Cur;
        for (int g = 0; g < 3; g++)
        {
            SetBoxText(t1[g], v.T1[g] > 0 ? v.T1[g].ToString() : "");
            SetBoxText(t0[g], v.T0[g] > 0 ? v.T0[g].ToString() : "");
        }
        SetBoxText(baseB, Fmt4(v.Base));
    }

    /// <summary>레벨에서 안 쓰는 칸은 숨김 (자리는 여백으로 남고, 넣어 둔 값은 그대로)</summary>
    void ApplyLevel()
    {
        var rec = Recipe;
        var use = CraftCalc.Uses(rec);
        SetHidden(ember[2], use.Dust || !use.AbsEmber);
        SetHidden(dust, !use.Dust);
        SetHidden(fossil, !use.Fossil);
        for (int g = 0; g < 3; g++)
        {
            var r = g < rec.Length ? rec[g] : new CraftGrade();
            SetHidden(t1[g], r.T1 == null);
            SetHidden(t0[g], r.T1 == null || r.T0 == null);
        }
    }

    /// <summary>시세표에서 절대자 엠버·결정 가루를 바꿨을 때 (창이 열려 있으면 칸도 바꿈)</summary>
    public void RefreshShared()
    {
        if (!IsOpen) return;
        SetBoxText(ember[2], Fmt4(AbsEmber));
        SetBoxText(dust, Fmt4(DustPrice));
        Recalc();
    }

    protected override void ReadInputs()
    {
        St.Correction = Num(corr) ?? 100;
        St.EmberPrices[0] = Num(ember[0]); St.EmberPrices[1] = Num(ember[1]);
        var abs = Num(ember[2]);
        if (abs != St.EmberPrices[2] || abs != getAbsEmber()) { St.EmberPrices[2] = abs; setAbsEmber(abs); }
        var du = Num(dust);
        if (du != St.DustPrice || du != getDust()) { St.DustPrice = du; setDust(du); }
        St.FossilPrice = Num(fossil);
        var v = Cur;
        for (int g = 0; g < 3; g++) { v.T1[g] = Int(t1[g]); v.T0[g] = Int(t0[g]); }
        v.Base = Num(baseB);
        save();
    }

    protected override void Compute()
    {
        double[] em = { St.EmberPrices[0] ?? 0, St.EmberPrices[1] ?? 0, AbsEmber ?? 0 };
        res = CraftCalc.Compute(Recipe, St.TwoHand, Num(corr) ?? St.Correction, em, St.FossilPrice ?? 0, Cur.T1, Cur.T0, Cur.Base ?? 0, DustPrice ?? 0);
    }

    protected override bool Click(int x, int y)
    {
        if (InLogical(18, RowY, 96, FH, x, y))
        {
            // 레벨: 100 → 86 → 82 → 100 (레시피에 있는 레벨, 높은 순)
            var levels = state.Recipes.Keys.OrderByDescending(k => k).ToList();
            St.Level = levels[(levels.IndexOf(St.Level) + 1) % levels.Count];
            LoadLevel();   // 그 레벨 값으로 바꿈 (지금 값은 이미 저장돼 있음)
            ApplyLevel();
            return true;
        }
        if (InLogical(122, RowY, 80, FH, x, y)) { St.TwoHand = !St.TwoHand; return true; }
        return false;
    }

    protected override void DrawBody(Canvas c)
    {
        var rec = Recipe;
        var use = CraftCalc.Uses(rec);
        DrawCombo(c, 18, RowY, 96, $"{St.Level}레벨");
        DrawCombo(c, 122, RowY, 80, St.TwoHand ? "양손" : "한손");
        LeftText(c, S(222), S(RowY) + S(FH) / 2.0, "보정", S(15), false, Muted);
        LeftText(c, S(330), S(RowY) + S(FH) / 2.0, "%", S(15), false, Muted);
        Sep(c, 106);

        LeftText(c, S(20), S(124), "재료 시세", S(13), false, Dim);
        RightText(c, S(W - 20), S(124), "1개당 결정", S(12), false, Dim);
        string?[] names =
        {
            CraftCalc.Embers[0], CraftCalc.Embers[1],
            use.Dust ? CraftCalc.Dust : use.AbsEmber ? CraftCalc.Embers[2] : null,
            use.Fossil ? CraftCalc.Fossil : null,
        };
        for (int i = 0; i < 4; i++)
        {
            if (names[i] == null) continue;   // 안 쓰는 재료: 여백
            var (x, y) = MatPos(i);
            LeftText(c, S(x - 90), S(y) + S(FH) / 2.0, names[i]!, S(14), false, i == 2 && use.Dust ? Gold : Muted);
        }
        if (use.Dust && !use.Fossil)
        {
            var (x, y) = MatPos(3);   // ④ 자리가 비면 가루 시세 안내
            LeftText(c, S(x - 90), S(y) + S(FH) / 2.0 - S(8), "결정 가루 = 강복 시세표의", S(11), false, Dim);
            LeftText(c, S(x - 90), S(y) + S(FH) / 2.0 + S(8), "'최초의 불꽃 가루' 와 같은 값", S(11), false, Dim);
        }
        Sep(c, 222);

        int hy = 238;
        LeftText(c, S(20), S(hy), "등급", S(13), false, Dim);
        CenterText(c, S(140), S(hy), CraftCalc.TopTier(St.Level), S(13), false, Dim);
        if (use.AnyT0) CenterText(c, S(212), S(hy), "0T", S(13), false, Dim);
        RightText(c, S(W - 20), S(hy), "소계", S(13), false, Dim);
        for (int g = 0; g < 3; g++)
        {
            var gc = res?.Grades[g];
            if (gc is { T1Possible: false }) continue;   // 이 레벨에서 못 만드는 등급: 여백
            double cy = S(TableY + g * RowStep) + S(FH) / 2.0;
            LeftText(c, S(20), cy, CraftCalc.Grades[g], S(15), false, Txt);
            double sub = gc?.Subtotal ?? 0;
            RightText(c, S(W - 20), cy, Won(sub), S(15), false, sub > 0 ? Txt : Dim);
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
        string foot = use.AnyT0 ? "0T = 1T 붙이기 + 승급 (실패해도 1T 유지)"
            : $"{St.Level}레벨: {CraftCalc.TopTier(St.Level)} 까지 · 초급·진급만" + (use.Dust ? " · 재료에 결정 가루" : "");
        LeftText(c, S(20), S(512), foot, S(12), false, Dim);
        LeftText(c, S(20), S(530), "개수는 0~4 · 양손은 재료 2배 · 레벨·한손은 눌러서 바꿈", S(12), false, Dim);
    }
}

/// <summary>
/// 부귀·숭고 스킬 레벨업: 티어별 [기록] 을 누르고 게임에서 가격 검색하면, 그 결과(로그의 매물 가격 목록)가 그 티어에 들어감.
/// 기록한 매물 안에서 상승치 50 이상을 가장 싸게 채우는 조합 + 영감 재료 ×500 + 베이스 비용.
/// </summary>
sealed class SkillWindow : CalcWindow
{
    readonly CalcState state;
    CalcState.SkillState St => state.Skill;
    protected override int H => 570;
    protected override string Title => "스킬 레벨업";
    protected override string Subtitle => $"부귀·숭고 · 상승치 {SkillCalc.Need}";
    protected override CalcState.WinPos Pos => St.Window;

    Input insp = null!, baseB = null!, nameB = null!;
    SkillCalc.Result? res;
    /// <summary>[기록] 을 눌러 다음 검색을 기다리는 티어 (-1 = 없음)</summary>
    int armed = -1;

    public SkillWindow(string cls, IntPtr inst, Func<(int X, int Y, int W, int H)?> gameRect, CalcState state, Action save)
        : base(cls, inst, gameRect, save) { this.state = state; }

    /// <summary>처음 위치: 제작비 계산 창 왼쪽 (둘 다 열어도 안 겹치게)</summary>
    protected override (int, int) DefaultPos((int X, int Y, int W, int H) a, int w) => (a.X + a.W - w * 2 - S(28), a.Y + S(90));

    // 배치 (논리 좌표)
    const int RowY = 144, RowStep = 38, BtnX = 318, BtnW = 64, ComboY = 280, CostY = 346, InspY = 380, BaseY = 418;
    static int RowCy(int i) => RowY + i * RowStep;

    protected override void Build()
    {
        nameB = AddBox(150, 61, 140, "", false, false, null, limit: 40, plainText: true);
        insp = AddBox(196, InspY, 90, Fmt4(St.Inspiration), true, false, 4, limit: 12);
        baseB = AddBox(196, BaseY, 90, Fmt4(St.Base), true, false, 4, limit: 14);
        armed = -1;
        UpdateNameBox();
    }

    /// <summary>이름표에 없는 스킬이면 이름 칸을 보여 줌 (한 번 적으면 기억)</summary>
    void UpdateNameBox()
    {
        var id = St.SkillId;
        bool unknown = !string.IsNullOrEmpty(id) && !SkillNames.Table.ContainsKey(id);
        if (unknown) SetBoxText(nameB, St.Names.TryGetValue(id!, out var n) ? n : "");
        SetHidden(nameB, !unknown);
    }

    /// <summary>게임에서 가격 검색 결과가 로그에 찍힘 (UI 스레드). [기록] 을 누른 티어가 있을 때만 받음</summary>
    public void OnSearch(XchgResult r)
    {
        if (!IsOpen || armed < 0) return;
        int t = armed; armed = -1;
        if (!string.IsNullOrEmpty(r.ItemId) && r.ItemId != St.SkillId)
        {
            // 다른 스킬: 이전 스킬의 다른 티어 매물이 섞이지 않게 비움
            for (int i = 0; i < 3; i++) { St.Listings[i] = null; St.RecordedAt[i] = null; }
            St.SkillId = r.ItemId;
        }
        St.Listings[t] = r.Prices.ToList();
        St.RecordedAt[t] = DateTime.Now;
        App.Log($"스킬 레벨업 {SkillCalc.Tiers[t]} 기록: 스킬 {r.ItemId} 매물 {r.Prices.Count}개");
        UpdateNameBox();
        save();
        Recalc();
    }

    protected override void ReadInputs()
    {
        St.Inspiration = Num(insp); St.Base = Num(baseB);
        if (!nameB.Hidden && !string.IsNullOrEmpty(St.SkillId))
        {
            var n = Text(nameB.Edit).Trim();
            if (n.Length > 0) St.Names[St.SkillId!] = n; else St.Names.Remove(St.SkillId!);
        }
        save();
    }

    protected override void Compute() =>
        res = SkillCalc.Compute(St.Listings, St.Inspiration ?? 0, St.Base ?? 0);

    protected override bool Click(int x, int y)
    {
        for (int i = 0; i < 3; i++)
            if (InLogical(BtnX, RowCy(i) - 14, BtnW, 28, x, y))
            {
                armed = armed == i ? -1 : i;   // 다시 누르면 취소, 다른 티어를 누르면 그쪽으로
                return true;
            }
        return false;
    }

    protected override void DrawBody(Canvas c)
    {
        // 기록한 스킬
        c.FillRound(S(18), S(56), S(W - 36), S(40), S(10), FieldOff);
        double ny = S(76);
        var id = St.SkillId;
        if (string.IsNullOrEmpty(id)) LeftText(c, S(30), ny, "기록한 스킬 없음", S(14), false, Dim);
        else if (SkillNames.Table.ContainsKey(id)) LeftText(c, S(30), ny, SkillNames.Find(id)!, S(16), true, Txt);
        else
        {
            LeftText(c, S(30), ny, $"스킬 {id}", S(13), false, Muted);
            if (Text(nameB.Edit).Length == 0) LeftText(c, S(160), ny, "이름 입력", S(14), false, Dim);
        }
        var last = St.RecordedAt.Where(t => t != null).Max();
        if (last is DateTime lt) RightText(c, S(W - 30), ny, $"{lt:HH:mm} 기록", S(12), false, Dim);

        // 매물 표
        int hy = 116;
        LeftText(c, S(20), S(hy), "재료 스킬", S(13), false, Dim);
        CenterText(c, S(140), S(hy), "매물", S(13), false, Dim);
        CenterText(c, S(216), S(hy), "가격대", S(13), false, Dim);
        RightText(c, S(304), S(hy), "구매", S(13), false, Dim);
        for (int i = 0; i < 3; i++)
        {
            double cy = S(RowCy(i));
            LeftText(c, S(20), cy, SkillCalc.Tiers[i], S(15), false, Txt);
            var (tw, _) = c.Measure(SkillCalc.Tiers[i], S(15), false);
            LeftText(c, S(20) + tw + S(6), cy + S(1), $"+{SkillCalc.Gain[i]}", S(12), false, Dim);
            var list = St.Listings[i];
            if (armed == i) CenterText(c, S(178), cy, "게임에서 검색하세요…", S(13), false, Gold);
            else if (list == null) CenterText(c, S(178), cy, "기록 없음", S(13), false, Dim);
            else if (list.Count == 0) CenterText(c, S(178), cy, "매물 없음", S(13), false, Dim);
            else
            {
                CenterText(c, S(140), cy, $"{list.Count}개", S(15), false, Txt);
                CenterText(c, S(216), cy, $"{Price(list.Min())} ~ {Price(list.Max())}", S(13), false, Muted);
            }
            int buy = res?.Buy[i] ?? 0;
            RightText(c, S(304), cy, buy > 0 ? $"{buy}개" : "-", S(15), false, buy > 0 ? Gold : Dim);
            // [기록] 버튼
            double bx = S(BtnX), by = S(RowCy(i) - 14), bw = S(BtnW), bh = S(28);
            if (armed == i)
            {
                c.FillRound(bx, by, bw, bh, S(8), Gold);
                CenterText(c, bx + bw / 2, by + bh / 2, "대기 중", S(13), true, Canvas.Argb(255, 30, 22, 0));
            }
            else
            {
                c.FillRound(bx, by, bw, bh, S(8), Field);
                c.StrokeRound(bx + 0.5, by + 0.5, bw - 1, bh - 1, S(8), GoldLine, 1);
                CenterText(c, bx + bw / 2, by + bh / 2, "기록", S(13), false, Txt);
            }
        }
        Sep(c, 236);

        // 최저 조합
        LeftText(c, S(20), S(256), "최저 조합", S(15), false, Muted);
        bool any = St.Listings.Any(l => l is { Count: > 0 });
        if (res is { } r && any)
        {
            int over = r.Points - SkillCalc.Need;
            if (r.Enough) RightText(c, S(W - 20), S(256), $"상승치 {r.Points} / {SkillCalc.Need}" + (over > 0 ? $" ({over} 무효)" : ""), S(13), false, Green);
            else RightText(c, S(W - 20), S(256), $"상승치 {-over} 부족 ({r.Points} / {SkillCalc.Need})", S(13), false, Warn);
            int line = 0;
            for (int i = 0; i < 3; i++)
            {
                if (r.Buy[i] <= 0) continue;
                var sorted = St.Listings[i]!.OrderBy(p => p).Take(r.Buy[i]).ToList();
                string range = sorted.Count == 1 ? Price(sorted[0]) : $"{Price(sorted[0])} ~ {Price(sorted[^1])}";
                double y = S(ComboY + line * 20);
                LeftText(c, S(20), y, $"{SkillCalc.Tiers[i]}  {range} × {r.Buy[i]}", S(14), false, Txt);
                RightText(c, S(W - 20), y, Won(r.TierCost[i]), S(14), false, Txt);
                line++;
            }
        }
        else LeftText(c, S(20), S(ComboY), "티어별 [기록] 을 누르고 게임에서 가격 검색하세요", S(13), false, Dim);
        LeftText(c, S(20), S(CostY), "스킬 재료비", S(15), false, Muted);
        RightText(c, S(W - 20), S(CostY), Won(res?.SkillCost ?? 0) + "결", S(15), false, Txt);
        Sep(c, CostY + 20);

        double cy1 = S(InspY) + S(FH) / 2.0, cy2 = S(BaseY) + S(FH) / 2.0;
        LeftText(c, S(20), cy1, "영감 재료", S(15), false, Muted);
        var (w1, _) = c.Measure("영감 재료", S(15), false);
        LeftText(c, S(20) + w1 + S(8), cy1 + S(1), $"× {SkillCalc.InspirationCount}", S(12), false, Dim);
        RightText(c, S(W - 20), cy1, Won((St.Inspiration ?? 0) * SkillCalc.InspirationCount) + "결", S(15), false, Txt);
        LeftText(c, S(20), cy2, "베이스 비용", S(15), false, Muted);
        RightText(c, S(W - 20), cy2, Won(St.Base ?? 0) + "결", S(15), false, Txt);
        Sep(c, BaseY + 44);
        LeftText(c, S(20), S(BaseY + 68), "총 비용", S(17), true, Txt);
        RightText(c, S(W - 20), S(BaseY + 68), Won(res?.Total ?? 0) + "결", S(22), true, Gold);
        LeftText(c, S(20), S(BaseY + 104), "[기록] 을 누르고 게임에서 그 티어로 가격 검색", S(12), false, Dim);
        LeftText(c, S(20), S(BaseY + 122), "기록 안 누른 검색(같은 스킬 필터 등)은 무시 · 매물은 1개씩", S(12), false, Dim);
    }
}

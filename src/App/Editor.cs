using System.Text;
using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>
/// 시세 입력 창 (어두운 테마, 1개당 결정).
///  - 상점 모드: 신들의 강복이 열리면 게임 화면 왼쪽 아래에 자동으로 뜸. [적용] 후에도 그대로.
///    상점을 나가면 사라지고, 게임에서 다른 창으로 전환하면 잠시 숨음.
///  - 트레이 모드: 트레이 메뉴/더블클릭/Ctrl+Shift+P. [입력완료] 를 누르면 적용하고 닫힘.
/// 입력 중에는 오른쪽에 휘석 환산만 미리 보여주고, 버튼(또는 Enter)을 눌러야 적용.
/// </summary>
sealed class Editor
{
    const int ID_NAME = 1000, ID_PRICE = 2000, ID_RATE = 4001, IDOK = 1, IDCANCEL = 2;
    const int EN_CHANGE = 0x300, WM_SETFONT = 0x30, WM_CLOSE = 0x10, WM_COMMAND_ = 0x111, WM_PAINT_ = 0xF,
        WM_ERASEBKGND = 0x14, WM_CTLCOLOREDIT = 0x133, WM_NCHITTEST = 0x84, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202;
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x10000, ES_AUTOHSCROLL = 0x80,
        WS_EX_CONTROLPARENT = 0x10000, WS_EX_TOOLWINDOW_ = 0x80, WS_CLIPCHILDREN = 0x02000000;
    const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNOACTIVATE_ = 4, SW_RESTORE = 9;

    // 색
    static readonly uint BgRgb = Rgb(14, 16, 22), FieldRgb = Rgb(26, 29, 37), TextRgb = Rgb(240, 240, 240);
    static readonly uint Bg = Canvas.Argb(255, 14, 16, 22), Field = Canvas.Argb(255, 26, 29, 37), Line_ = Canvas.Argb(255, 34, 38, 48),
        Border = Canvas.Argb(255, 44, 49, 61), Txt = Canvas.Argb(255, 225, 227, 232), Muted = Canvas.Argb(255, 150, 155, 165),
        Dim = Canvas.Argb(255, 105, 110, 120), Gold = Canvas.Argb(255, 234, 179, 8), GoldText = Canvas.Argb(255, 30, 22, 0),
        Warn = Canvas.Argb(255, 239, 68, 68);

    readonly Config cfg;
    readonly Func<IEnumerable<string>> unknownNames;
    readonly Action applied;
    string cls = "";
    IntPtr inst;

    public IntPtr Hwnd { get; private set; }
    public bool ShopMode { get; private set; }
    IntPtr font, fieldBrush;
    sealed class Row { public string Label = ""; public IntPtr NameEdit, PriceEdit; public ItemDef? Orig; public int Y; }
    readonly List<Row> rows = new();
    IntPtr rateEdit;
    bool loading, dirty;
    string status = "";
    double scale = 1;
    int winW, winH, footerY;
    HashSet<string> builtExtra = new();
    (int X, int Y, int W, int H) placedFor;

    public Editor(Config cfg, Func<IEnumerable<string>> unknownNames, Action applied)
    {
        this.cfg = cfg; this.unknownNames = unknownNames; this.applied = applied;
    }

    public void Init(string cls, IntPtr inst) { this.cls = cls; this.inst = inst; }

    int S(double v) => (int)Math.Round(v * scale);
    bool Exists => Hwnd != IntPtr.Zero && IsWindow(Hwnd);

    /// <summary>상점 안에 떠 있을 때 화면 좌표 (이익 카드를 그 위에 붙임).</summary>
    public (int X, int Y, int W, int H)? ShopScreenRect()
    {
        if (!Exists || !ShopMode || !IsWindowVisible(Hwnd)) return null;
        GetWindowRect(Hwnd, out var r);
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    // ---------------- 열기/닫기 ----------------

    public void OpenTray()
    {
        if (Exists)
        {
            ShowWindow(Hwnd, SW_RESTORE);
            SetForegroundWindow(Hwnd);
            return;
        }
        Create(shop: false);
        int sh = GetSystemMetrics(1);
        SetWindowPos(Hwnd, HWND_TOPMOST, S(20), Math.Max(0, sh - winH - S(70)), 0, 0, 0x1 | SWP_NOACTIVATE);
        ShowWindow(Hwnd, SW_SHOW);
        SetForegroundWindow(Hwnd);
        if (rows.Count > 0) SetFocus(rows[0].PriceEdit);
    }

    public void ShowInShop((int X, int Y, int W, int H) game)
    {
        var extra = ExtraNames();
        bool focused = Exists && GetForegroundWindow() == Hwnd;
        if (Exists && ShopMode && !focused && !dirty && !extra.SetEquals(builtExtra)) Destroy();
        if (!Exists) { Create(shop: true); placedFor = default; }
        else if (!ShopMode) { ShopMode = true; InvalidateRect(Hwnd, IntPtr.Zero, false); }
        if (placedFor != game)
        {
            placedFor = game;
            int x = game.X + S(16), y = game.Y + game.H - winH - S(16);
            SetWindowPos(Hwnd, HWND_TOPMOST, x, Math.Max(game.Y, y), 0, 0, 0x1 | SWP_NOACTIVATE);
        }
        ShowWindow(Hwnd, SW_SHOWNOACTIVATE_);
    }

    public void HideInShop() { if (Exists && ShopMode) ShowWindow(Hwnd, SW_HIDE); }
    public void CloseShop() { if (Exists && ShopMode) Destroy(); }

    void Destroy()
    {
        if (Exists) DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
        if (font != IntPtr.Zero) { DeleteObject(font); font = IntPtr.Zero; }
        if (fieldBrush != IntPtr.Zero) { DeleteObject(fieldBrush); fieldBrush = IntPtr.Zero; }
        rows.Clear();
        dirty = false; status = "";
    }

    HashSet<string> ExtraNames()
    {
        var known = new HashSet<string>(cfg.Items.Select(i => Parser.Norm(i.Name)));
        return unknownNames().Select(Parser.Norm).Where(n => n.Length > 0 && !known.Contains(n)).Take(4).ToHashSet();
    }

    // ---------------- 배치 ----------------
    const int W = 400, Header = 58, RowH = 38, FieldX = 196, FieldW = 108, FieldH = 30;

    (int x, int y, int w, int h) ButtonRect => (winW - S(18) - S(104), footerY + S(12), S(104), S(38));
    (int x, int y, int w, int h) CloseRect => (winW - S(38), S(16), S(22), S(22));

    void Create(bool shop)
    {
        ShopMode = shop;
        scale = GetDpiForSystem() / 96.0;
        font = CreateFontW(-S(15), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Malgun Gothic");
        fieldBrush = CreateSolidBrush(FieldRgb);

        var items = cfg.Items.Where(i => !i.IsCrystal && !i.IsGamble).ToList();
        builtExtra = ExtraNames();
        var extra = builtExtra.ToList();
        int n = items.Count + extra.Count + 1;

        winW = S(W);
        int y0 = Header + 52;                     // 결정 환율 줄 + 구분선 아래
        footerY = S(y0 + RowH * n + 6);
        winH = footerY + S(62);
        Hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_CONTROLPARENT | WS_EX_TOOLWINDOW_ | WS_EX_LAYERED, cls, "시세 입력",
            WS_POPUP | WS_CLIPCHILDREN, 0, 0, winW, winH, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        SetLayeredWindowAttributes(Hwnd, 0, 246, 2);                         // 살짝 투명
        SetWindowRgn(Hwnd, CreateRoundRectRgn(0, 0, winW + 1, winH + 1, S(28), S(28)), true); // 둥근 모서리
        SetWindowDisplayAffinity(Hwnd, 0x11);                                // 화면 캡처(OCR)에 안 찍히게

        loading = true;
        rows.Clear();
        rateEdit = FieldEdit(Parser.FmtPrice(cfg.CrystalRate), S(FieldX), S(Header + 6), ID_RATE);
        int y = y0, i = 0;
        foreach (var it in items) AddRow(it.Name, it.Price is double p ? Parser.FmtPrice(p) : "", it, i++, editableName: false, ref y);
        foreach (var name in extra) AddRow(name, "", null, i++, editableName: true, ref y);
        AddRow("", "", null, i++, editableName: true, ref y);
        loading = false;
        status = shop ? "고친 뒤 [적용] 또는 Enter" : "고친 뒤 [입력완료] 또는 Enter";
    }

    void AddRow(string name, string price, ItemDef? orig, int i, bool editableName, ref int y)
    {
        var r = new Row { Label = name, Orig = orig, Y = y };
        if (editableName) r.NameEdit = FieldEdit(name, S(18), S(y), ID_NAME + i, S(166));
        r.PriceEdit = FieldEdit(price, S(FieldX), S(y), ID_PRICE + i);
        rows.Add(r);
        y += RowH;
    }

    /// <summary>테두리 없는 EDIT 를 칸 배경 안쪽에 넣음 (배경은 WM_PAINT 에서 그림).</summary>
    IntPtr FieldEdit(string text, int x, int y, int id, int w = 0)
    {
        if (w == 0) w = S(FieldW);
        int eh = S(20);
        var e = CreateWindowExW(0, "EDIT", text, WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
            x + S(10), y + (S(FieldH) - eh) / 2, w - S(20), eh, Hwnd, new IntPtr(id), IntPtr.Zero, IntPtr.Zero);
        SendMessageW(e, WM_SETFONT, font, new IntPtr(1));
        return e;
    }

    static string Text(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        int n = GetWindowTextLengthW(h);
        var sb = new StringBuilder(n + 1);
        GetWindowTextW(h, sb, n + 1);
        return sb.ToString();
    }

    string RowName(Row r) => r.NameEdit != IntPtr.Zero ? Text(r.NameEdit).Trim() : r.Label;

    double RateInput() =>
        PriceInput.TryParse(Text(rateEdit), 1, out var v) && v is double d && d > 0 ? d : PriceInput.CrystalPrice(cfg);

    // ---------------- 그리기 ----------------
    void Paint()
    {
        var dc = BeginPaint(Hwnd, out var ps);
        var mem = CreateCompatibleDC(dc);
        var bmp = CreateCompatibleBitmap(dc, winW, winH);
        var old = SelectObject(mem, bmp);
        using (var c = Canvas.OnDc(mem))
        {
            c.Clear(Bg);
            c.StrokeRound(0.5, 0.5, winW - 1, winH - 1, S(14), Border, 1);
            // 머리
            c.FillRect(S(18), S(20), S(4), S(20), Gold);
            c.Text(S(30), S(15), "시세 입력", S(20), true, Txt);
            var (tw, _) = c.Measure("시세 입력", S(20), true);
            c.Text(S(30) + tw + S(10), S(22), "1개당 결정 · 1/20 · 5.5휘", S(13), false, Dim);
            if (!ShopMode)
            {
                var (x, y, w, h) = CloseRect;
                c.Line(x + S(6), y + S(6), x + w - S(6), y + h - S(6), Muted, 1.6);
                c.Line(x + w - S(6), y + S(6), x + S(6), y + h - S(6), Muted, 1.6);
            }
            // 결정 환율
            int ry = S(Header + 6);
            c.Text(S(20), ry + S(6), "결정 1개 =", S(15), false, Muted);
            FieldBg(c, S(FieldX), ry, S(FieldW), Text(rateEdit), true);
            c.Text(S(FieldX + FieldW + 12), ry + S(7), "휘석", S(13), false, Dim);
            c.Line(S(18), S(Header + 44), winW - S(18), S(Header + 44), Line_);
            // 항목
            double rate = RateInput();
            foreach (var r in rows)
            {
                int y = S(r.Y);
                if (r.NameEdit != IntPtr.Zero)
                {
                    c.FillRound(S(18), y, S(166), S(FieldH), S(8), Field);
                    if (Text(r.NameEdit).Length == 0) c.Text(S(28), y + S(6), "새 항목 이름", S(14), false, Dim);
                }
                else c.Text(S(20), y + S(6), r.Label, S(15), false, Txt);
                string pt = Text(r.PriceEdit);
                FieldBg(c, S(FieldX), y, S(FieldW), pt, false);
                string res = "";
                uint rc = Dim;
                if (!PriceInput.TryParse(pt, rate, out var v)) { res = "형식 오류"; rc = Warn; }
                else if (v is double d) res = "= " + Parser.FmtPrice(d * rate) + "휘";
                c.Text(S(FieldX + FieldW + 12), y + S(7), res, S(13), false, rc);
            }
            // 아래
            c.Line(S(18), footerY, winW - S(18), footerY, Line_);
            c.Text(S(20), footerY + S(22), status, S(13), false, dirty ? Canvas.Argb(255, 220, 190, 120) : Dim);
            var (bx, by, bw, bh) = ButtonRect;
            c.FillRound(bx, by, bw, bh, S(10), Gold);
            string label = ShopMode ? "적용" : "입력완료";
            var (lw, lh) = c.Measure(label, S(16), true);
            c.Text(bx + (bw - lw) / 2, by + (bh - lh) / 2, label, S(16), true, GoldText);
        }
        BitBlt(dc, 0, 0, winW, winH, mem, 0, 0, SRCCOPY);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem);
        EndPaint(Hwnd, ref ps);
    }

    void FieldBg(Canvas c, int x, int y, int w, string text, bool accent)
    {
        c.FillRound(x, y, w, S(FieldH), S(8), Field);
        if (accent) c.StrokeRound(x + 0.5, y + 0.5, w - 1, S(FieldH) - 1, S(8), Canvas.Argb(90, 234, 179, 8), 1);
        if (text.Length == 0 && !accent) c.Text(x + S(10), y + S(6), "시세 없음", S(14), false, Dim);
    }

    // ---------------- 적용 ----------------
    void Apply()
    {
        var errors = new List<string>();
        double rate = PriceInput.CrystalPrice(cfg);
        if (PriceInput.TryParse(Text(rateEdit), 1, out var rv) && rv is double r0 && r0 > 0) rate = r0;
        else errors.Add("결정 환율");

        // 결정·도박 항목은 그대로 두고, 나머지만 입력값으로 바꿈
        var items = cfg.Items.Where(i => i.IsCrystal || i.IsGamble).ToList();
        var normalOld = cfg.Items.Where(i => !i.IsCrystal && !i.IsGamble).ToList();
        var seen = new HashSet<ItemDef>();
        foreach (var r in rows)
        {
            string name = RowName(r);
            if (name.Length == 0) continue;
            if (!PriceInput.TryParse(Text(r.PriceEdit), rate, out var v))
            {
                errors.Add(name);
                if (r.Orig != null) { items.Add(r.Orig); seen.Add(r.Orig); }
                continue;
            }
            var it = r.Orig ?? new ItemDef { Name = name };
            it.Name = name; it.Price = v;
            items.Add(it); seen.Add(it);
        }
        // 창을 연 뒤에 바깥에서 추가된 항목은 지우지 않음
        items.AddRange(normalOld.Where(o => !seen.Contains(o) && !rows.Any(r => r.Orig == o)));

        cfg.CrystalRate = rate;
        cfg.Items = items;
        try { App.SavePrices(cfg); } catch (Exception e) { errors.Add("저장 실패: " + e.Message); }
        applied();
        dirty = false;
        if (!ShopMode && errors.Count == 0) { Destroy(); return; }
        status = errors.Count == 0 ? $"적용됨 {DateTime.Now:HH:mm:ss}" : "형식 오류는 이전 값 유지: " + string.Join(", ", errors);
        InvalidateRect(Hwnd, IntPtr.Zero, false);
    }

    static bool In((int x, int y, int w, int h) r, int px, int py) => px >= r.x && px < r.x + r.w && py >= r.y && py < r.y + r.h;

    /// <summary>Overlay 의 창 프로시저에서 이 창 메시지를 넘겨받음. 처리했으면 결과값, 아니면 null.</summary>
    public IntPtr? Handle(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (h != Hwnd || Hwnd == IntPtr.Zero) return null;
        switch ((int)msg)
        {
            case WM_PAINT_: Paint(); return IntPtr.Zero;
            case WM_ERASEBKGND: return new IntPtr(1);
            case WM_CTLCOLOREDIT:
                SetTextColor(w, TextRgb);
                SetBkColor(w, FieldRgb);
                return fieldBrush;
            case WM_NCHITTEST:
            {
                var p = new POINT { X = (short)(l.ToInt64() & 0xFFFF), Y = (short)((l.ToInt64() >> 16) & 0xFFFF) };
                ScreenToClient(Hwnd, ref p);
                if (p.Y < S(Header - 6) && !(ShopMode == false && In(CloseRect, p.X, p.Y))) return new IntPtr(2); // 머리 = 끌어서 이동
                return new IntPtr(1);
            }
            case WM_LBUTTONUP:
            {
                int x = (short)(l.ToInt64() & 0xFFFF), y = (short)((l.ToInt64() >> 16) & 0xFFFF);
                if (In(ButtonRect, x, y)) Apply();
                else if (!ShopMode && In(CloseRect, x, y)) Destroy();
                return IntPtr.Zero;
            }
            case WM_COMMAND_:
            {
                int id = (int)(w.ToInt64() & 0xFFFF), code = (int)((w.ToInt64() >> 16) & 0xFFFF);
                if (code == EN_CHANGE && !loading)
                {
                    dirty = true;
                    status = ShopMode ? "바뀐 내용 있음 · [적용] 또는 Enter" : "바뀐 내용 있음 · [입력완료] 또는 Enter";
                    InvalidateRect(Hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }
                if (id == IDOK) { Apply(); return IntPtr.Zero; }
                if (id == IDCANCEL && !ShopMode) { Destroy(); return IntPtr.Zero; }
                break;
            }
            case WM_CLOSE: Destroy(); return IntPtr.Zero;
        }
        return null;
    }
}

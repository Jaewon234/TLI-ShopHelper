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
        WM_ERASEBKGND = 0x14, WM_CTLCOLOREDIT = 0x133, WM_NCHITTEST = 0x84, WM_LBUTTONUP = 0x202;
    const int WS_EX_NOACTIVATE = 0x8000000;
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x10000, ES_AUTOHSCROLL = 0x80,
        WS_EX_CONTROLPARENT = 0x10000, WS_EX_TOOLWINDOW_ = 0x80, WS_CLIPCHILDREN = 0x02000000;
    const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNOACTIVATE_ = 4, SW_RESTORE = 9;

    // 색
    static readonly uint FieldRgb = Rgb(26, 29, 37), TextRgb = Rgb(240, 240, 240);
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
    sealed class Row { public string Label = ""; public IntPtr NameEdit, PriceEdit; public ItemDef? Orig; public int Y; public bool Deleted; }

    /// <summary>이름 오른쪽 ✕ (지우기) / ↺ (되돌리기). 적용을 눌러야 실제로 지워짐</summary>
    (int x, int y, int w, int h) DelRect(Row r) => (S(170), S(r.Y) + (S(FieldH) - S(18)) / 2, S(18), S(18));
    bool HasName(Row r) => RowName(r).Length > 0 || r.Deleted;
    readonly List<Row> rows = new();
    IntPtr rateEdit;
    bool loading, dirty;
    string status = "";
    double scale = 1;
    int winW, winH, footerY, mainW;
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
        return (r.Left, r.Top, mainW, r.Bottom - r.Top);   // 이익 카드는 시세 입력 칸 폭에 맞춤
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

    // ---------------- 상점 안: 평소엔 작은 [시세 입력] 버튼만, 누르면 입력 창이 펼쳐짐 ----------------
    IntPtr toggle;
    const int ToggleW = 128, ToggleH = 40;
    (int X, int Y, int W, int H) game;
    bool expanded;   // 상점 안에서 입력 창이 펼쳐져 있는지
    bool ToggleExists => toggle != IntPtr.Zero && IsWindow(toggle);

    /// <summary>순이익 표시를 붙일 자리: 상점 안에 보이는 [시세 입력] 버튼 또는 펼친 시세 창 (화면 좌표, 시세 칸 폭)</summary>
    public (int X, int Y, int W, int H)? AnchorScreenRect()
    {
        if (ShopScreenRect() is { } e) return e;
        if (ToggleExists && IsWindowVisible(toggle)) { GetWindowRect(toggle, out var r); return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); }
        return null;
    }

    public void ShowInShop((int X, int Y, int W, int H) g)
    {
        game = g;
        if (expanded && Exists && ShopMode)
        {
            if (placedFor != g) PlaceExpanded();
            ShowWindow(Hwnd, SW_SHOWNOACTIVATE_);
            return;
        }
        expanded = false;
        if (Exists && !ShopMode) return;              // 트레이에서 연 창이 떠 있으면 그대로 둠
        if (!ToggleExists)
        {
            scale = GetDpiForSystem() / 96.0;
            toggle = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW_ | WS_EX_NOACTIVATE | WS_EX_LAYERED, cls, "시세 입력 버튼",
                WS_POPUP, 0, 0, S(ToggleW), S(ToggleH), IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            SetLayeredWindowAttributes(toggle, 0, 240, 2);
            SetWindowRgn(toggle, CreateRoundRectRgn(0, 0, S(ToggleW) + 1, S(ToggleH) + 1, S(20), S(20)), true);
            SetWindowDisplayAffinity(toggle, 0x11);
        }
        SetWindowPos(toggle, HWND_TOPMOST, g.X + S(16), g.Y + g.H - S(ToggleH) - S(16), 0, 0, 0x1 | SWP_NOACTIVATE);
        ShowWindow(toggle, SW_SHOWNOACTIVATE_);
    }

    void PlaceExpanded()
    {
        placedFor = game;
        int x = game.X + S(16), y = game.Y + game.H - winH - S(16);
        SetWindowPos(Hwnd, HWND_TOPMOST, x, Math.Max(game.Y, y), 0, 0, 0x1 | SWP_NOACTIVATE);
    }

    /// <summary>[시세 입력] 버튼 → 입력 창 펼치기</summary>
    void Expand()
    {
        if (ToggleExists) ShowWindow(toggle, SW_HIDE);
        if (Exists) Destroy();
        Create(shop: true);
        expanded = true;
        PlaceExpanded();
        ShowWindow(Hwnd, SW_SHOW);
        SetForegroundWindow(Hwnd);                      // 바로 입력할 수 있게
        if (rows.Count > 0) SetFocus(rows[0].PriceEdit);
    }

    /// <summary>입력 창 접기 (적용 후 또는 ✕) → 다시 작은 버튼</summary>
    void Collapse()
    {
        if (Exists) Destroy();
        expanded = false;
        if (game.W > 0) ShowInShop(game);
    }

    public void HideInShop()
    {
        if (Exists && ShopMode) ShowWindow(Hwnd, SW_HIDE);
        if (ToggleExists) ShowWindow(toggle, SW_HIDE);
    }

    public void CloseShop()
    {
        if (Exists && ShopMode) Destroy();
        if (ToggleExists) DestroyWindow(toggle);
        toggle = IntPtr.Zero;
        expanded = false;
    }

    void PaintToggle()
    {
        var dc = BeginPaint(toggle, out var ps);
        int w = S(ToggleW), h = S(ToggleH);
        var mem = CreateCompatibleDC(dc);
        var bmp = CreateCompatibleBitmap(dc, w, h);
        var old = SelectObject(mem, bmp);
        using (var c = Canvas.OnDc(mem)) DrawToggle(c, w, h);
        BitBlt(dc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem);
        EndPaint(toggle, ref ps);
    }

    void DrawToggle(Canvas c, int w, int h)
    {
        c.Clear(Bg);
        c.StrokeRound(0.5, 0.5, w - 1, h - 1, S(10), Border, 1);
        c.FillRect(S(14), S(11), S(4), S(18), Gold);
        var (tw, th) = c.Measure("시세 입력", S(15), true);
        c.Text(S(26), (h - th) / 2, "시세 입력", S(15), true, Txt);
        c.Text(S(26) + tw + S(8), (h - th) / 2 + S(1), "▴", S(13), false, Muted);
    }

    /// <summary>
    /// 화면 그대로 저장용: 지금 보이는 시세 창/버튼을 같은 코드로 그림으로 다시 그림.
    /// (창은 OCR 에 안 섞이게 캡처에서 빠져 있어서, 창 내용을 Windows 에서 받아오면 까맣게 나옴)
    /// </summary>
    public List<(Frame img, IntPtr hwnd)> RenderVisible()
    {
        var list = new List<(Frame, IntPtr)>();
        void One(IntPtr wnd, int w, int h, Action<Canvas> draw)
        {
            var bits = System.Runtime.InteropServices.Marshal.AllocHGlobal(w * h * 4);
            try
            {
                using (var c = Canvas.OnBits(bits, w, h)) draw(c);
                var f = new Frame(w, h);
                System.Runtime.InteropServices.Marshal.Copy(bits, f.Px, 0, f.Px.Length);
                list.Add((f, wnd));
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(bits); }
        }
        if (ToggleExists && IsWindowVisible(toggle)) One(toggle, S(ToggleW), S(ToggleH), c => DrawToggle(c, S(ToggleW), S(ToggleH)));
        if (Exists && IsWindowVisible(Hwnd)) One(Hwnd, winW, winH, c => DrawEditor(c, withTexts: true));
        return list;
    }

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
    // 오른쪽 아래에 붙은 '자동 읽기' 칸
    const int SideW = 170, SideGap = 10, SideH = 150;
    const int ID_MIN = 4005;
    IntPtr minEdit;   // 추천 기준 입력칸
    (int x, int y, int w, int h) Side2Rect { get { var (sx, sy, sw, sh) = SideRect; return (sx + sw + S(SideGap), sy, sw, sh); } }
    // 자동 읽기 슬라이더: 0.1초 ~ 1초 (0.1초 단위)
    const double SpeedMin = 0.1, SpeedMax = 1.0;
    bool dragging;

    (int x, int y, int w, int h) ButtonRect => (mainW - S(18) - S(104), footerY + S(12), S(104), S(38));
    (int x, int y, int w, int h) CloseRect => (mainW - S(38), S(16), S(22), S(22));
    (int x, int y, int w, int h) SideRect => (mainW + S(SideGap), winH - S(SideH), S(SideW), S(SideH));
    /// <summary>슬라이더 트랙 (누르는 영역은 위아래로 넉넉히)</summary>
    (int x, int y, int w, int h) TrackRect
    {
        get { var (sx, sy, sw, _) = SideRect; return (sx + S(22), sy + S(86), sw - S(44), S(6)); }
    }
    (int x, int y, int w, int h) SliderHit
    {
        get { var (tx, ty, tw, _) = TrackRect; return (tx - S(14), ty - S(16), tw + S(28), S(38)); }
    }
    double SpeedValue => Math.Clamp(cfg.RereadSec <= 0 ? SpeedMax : cfg.RereadSec, SpeedMin, SpeedMax);
    void SetSpeedFromX(int x)
    {
        var (tx, _, tw, _) = TrackRect;
        double v = SpeedMin + Math.Clamp((x - tx) / (double)tw, 0, 1) * (SpeedMax - SpeedMin);
        v = Math.Round(v * 10) / 10;
        if (Math.Abs(v - cfg.RereadSec) > 1e-9) { cfg.RereadSec = v; InvalidateRect(Hwnd, IntPtr.Zero, false); }
    }

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

        mainW = S(W);
        winW = mainW + S(SideGap + SideW) * 2;   // 자동 읽기 + 추천 기준
        int y0 = Header + 52;                     // 결정 환율 줄 + 구분선 아래
        footerY = S(y0 + RowH * n + 6);
        winH = Math.Max(footerY + S(62), S(SideH));
        Hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_CONTROLPARENT | WS_EX_TOOLWINDOW_ | WS_EX_LAYERED, cls, "시세 입력",
            WS_POPUP | WS_CLIPCHILDREN, 0, 0, winW, winH, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        SetLayeredWindowAttributes(Hwnd, 0, 246, 2);                         // 살짝 투명
        // 둥근 모서리 두 칸(시세 입력 + 오른쪽 아래 자동 읽기)을 합친 모양
        var rgn = CreateRoundRectRgn(0, 0, mainW + 1, winH + 1, S(28), S(28));
        var (sx0, sy0, sw0, sh0) = SideRect;
        var side = CreateRoundRectRgn(sx0, sy0, sx0 + sw0 + 1, sy0 + sh0 + 1, S(28), S(28));
        CombineRgn(rgn, rgn, side, 2 /*RGN_OR*/);
        DeleteObject(side);
        var (tx0, ty0, tw0, th0) = Side2Rect;
        var side2 = CreateRoundRectRgn(tx0, ty0, tx0 + tw0 + 1, ty0 + th0 + 1, S(28), S(28));
        CombineRgn(rgn, rgn, side2, 2 /*RGN_OR*/);
        DeleteObject(side2);
        SetWindowRgn(Hwnd, rgn, true);
        SetWindowDisplayAffinity(Hwnd, 0x11);                                // 화면 캡처(OCR)에 안 찍히게

        loading = true;
        rows.Clear();
        rateEdit = FieldEdit(Parser.FmtPrice(cfg.HuiseokPrice), S(FieldX), S(Header + 6), ID_RATE);
        NumericEdit.Attach(rateEdit);
        {
            var (mx, my, _, _) = Side2Rect;
            minEdit = FieldEdit(Parser.FmtPrice(cfg.RecommendMin), mx + S(22), my + S(48), ID_MIN, S(90), "0 = 전부");
            NumericEdit.Attach(minEdit);
        }
        int y = y0, i = 0;
        foreach (var it in items) AddRow(it.Name, it.Price is double p ? Parser.FmtPrice(p) : "", it, i++, editableName: false, ref y);
        foreach (var name in extra) AddRow(name, "", null, i++, editableName: true, ref y);
        AddRow("", "", null, i++, editableName: true, ref y);
        loading = false;
        status = shop ? "[적용] 하면 반영하고 접힘 · ✕ 는 취소" : "고친 뒤 [입력완료] 또는 Enter";
    }

    void AddRow(string name, string price, ItemDef? orig, int i, bool editableName, ref int y)
    {
        var r = new Row { Label = name, Orig = orig, Y = y };
        if (editableName) r.NameEdit = FieldEdit(name, S(18), S(y), ID_NAME + i, S(148), NameCue);   // 오른쪽에 ✕ 자리
        r.PriceEdit = FieldEdit(price, S(FieldX), S(y), ID_PRICE + i, 0, PriceCue);
        NumericEdit.Attach(r.PriceEdit);
        rows.Add(r);
        y += RowH;
    }

    /// <summary>테두리 없는 EDIT 를 칸 배경 안쪽에 넣음 (배경은 WM_PAINT 에서 그림).</summary>
    const string NameCue = "아이템 이름 똑같이", PriceCue = "시세 없음";

    IntPtr FieldEdit(string text, int x, int y, int id, int w = 0, string? cue = null)
    {
        if (w == 0) w = S(FieldW);
        int eh = S(20);
        var e = CreateWindowExW(0, "EDIT", text, WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
            x + S(10), y + (S(FieldH) - eh) / 2, w - S(20), eh, Hwnd, new IntPtr(id), IntPtr.Zero, IntPtr.Zero);
        SendMessageW(e, WM_SETFONT, font, new IntPtr(1));
        // 비어 있으면 회색 안내 문구, 한 글자라도 입력하면 사라짐 (EM_SETCUEBANNER, 입력 중에도 표시)
        if (cue != null) SendMessageStr(e, 0x1501, new IntPtr(1), cue);
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

    /// <summary>입력칸은 '신력의 휘석 1개 = 몇 결정'. 계산에는 결정 1개 = 몇 휘석(역수)을 씀.</summary>
    double RateInput() =>
        PriceInput.TryParse(Text(rateEdit), out var v) && v is double d && d > 0 ? 1 / d : PriceInput.CrystalPrice(cfg);

    // ---------------- 그리기 ----------------
    void Paint()
    {
        var dc = BeginPaint(Hwnd, out var ps);
        var mem = CreateCompatibleDC(dc);
        var bmp = CreateCompatibleBitmap(dc, winW, winH);
        var old = SelectObject(mem, bmp);
        using (var c = Canvas.OnDc(mem)) DrawEditor(c, withTexts: false);
        BitBlt(dc, 0, 0, winW, winH, mem, 0, 0, SRCCOPY);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem);
        EndPaint(Hwnd, ref ps);
    }

    /// <summary>시세 창 그리기. withTexts = 입력칸 글자도 직접 그림 (화면 그대로 저장용, 평소엔 입력칸이 스스로 그림)</summary>
    void DrawEditor(Canvas c, bool withTexts)
    {
        c.Clear(Bg);
        c.StrokeRound(0.5, 0.5, mainW - 1, winH - 1, S(14), Border, 1);
        DrawSpeed(c);
        DrawMin(c, withTexts);
        // 머리
        c.FillRect(S(18), S(20), S(4), S(20), Gold);
        c.Text(S(30), S(15), "시세 입력", S(20), true, Txt);
        var (tw, _) = c.Measure("시세 입력", S(20), true);
        c.Text(S(30) + tw + S(10), S(22), "1개당 결정 (숫자만)", S(13), false, Dim);
        {
            var (x, y, w, h) = CloseRect;
            c.Line(x + S(6), y + S(6), x + w - S(6), y + h - S(6), Muted, 1.6);
            c.Line(x + w - S(6), y + S(6), x + S(6), y + h - S(6), Muted, 1.6);
        }
        // 결정 환율
        int ry = S(Header + 6);
        c.Text(S(20), ry + S(6), "신력의 휘석 1개 =", S(15), false, Muted);
        FieldBg(c, S(FieldX), ry, S(FieldW), Text(rateEdit), true);
        c.Text(S(FieldX + FieldW + 12), ry + S(7), "결정", S(13), false, Dim);
        c.Line(S(18), S(Header + 44), mainW - S(18), S(Header + 44), Line_);
        // 항목
        double rate = RateInput();
        foreach (var r in rows)
        {
            int y = S(r.Y);
            if (r.NameEdit != IntPtr.Zero)
            {
                c.FillRound(S(18), y, S(148), S(FieldH), S(8), Field);
                if (withTexts && Text(r.NameEdit).Length == 0) c.Text(S(28), y + S(6), NameCue, S(15), false, Dim);   // 화면 저장용 (평소엔 입력칸이 직접 보여줌)
            }
            else c.Text(S(20), y + S(6), r.Label, S(15), false, r.Deleted ? Dim : Txt);
            if (HasName(r))
            {
                var (dx, dy, dw, dh) = DelRect(r);
                if (r.Deleted)
                {
                    c.Line(S(20), y + S(FieldH) / 2.0, S(166), y + S(FieldH) / 2.0, Warn, 1.4);   // 취소선
                    c.Text(dx + S(2), dy - S(1), "↺", S(14), true, Gold);
                }
                else
                {
                    c.Line(dx + S(4), dy + S(4), dx + dw - S(4), dy + dh - S(4), Dim, 1.5);
                    c.Line(dx + dw - S(4), dy + S(4), dx + S(4), dy + dh - S(4), Dim, 1.5);
                }
            }
            if (r.Deleted)
            {
                if (r.NameEdit != IntPtr.Zero) c.Text(S(28), y + S(6), Text(r.NameEdit), S(15), false, Dim);
                c.Text(S(FieldX) + S(10), y + S(6), "지움 (적용 시)", S(13), false, Warn);
                continue;
            }
            string pt = Text(r.PriceEdit);
            FieldBg(c, S(FieldX), y, S(FieldW), withTexts ? pt : "x", false);   // 평소엔 안내 문구를 입력칸이 직접 보여줌
            string res = "";
            uint rc = Dim;
            if (!PriceInput.TryParse(pt, out var v)) { res = "형식 오류"; rc = Warn; }
            else if (v is double d) res = "= " + Parser.FmtPrice(d * rate) + "휘";
            c.Text(S(FieldX + FieldW + 12), y + S(7), res, S(13), false, rc);
        }
        // 아래
        c.Line(S(18), footerY, mainW - S(18), footerY, Line_);
        c.Text(S(20), footerY + S(22), status, S(13), false, dirty ? Canvas.Argb(255, 220, 190, 120) : Dim);
        var (bx, by, bw, bh) = ButtonRect;
        c.FillRound(bx, by, bw, bh, S(10), Gold);
        string label = ShopMode ? "적용" : "입력완료";
        var (lw, lh) = c.Measure(label, S(16), true);
        c.Text(bx + (bw - lw) / 2, by + (bh - lh) / 2, label, S(16), true, GoldText);
        if (withTexts)
        {
            int ey = (S(FieldH) - S(20)) / 2;
            c.Text(S(FieldX) + S(10), S(Header + 6) + ey, Text(rateEdit), S(15), false, Txt);
            foreach (var r in rows)
            {
                if (r.NameEdit != IntPtr.Zero && Text(r.NameEdit).Length > 0) c.Text(S(18) + S(10), S(r.Y) + ey, Text(r.NameEdit), S(15), false, Txt);
                if (Text(r.PriceEdit).Length > 0) c.Text(S(FieldX) + S(10), S(r.Y) + ey, Text(r.PriceEdit), S(15), false, Txt);
            }
        }
    }

    /// <summary>추천 기준 칸: 이득이 몇 결정 이상이면 초록 '구매' 로 보일지 (입력 즉시 적용·저장)</summary>
    void DrawMin(Canvas c, bool withTexts)
    {
        var (sx, sy, sw, sh) = Side2Rect;
        c.FillRound(sx, sy, sw, sh, S(14), Bg);
        c.StrokeRound(sx + 0.5, sy + 0.5, sw - 1, sh - 1, S(14), Border, 1);
        c.FillRect(sx + S(14), sy + S(16), S(4), S(18), Gold);
        c.Text(sx + S(24), sy + S(12), "추천 기준", S(16), true, Txt);
        FieldBg(c, sx + S(22), sy + S(48), S(90), withTexts ? Text(minEdit) : "x", true);
        if (withTexts) c.Text(sx + S(32), sy + S(48) + (S(FieldH) - S(20)) / 2, Text(minEdit), S(15), false, Txt);
        c.Text(sx + S(22) + S(90) + S(8), sy + S(55), "결", S(14), true, Muted);
        bool ok = PriceInput.TryParse(Text(minEdit), out _);
        c.Text(sx + S(14), sy + S(90), ok ? "이득이 이 값 이상인" : "숫자를 넣어 주세요", S(12), false, ok ? Dim : Warn);
        c.Text(sx + S(14), sy + S(108), "칸만 초록 '구매'", S(12), false, Dim);
    }

    /// <summary>자동 읽기 칸: 몇 초마다 모든 칸을 다시 읽을지 (누르면 바로 적용·저장).</summary>
    void DrawSpeed(Canvas c)
    {
        var (sx, sy, sw, sh) = SideRect;
        c.FillRound(sx, sy, sw, sh, S(14), Bg);
        c.StrokeRound(sx + 0.5, sy + 0.5, sw - 1, sh - 1, S(14), Border, 1);
        c.FillRect(sx + S(14), sy + S(16), S(4), S(18), Gold);
        c.Text(sx + S(24), sy + S(12), "자동 읽기", S(16), true, Txt);
        double v = SpeedValue;
        c.Text(sx + S(22), sy + S(44), $"{v:0.0}초마다", S(18), true, Gold);
        var (tx, ty, tw, th) = TrackRect;
        double f = (v - SpeedMin) / (SpeedMax - SpeedMin);
        c.FillRound(tx, ty, tw, th, th / 2.0, Field);
        c.FillRound(tx, ty, Math.Max(th, tw * f), th, th / 2.0, Canvas.WithAlpha(Gold, 200));
        double kx = tx + tw * f, ky = ty + th / 2.0, kr = S(9);
        c.FillRound(kx - kr, ky - kr, kr * 2, kr * 2, kr, Gold);
        c.StrokeRound(kx - kr, ky - kr, kr * 2, kr * 2, kr, Canvas.Argb(255, 30, 22, 0), 1.5);
        c.Text(tx - S(6), ty + S(14), "0.1초", S(11), false, Dim);
        var (mw, _) = c.Measure("1초", S(11), false);
        c.Text(tx + tw - mw + S(4), ty + S(14), "1초", S(11), false, Dim);
        c.Text(sx + S(14), sy + sh - S(24), "짧을수록 빨리 바로잡음", S(12), false, Dim);
    }

    void FieldBg(Canvas c, int x, int y, int w, string text, bool accent)
    {
        c.FillRound(x, y, w, S(FieldH), S(8), Field);
        if (accent) c.StrokeRound(x + 0.5, y + 0.5, w - 1, S(FieldH) - 1, S(8), Canvas.Argb(90, 234, 179, 8), 1);
        if (text.Length == 0 && !accent) c.Text(x + S(10), y + S(6), PriceCue, S(15), false, Dim);
    }

    // ---------------- 적용 ----------------
    void Apply()
    {
        var errors = new List<string>();
        double rate = PriceInput.CrystalPrice(cfg);
        double? huiseok = null;
        if (PriceInput.TryParse(Text(rateEdit), out var rv) && rv is double r0 && r0 > 0) { rate = 1 / r0; huiseok = r0; SetNormalized(rateEdit, r0); }
        else errors.Add("신력의 휘석 시세");

        // 결정·도박 항목은 그대로 두고, 나머지만 입력값으로 바꿈
        var items = cfg.Items.Where(i => i.IsCrystal || i.IsGamble).ToList();
        var normalOld = cfg.Items.Where(i => !i.IsCrystal && !i.IsGamble).ToList();
        var seen = new HashSet<ItemDef>();
        foreach (var r in rows)
        {
            string name = RowName(r);
            if (r.Deleted)
            {
                // 지운 항목: 목록에서 빼고, 기본 항목이면 업데이트 때 되살아나지 않게 기억
                string nm = r.Orig?.Name ?? name;
                if (nm.Length > 0 && !cfg.IsRemoved(nm)) cfg.RemovedItems.Add(nm);
                continue;
            }
            if (name.Length == 0) continue;
            cfg.RemovedItems.RemoveAll(n => n.Replace(" ", "") == name.Replace(" ", ""));   // 지웠던 이름을 다시 넣으면 되살림
            if (!PriceInput.TryParse(Text(r.PriceEdit), out var v))
            {
                errors.Add(name);
                if (r.Orig != null) { items.Add(r.Orig); seen.Add(r.Orig); }
                continue;
            }
            var it = r.Orig ?? new ItemDef { Name = name };
            it.Name = name; it.Price = v;
            SetNormalized(r.PriceEdit, v);
            items.Add(it); seen.Add(it);
        }
        // 창을 연 뒤에 바깥에서 추가된 항목은 지우지 않음
        items.AddRange(normalOld.Where(o => !seen.Contains(o) && !rows.Any(r => r.Orig == o)));

        if (huiseok is double hv) cfg.HuiseokPrice = hv; else cfg.CrystalRate = rate;
        cfg.Items = items;
        try { App.SavePrices(cfg); } catch (Exception e) { errors.Add("저장 실패: " + e.Message); }
        applied();
        dirty = false;
        if (errors.Count == 0)
        {
            if (ShopMode) Collapse(); else Destroy();   // 적용하면 접힘 (상점 안) / 닫힘 (트레이)
            return;
        }
        status = errors.Count == 0 ? $"적용됨 {DateTime.Now:HH:mm:ss}" : "형식 오류는 이전 값 유지: " + string.Join(", ", errors);
        InvalidateRect(Hwnd, IntPtr.Zero, false);
    }

    /// <summary>입력칸 글자를 정리된 값으로 ("0.000" → "0", "0.0500" → "0.05", ".5" → "0.5"). 빈칸은 그대로.</summary>
    void SetNormalized(IntPtr edit, double? v)
    {
        if (edit == IntPtr.Zero || v is not double d) return;
        var norm = Parser.FmtPrice(d);
        if (Text(edit).Trim() == norm) return;
        bool was = loading; loading = true;
        SetWindowTextW(edit, norm);
        loading = was;
    }

    static bool In((int x, int y, int w, int h) r, int px, int py) => px >= r.x && px < r.x + r.w && py >= r.y && py < r.y + r.h;

    /// <summary>Overlay 의 창 프로시저에서 이 창 메시지를 넘겨받음. 처리했으면 결과값, 아니면 null.</summary>
    public IntPtr? Handle(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (h == toggle && toggle != IntPtr.Zero)
        {
            switch ((int)msg)
            {
                case WM_PAINT_: PaintToggle(); return IntPtr.Zero;
                case WM_ERASEBKGND: return new IntPtr(1);
                case WM_LBUTTONUP: Expand(); return IntPtr.Zero;
                case 0x21: return new IntPtr(3);   // WM_MOUSEACTIVATE → MA_NOACTIVATE (게임 포커스 유지)
            }
            return null;
        }
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
                if (p.X < mainW && p.Y < S(Header - 6) && !(ShopMode == false && In(CloseRect, p.X, p.Y))) return new IntPtr(2); // 머리 = 끌어서 이동
                return new IntPtr(1);
            }
            case 0x201: // WM_LBUTTONDOWN: 슬라이더 잡기
            {
                int x = (short)(l.ToInt64() & 0xFFFF), y = (short)((l.ToInt64() >> 16) & 0xFFFF);
                if (In(SliderHit, x, y)) { dragging = true; SetCapture(Hwnd); SetSpeedFromX(x); return IntPtr.Zero; }
                break;
            }
            case 0x200: // WM_MOUSEMOVE: 끄는 중
                if (dragging) { SetSpeedFromX((short)(l.ToInt64() & 0xFFFF)); return IntPtr.Zero; }
                break;
            case WM_LBUTTONUP:
            {
                int x = (short)(l.ToInt64() & 0xFFFF), y = (short)((l.ToInt64() >> 16) & 0xFFFF);
                if (dragging)
                {
                    dragging = false; ReleaseCapture();
                    SetSpeedFromX(x);
                    try { App.SaveSettings(cfg); } catch { }
                    App.Log($"자동 읽기: {cfg.RereadSec:0.0}초마다");
                    return IntPtr.Zero;
                }
                foreach (var r in rows)
                    if (HasName(r) && In(DelRect(r), x, y))
                    {
                        r.Deleted = !r.Deleted;
                        ShowWindow(r.PriceEdit, r.Deleted ? SW_HIDE : SW_SHOW);
                        if (r.NameEdit != IntPtr.Zero) ShowWindow(r.NameEdit, r.Deleted ? SW_HIDE : SW_SHOW);
                        dirty = true;
                        status = ShopMode ? "지울 항목 표시됨 · [적용] 하면 지워짐" : "지울 항목 표시됨 · [입력완료] 하면 지워짐";
                        InvalidateRect(Hwnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    }
                if (In(ButtonRect, x, y)) Apply();
                else if (In(CloseRect, x, y)) { if (ShopMode) Collapse(); else Destroy(); }   // ✕ = 바꾼 값 버리고 접기/닫기
                return IntPtr.Zero;
            }
            case WM_COMMAND_:
            {
                int id = (int)(w.ToInt64() & 0xFFFF), code = (int)((w.ToInt64() >> 16) & 0xFFFF);
                if (code == EN_CHANGE && !loading && id == ID_MIN)
                {
                    // 추천 기준은 입력하는 즉시 적용·저장 (시세처럼 [적용] 필요 없음)
                    if (PriceInput.TryParse(Text(minEdit), out var mv))
                    {
                        cfg.RecommendMin = Math.Max(0, mv ?? 0);
                        try { App.SaveSettings(cfg); } catch { }
                        applied();
                    }
                    InvalidateRect(Hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }
                if (code == EN_CHANGE && !loading)
                {
                    dirty = true;
                    status = ShopMode ? "바뀐 내용 있음 · [적용] 또는 Enter" : "바뀐 내용 있음 · [입력완료] 또는 Enter";
                    InvalidateRect(Hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }
                if (id == IDOK) { Apply(); return IntPtr.Zero; }
                if (id == IDCANCEL) { if (ShopMode) Collapse(); else Destroy(); return IntPtr.Zero; }   // Esc
                break;
            }
            case WM_CLOSE: if (ShopMode) Collapse(); else Destroy(); return IntPtr.Zero;
        }
        return null;
    }
}

using System.Text;
using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>
/// 시세 입력 창.
///  - 상점 모드: 신들의 강복이 열리면 게임 화면 왼쪽 아래에 자동으로 뜸. [적용] 을 눌러도 창은 그대로.
///    상점을 나가면 사라지고, 게임에서 다른 창으로 전환하면 잠시 숨음.
///  - 트레이 모드: 트레이 메뉴/더블클릭/Ctrl+Shift+P 로 열기. [입력완료] 를 누르면 적용하고 닫힘.
/// 입력하는 동안은 오른쪽에 계산 결과만 미리 보여주고, 버튼(또는 Enter)을 눌러야 적용됨.
/// </summary>
sealed class Editor
{
    const int ID_NAME = 1000, ID_PRICE = 2000, ID_RESULT = 3000, ID_TAX = 4001, ID_REFRESH = 4002, ID_STATUS = 4003, ID_APPLY = 4010, IDOK = 1;
    const int EN_CHANGE = 0x300, BN_CLICKED = 0, WM_SETFONT = 0x30, WM_CLOSE = 0x10, WM_COMMAND_ = 0x111;
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x10000, ES_AUTOHSCROLL = 0x80,
        WS_EX_CLIENTEDGE = 0x200, WS_EX_CONTROLPARENT = 0x10000, WS_EX_TOOLWINDOW_ = 0x80, BS_DEFPUSHBUTTON = 0x1;
    const int WS_CAPTION = 0xC00000, WS_SYSMENU = 0x80000;
    const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNOACTIVATE_ = 4, SW_RESTORE = 9;

    readonly Config cfg;
    readonly Func<IEnumerable<string>> unknownNames;
    readonly Action applied;
    string cls = "";
    IntPtr inst;

    public IntPtr Hwnd { get; private set; }
    public bool ShopMode { get; private set; }
    IntPtr font, fontBold;
    readonly List<(IntPtr name, IntPtr price, IntPtr result, ItemDef? orig)> rows = new();
    IntPtr tax, refresh, status, button;
    bool loading, dirty;
    double scale = 1;
    int winW, winH;
    HashSet<string> builtExtra = new();
    (int X, int Y, int W, int H) placedFor;

    public Editor(Config cfg, Func<IEnumerable<string>> unknownNames, Action applied)
    {
        this.cfg = cfg; this.unknownNames = unknownNames; this.applied = applied;
    }

    public void Init(string cls, IntPtr inst) { this.cls = cls; this.inst = inst; }

    int S(double v) => (int)Math.Round(v * scale);
    bool Exists => Hwnd != IntPtr.Zero && IsWindow(Hwnd);

    // ---------------- 열기/닫기 ----------------

    /// <summary>트레이에서 열기: 화면 왼쪽 아래, [입력완료] 누르면 닫힘.</summary>
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
        if (rows.Count > 0) SetFocus(rows[0].price);
    }

    /// <summary>상점이 열려 있고 게임(또는 이 창)이 앞에 있을 때: 게임 화면 왼쪽 아래에 표시.</summary>
    public void ShowInShop((int X, int Y, int W, int H) game)
    {
        var extra = ExtraNames();
        bool focused = Exists && GetForegroundWindow() == Hwnd;
        // 상점에 새로 '모르는 항목'이 보이면 (입력 중이 아닐 때) 줄을 추가해서 다시 만듦
        if (Exists && ShopMode && !focused && !dirty && !extra.SetEquals(builtExtra)) Destroy();
        if (!Exists) { Create(shop: true); placedFor = default; }
        else if (!ShopMode) SetShopMode(true);
        if (placedFor != game)
        {
            placedFor = game;
            int x = game.X + S(16), y = game.Y + game.H - winH - S(16);
            SetWindowPos(Hwnd, HWND_TOPMOST, x, Math.Max(game.Y, y), 0, 0, 0x1 | SWP_NOACTIVATE);
        }
        ShowWindow(Hwnd, SW_SHOWNOACTIVATE_); // 게임 포커스를 뺏지 않음
    }

    /// <summary>게임이 뒤로 갔을 때: 상점 창만 잠시 숨김 (입력 내용 유지).</summary>
    public void HideInShop()
    {
        if (Exists && ShopMode) ShowWindow(Hwnd, SW_HIDE);
    }

    /// <summary>상점을 나갔을 때: 상점 창을 닫음.</summary>
    public void CloseShop()
    {
        if (Exists && ShopMode) Destroy();
    }

    void SetShopMode(bool shop)
    {
        ShopMode = shop;
        SetWindowTextW(Hwnd, shop ? "시세 입력 · 신들의 강복" : "시세 입력");
        SetWindowTextW(button, shop ? "적용" : "입력완료");
    }

    void Destroy()
    {
        if (Exists) DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
        if (font != IntPtr.Zero) { DeleteObject(font); DeleteObject(fontBold); font = fontBold = IntPtr.Zero; }
        rows.Clear();
        dirty = false;
    }

    HashSet<string> ExtraNames()
    {
        var known = new HashSet<string>(cfg.Items.Select(i => Parser.Norm(i.Name)));
        return unknownNames().Select(Parser.Norm).Where(n => n.Length > 0 && !known.Contains(n)).Take(5).ToHashSet();
    }

    // ---------------- 창 만들기 ----------------

    void Create(bool shop)
    {
        scale = GetDpiForSystem() / 96.0;
        font = CreateFontW(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Malgun Gothic");
        fontBold = CreateFontW(-S(14), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Malgun Gothic");

        var items = cfg.Items.ToList();
        builtExtra = ExtraNames();
        var extra = builtExtra.ToList();
        int n = items.Count + extra.Count + 2;

        int rowH = S(27), top = S(52);
        winW = S(430);
        winH = top + rowH * n + S(132);
        Hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_CONTROLPARENT | WS_EX_TOOLWINDOW_, cls, "",
            WS_CAPTION | WS_SYSMENU, 0, 0, winW, winH, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        SetWindowDisplayAffinity(Hwnd, 0x11); // 화면 캡처(OCR)에 안 찍히게

        Static("1개당 휘석.  예) 5.5   40/20 (20개에 40)   20.5결   빈칸=시세 없음", S(10), S(6), S(410), S(18), false);
        Static("항목", S(10), S(28), S(190), S(18), true);
        Static("시세 입력", S(206), S(28), S(100), S(18), true);
        Static("1개당 휘석", S(312), S(28), S(100), S(18), true);

        loading = true;
        rows.Clear();
        int y = top, i = 0;
        foreach (var it in items) AddRow(it.Name, it.Price is double p ? Parser.Fmt(p) : "", it, ref y, i++);
        foreach (var name in extra) AddRow(name, "", null, ref y, i++);
        for (int k = 0; k < 2; k++) AddRow("", "", null, ref y, i++);

        y += S(6);
        Static("판매 수수료(%)", S(10), y + S(4), S(100), S(18), false);
        tax = Edit(Parser.Fmt(cfg.TaxRate * 100), S(110), y, S(50), S(23), ID_TAX);
        Static("새로고침 비용", S(176), y + S(4), S(90), S(18), false);
        refresh = Edit(Parser.Fmt(cfg.RefreshCost), S(266), y, S(50), S(23), ID_REFRESH);
        y += S(32);
        button = CreateWindowExW(0, "BUTTON", "", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
            S(10), y, S(110), S(30), Hwnd, new IntPtr(ID_APPLY), IntPtr.Zero, IntPtr.Zero);
        SendMessageW(button, WM_SETFONT, fontBold, new IntPtr(1));
        status = Static("", S(130), y + S(6), S(290), S(36), false, ID_STATUS);
        loading = false;
        SetShopMode(shop);
        Preview();
    }

    void AddRow(string name, string price, ItemDef? orig, ref int y, int i)
    {
        var ne = Edit(name, S(10), y, S(190), S(24), ID_NAME + i);
        var pe = Edit(price, S(206), y, S(100), S(24), ID_PRICE + i);
        var re = Static("", S(312), y + S(4), S(110), S(18), false, ID_RESULT + i);
        rows.Add((ne, pe, re, orig));
        y += S(27);
    }

    IntPtr Edit(string text, int x, int y, int w, int h, int id)
    {
        var e = CreateWindowExW(WS_EX_CLIENTEDGE, "EDIT", text, WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
            x, y, w, h, Hwnd, new IntPtr(id), IntPtr.Zero, IntPtr.Zero);
        SendMessageW(e, WM_SETFONT, font, new IntPtr(1));
        return e;
    }

    IntPtr Static(string text, int x, int y, int w, int h, bool bold, int id = 0)
    {
        var s = CreateWindowExW(0, "STATIC", text, WS_CHILD | WS_VISIBLE, x, y, w, h, Hwnd, new IntPtr(id), IntPtr.Zero, IntPtr.Zero);
        SendMessageW(s, WM_SETFONT, bold ? fontBold : font, new IntPtr(1));
        return s;
    }

    static string Text(IntPtr h)
    {
        int n = GetWindowTextLengthW(h);
        var sb = new StringBuilder(n + 1);
        GetWindowTextW(h, sb, n + 1);
        return sb.ToString();
    }

    // ---------------- 계산/적용 ----------------

    double CrystalInput()
    {
        foreach (var r in rows)
            if (Parser.Norm(Text(r.name)) == "최초의불꽃결정" && PriceInput.TryParse(Text(r.price), 50, out var cp) && cp is double c)
                return c;
        return 50;
    }

    /// <summary>입력 중: 오른쪽 칸에 계산 결과만 보여줌 (적용 안 함).</summary>
    void Preview()
    {
        double crystal = CrystalInput();
        foreach (var r in rows)
        {
            string name = Text(r.name).Trim();
            if (!PriceInput.TryParse(Text(r.price), crystal, out var v)) SetWindowTextW(r.result, "형식 오류");
            else SetWindowTextW(r.result, v is double d ? "= " + Parser.Fmt(d) : (name.Length > 0 ? "시세 없음" : ""));
        }
        SetWindowTextW(status, dirty
            ? (ShopMode ? "바뀐 내용이 있습니다. [적용] 또는 Enter" : "바뀐 내용이 있습니다. [입력완료] 또는 Enter")
            : (ShopMode ? "고친 뒤 [적용] 을 누르면 바로 반영됩니다." : "고친 뒤 [입력완료] 를 누르면 반영되고 닫힙니다."));
    }

    /// <summary>[적용]/[입력완료]: 설정에 반영하고 저장. 형식이 틀린 칸은 이전 값 유지.</summary>
    void Apply()
    {
        double crystal = CrystalInput();
        var items = new List<ItemDef>();
        var errors = new List<string>();
        foreach (var r in rows)
        {
            string name = Text(r.name).Trim();
            if (!PriceInput.TryParse(Text(r.price), crystal, out var v))
            {
                if (name.Length > 0) { errors.Add(name); if (r.orig != null) items.Add(r.orig); }
                continue;
            }
            if (name.Length == 0) continue;
            items.Add(new ItemDef
            {
                Name = name,
                Price = v,
                Taxed = r.orig?.Taxed ?? Parser.Norm(name) != "최초의불꽃결정",
                Aliases = r.orig?.Aliases ?? new List<string>(),
            });
        }
        bool taxOk = PriceInput.TryParse(Text(tax), 1, out var t) && t is double tv && tv >= 0 && tv < 100;
        bool refOk = PriceInput.TryParse(Text(refresh), 1, out var rf) && rf is double;
        if (!taxOk) errors.Add("판매 수수료");
        if (!refOk) errors.Add("새로고침 비용");

        cfg.Items = items;
        if (taxOk) cfg.TaxRate = t!.Value / 100;
        if (refOk) cfg.RefreshCost = rf!.Value;
        try { cfg.Save(App.ConfigPath); App.ConfigSaved(); } catch (Exception e) { errors.Add("저장 실패: " + e.Message); }
        applied();
        dirty = false;

        if (!ShopMode && errors.Count == 0) { Destroy(); return; }
        // 바뀐 이름(새 항목)을 다음에 다시 만들 때 반영하도록
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var it = items.FirstOrDefault(x => x.Name == Text(r.name).Trim());
            rows[i] = (r.name, r.price, r.result, it ?? r.orig);
        }
        Preview();
        SetWindowTextW(status, errors.Count == 0
            ? $"적용됨 {DateTime.Now:HH:mm:ss}  (항목 {items.Count}개)"
            : "적용됨. 형식 오류는 이전 값 유지: " + string.Join(", ", errors));
    }

    /// <summary>Overlay 의 창 프로시저에서 이 창 메시지를 넘겨받음.</summary>
    public bool Handle(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (h != Hwnd || Hwnd == IntPtr.Zero) return false;
        if (msg == WM_COMMAND_)
        {
            int id = (int)(w.ToInt64() & 0xFFFF), code = (int)((w.ToInt64() >> 16) & 0xFFFF);
            if (code == EN_CHANGE && !loading) { dirty = true; Preview(); return true; }
            if ((id == ID_APPLY && code == BN_CLICKED) || id == IDOK) { Apply(); return true; }
        }
        if (msg == WM_CLOSE) { Destroy(); return true; }
        return false;
    }
}

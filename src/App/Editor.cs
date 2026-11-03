using System.Runtime.InteropServices;
using System.Text;
using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>
/// 시세 입력 창. 칸에 입력하는 즉시 오버레이에 반영되고 config.json 에 저장됨.
/// 열기: 트레이 아이콘 더블클릭 / 트레이 메뉴 / Ctrl+Shift+P
/// </summary>
sealed class Editor
{
    const int ID_NAME = 1000, ID_PRICE = 2000, ID_RESULT = 3000, ID_TAX = 4001, ID_REFRESH = 4002, ID_STATUS = 4003;
    const int EN_CHANGE = 0x300, WM_SETFONT = 0x30, WM_CLOSE = 0x10, WM_COMMAND_ = 0x111;
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x10000, ES_AUTOHSCROLL = 0x80,
        WS_EX_CLIENTEDGE = 0x200, WS_EX_CONTROLPARENT = 0x10000, SS_RIGHT = 0x2;
    const int WS_CAPTION = 0xC00000, WS_SYSMENU = 0x80000, WS_MINIMIZEBOX = 0x20000;

    readonly Config cfg;
    readonly Func<IEnumerable<string>> unknownNames;
    readonly Action applied;
    public IntPtr Hwnd { get; private set; }
    IntPtr font, fontBold;
    readonly List<(IntPtr name, IntPtr price, IntPtr result, ItemDef? orig)> rows = new();
    IntPtr tax, refresh, status;
    bool loading;
    double scale = 1;

    public Editor(Config cfg, Func<IEnumerable<string>> unknownNames, Action applied)
    {
        this.cfg = cfg; this.unknownNames = unknownNames; this.applied = applied;
    }

    int S(double v) => (int)Math.Round(v * scale);

    public void Open(string cls, IntPtr inst)
    {
        if (Hwnd != IntPtr.Zero && IsWindow(Hwnd))
        {
            ShowWindow(Hwnd, 9); // SW_RESTORE
            SetForegroundWindow(Hwnd);
            return;
        }
        scale = GetDpiForSystem() / 96.0;
        font = CreateFontW(-S(15), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Malgun Gothic");
        fontBold = CreateFontW(-S(15), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Malgun Gothic");

        var items = cfg.Items.ToList();
        var known = new HashSet<string>(items.Select(i => Parser.Norm(i.Name)));
        var extra = unknownNames().Select(Parser.Norm).Distinct().Where(n => n.Length > 0 && !known.Contains(n)).Take(5).ToList();
        int n = items.Count + extra.Count + 3;

        int rowH = S(30), top = S(58);
        int w = S(470), h = top + rowH * n + S(120);
        Hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_CONTROLPARENT, cls, "시세 바로 바꾸기 (입력 즉시 반영)",
            WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX, S(20), Math.Max(0, GetSystemMetrics(1) - h - S(70)), w, h, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        SetWindowDisplayAffinity(Hwnd, 0x11); // 화면 캡처(OCR)에 안 찍히게

        Static("1개당 휘석으로 입력.  예) 5.5   40/20 (20개에 40)   20.5결 (결정 단위)   빈칸 = 시세 없음",
            S(12), S(8), S(440), S(20), false);
        Static("항목 이름", S(12), S(32), S(200), S(20), true);
        Static("시세 입력", S(222), S(32), S(110), S(20), true);
        Static("1개당 휘석", S(340), S(32), S(100), S(20), true);

        loading = true;
        rows.Clear();
        int y = top;
        int i = 0;
        foreach (var it in items) AddRow(it.Name, it.Price is double p ? Parser.Fmt(p) : "", it, ref y, i++);
        foreach (var name in extra) AddRow(name, "", null, ref y, i++);
        for (int k = 0; k < 3; k++) AddRow("", "", null, ref y, i++);

        y += S(8);
        Static("판매 수수료(%)", S(12), y + S(4), S(110), S(20), false);
        tax = Edit(Parser.Fmt(cfg.TaxRate * 100), S(122), y, S(60), S(24), ID_TAX);
        Static("새로고침 비용", S(200), y + S(4), S(100), S(20), false);
        refresh = Edit(Parser.Fmt(cfg.RefreshCost), S(300), y, S(60), S(24), ID_REFRESH);
        y += S(34);
        status = Static("", S(12), y, S(440), S(40), false, ID_STATUS);
        loading = false;
        Recalc(save: false);

        ShowWindow(Hwnd, 5);
        SetForegroundWindow(Hwnd);
        if (rows.Count > 0) SetFocus(rows[0].price);
    }

    void AddRow(string name, string price, ItemDef? orig, ref int y, int i)
    {
        var ne = Edit(name, S(12), y, S(200), S(26), ID_NAME + i);
        var pe = Edit(price, S(222), y, S(110), S(26), ID_PRICE + i);
        var re = Static("", S(340), y + S(4), S(110), S(20), false, ID_RESULT + i);
        rows.Add((ne, pe, re, orig));
        y += S(30);
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

    /// <summary>입력값을 읽어 설정에 적용. 잘못된 칸이 있으면 그 칸만 빼고 적용.</summary>
    void Recalc(bool save)
    {
        // 결정 시세를 먼저 알아야 "결" 단위를 계산할 수 있음
        double crystal = 50;
        foreach (var r in rows)
            if (Parser.Norm(Text(r.name)) == "최초의불꽃결정" && PriceInput.TryParse(Text(r.price), 50, out var cp) && cp is double c)
                crystal = c;

        var items = new List<ItemDef>();
        var errors = new List<string>();
        foreach (var r in rows)
        {
            string name = Text(r.name).Trim();
            string ptxt = Text(r.price);
            if (!PriceInput.TryParse(ptxt, crystal, out var v))
            {
                SetWindowTextW(r.result, "형식 오류");
                if (name.Length > 0) { errors.Add(name); if (r.orig != null) items.Add(r.orig); }
                continue;
            }
            SetWindowTextW(r.result, v is double d ? "= " + Parser.Fmt(d) : (name.Length > 0 ? "시세 없음" : ""));
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

        if (!save) { SetWindowTextW(status, "입력하는 즉시 오버레이에 반영되고 저장됩니다."); return; }

        cfg.Items = items;
        if (taxOk) cfg.TaxRate = t!.Value / 100;
        if (refOk) cfg.RefreshCost = rf!.Value;
        try { cfg.Save(App.ConfigPath); App.ConfigSaved(); } catch (Exception e) { errors.Add("저장 실패: " + e.Message); }
        applied();
        SetWindowTextW(status, errors.Count == 0
            ? $"반영됨 {DateTime.Now:HH:mm:ss}  (항목 {items.Count}개)"
            : "반영됨, 단 형식 오류는 이전 값 유지: " + string.Join(", ", errors));
    }

    /// <summary>Overlay 의 창 프로시저에서 이 창 메시지를 넘겨받음.</summary>
    public bool Handle(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (h != Hwnd) return false;
        if (msg == WM_COMMAND_ && ((w.ToInt64() >> 16) & 0xFFFF) == EN_CHANGE && !loading)
        {
            Recalc(save: true);
            return true;
        }
        if (msg == WM_CLOSE)
        {
            DestroyWindow(Hwnd);
            Hwnd = IntPtr.Zero;
            DeleteObject(font); DeleteObject(fontBold);
            return true;
        }
        return false;
    }
}

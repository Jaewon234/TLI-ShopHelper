using System.Runtime.InteropServices;
using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>
/// 게임 창 위에 겹치는 투명 창 (클릭 통과, 캡처 제외) + 트레이 아이콘.
/// Win32/GDI 만 사용.
/// </summary>
sealed class Overlay
{
    const uint MSG_UPDATE = WM_APP + 1, MSG_TRAY = WM_APP + 2, MSG_NOTIFY = WM_APP + 3, MSG_PANEL = WM_APP + 4;
    const uint KEY = 0x010101;
    static readonly uint Green = Rgb(46, 204, 113), Red = Rgb(231, 76, 60), Gray = Rgb(149, 165, 166),
        Yellow = Rgb(241, 196, 15), White = Rgb(230, 230, 230), PanelBg = Rgb(27, 27, 27);

    readonly Config cfg;
    public Engine? Engine;
    Editor? editor;
    IntPtr inst;
    public IntPtr EditorHwnd => editor?.Hwnd ?? IntPtr.Zero;
    readonly WndProc proc;   // GC 방지
    IntPtr hwnd, msgWnd, keyBrush, panelBrush, icon;
    readonly object gate = new();
    (int X, int Y, int W, int H) rect, placed;
    Snapshot? snap;
    bool visible;
    string notifyTitle = "", notifyText = "";
    readonly Dictionary<int, IntPtr> fonts = new();

    public Overlay(Config cfg)
    {
        this.cfg = cfg;
        proc = WndProcImpl;
    }

    // ---------------- 엔진 스레드에서 호출 ----------------
    public bool RectChanged((int, int, int, int) r) { lock (gate) return r != rect; }

    public void Show((int X, int Y, int W, int H)? r, Snapshot s)
    {
        lock (gate) { if (r != null) rect = r.Value; snap = s; visible = true; }
        PostMessageW(hwnd, MSG_UPDATE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Hide()
    {
        lock (gate) visible = false;
        PostMessageW(hwnd, MSG_UPDATE, IntPtr.Zero, IntPtr.Zero);
    }

    (PanelState st, (int X, int Y, int W, int H) r, int ver) panelWant, panelSent = ((PanelState)(-1), default, -1);

    /// <summary>상점 안 시세 창 상태 (엔진 스레드). 바뀔 때만 UI 스레드로 보냄.</summary>
    public void Panel(PanelState st, (int X, int Y, int W, int H) r, int ver)
    {
        lock (gate)
        {
            panelWant = (st, r, ver);
            if (panelWant == panelSent) return;
            panelSent = panelWant;
        }
        PostMessageW(msgWnd, MSG_PANEL, IntPtr.Zero, IntPtr.Zero);
    }

    public void Notify(string title, string text)
    {
        lock (gate) { notifyTitle = title; notifyText = text; }
        PostMessageW(msgWnd, MSG_NOTIFY, IntPtr.Zero, IntPtr.Zero);
    }

    // ---------------- UI 스레드 ----------------
    public void Run(Action onReady)
    {
        inst = GetModuleHandleW(null);
        // exe 에 넣은 아이콘 (작은 것 = 트레이, 큰 것 = 창). 못 읽으면 기본 아이콘
        IntPtr big = IntPtr.Zero, small = IntPtr.Zero;
        try { if (Environment.ProcessPath is string exe) ExtractIconExW(exe, 0, out big, out small, 1); } catch { }
        icon = small != IntPtr.Zero ? small : LoadIconW(IntPtr.Zero, new IntPtr(32516));
        var bigIcon = big != IntPtr.Zero ? big : icon;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = proc, hInstance = inst,
            lpszClassName = "ShopHelperOverlay", hIcon = bigIcon, hIconSm = icon,
        };
        RegisterClassExW(ref wc);
        var ec = wc; ec.lpszClassName = "ShopHelperEditor"; ec.hbrBackground = new IntPtr(16); // COLOR_BTNFACE+1
        ec.hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512));
        RegisterClassExW(ref ec);
        keyBrush = CreateSolidBrush(KEY);
        panelBrush = CreateSolidBrush(PanelBg);

        hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            wc.lpszClassName, "ShopHelper", WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        App.Log($"오버레이 창 {(hwnd != IntPtr.Zero ? "생성됨" : "생성 실패 (오류 " + Marshal.GetLastWin32Error() + ")")} · 아이콘 {(small != IntPtr.Zero ? "exe" : "기본")}");
        SetLayeredWindowAttributes(hwnd, KEY, 0, LWA_COLORKEY);
        if (!SetWindowDisplayAffinity(hwnd, 0x11)) App.Log("캡처 제외 설정 실패 (Windows 10 2004 이상 필요)");
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);

        msgWnd = CreateWindowExW(WS_EX_TOOLWINDOW, wc.lpszClassName, "ShopHelperTray", WS_POPUP, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        AddTray();
        App.Log("트레이 아이콘 등록");
        if (!RegisterHotKey(msgWnd, 1, 0x2 | 0x4 | 0x4000, 0x50)) App.Log("단축키 Ctrl+Shift+P 등록 실패 (다른 프로그램이 사용 중)");
        editor = new Editor(cfg, () => Engine?.UnknownNames() ?? new List<string>(), () => Engine?.Reevaluate());
        editor.Init("ShopHelperEditor", inst);
        onReady();

        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            var eh = EditorHwnd;
            if (eh != IntPtr.Zero && IsDialogMessageW(eh, ref m)) continue; // Tab 키 이동
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
        App.Log("종료 (메시지 루프 끝)");
        RemoveTray();
    }

    NOTIFYICONDATA TrayData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = msgWnd, uID = 1,
        uFlags = 0x1 | 0x2 | 0x4, uCallbackMessage = MSG_TRAY, hIcon = icon,
        szTip = "신들의 강복 도우미", szInfo = "", szInfoTitle = "",
    };

    void AddTray() { var d = TrayData(); Shell_NotifyIconW(0, ref d); }
    void RemoveTray() { var d = TrayData(); Shell_NotifyIconW(2, ref d); }

    void ShowBalloon()
    {
        var d = TrayData();
        lock (gate) { d.szInfoTitle = notifyTitle; d.szInfo = notifyText; }
        d.uFlags |= 0x10; // NIF_INFO
        d.dwInfoFlags = 1;
        Shell_NotifyIconW(1, ref d);
    }

    void TrayMenu()
    {
        var menu = CreatePopupMenu();
        const uint CHECKED = 0x8, GRAYED = 0x1, SEP = 0x800;
        AppendMenuW(menu, GRAYED, UIntPtr.Zero, Engine?.LogInfo ?? "");
        AppendMenuW(menu, SEP, UIntPtr.Zero, null);
        AppendMenuW(menu, 0, (UIntPtr)7, "시세 입력...  (Ctrl+Shift+P)");
        AppendMenuW(menu, SEP, UIntPtr.Zero, null);
        AppendMenuW(menu, cfg.Resell ? CHECKED : 0, (UIntPtr)2, "결정화 기준 (팔아서 결정, 수수료 반영)");
        AppendMenuW(menu, cfg.Resell ? 0 : CHECKED, (UIntPtr)1, "직접 사용 기준 (시세)");
        AppendMenuW(menu, SEP, UIntPtr.Zero, null);
        AppendMenuW(menu, 0, (UIntPtr)3, "config.json 열기 (메모장, 저장하면 자동 반영)");
        AppendMenuW(menu, 0, (UIntPtr)5, "현재 화면 저장 (문제 확인용)");
        AppendMenuW(menu, 0, (UIntPtr)6, "프로그램 로그 열기");
        AppendMenuW(menu, SEP, UIntPtr.Zero, null);
        AppendMenuW(menu, 0, (UIntPtr)9, "종료");
        GetCursorPos(out var p);
        SetForegroundWindow(msgWnd);
        int cmd = TrackPopupMenu(menu, 0x100 | 0x2, p.X, p.Y, 0, msgWnd, IntPtr.Zero);
        DestroyMenu(menu);
        switch (cmd)
        {
            case 1: SetMode("use"); break;
            case 2: SetMode("resell"); break;
            case 3: ShellExecuteW(IntPtr.Zero, "open", "notepad.exe", $"\"{App.ConfigPath}\"", null, 1); break;
            case 7: OpenEditor(); break;
            case 5: Engine?.RequestSave(); break;
            case 6: ShellExecuteW(IntPtr.Zero, "open", "notepad.exe", $"\"{App.LogPath}\"", null, 1); break;
            case 9: PostQuitMessage(0); break;
        }
    }

    void SetMode(string mode)
    {
        cfg.Mode = mode;
        try { cfg.Save(App.ConfigPath); } catch { }
        Engine?.Reevaluate();
    }

    void OpenEditor()
    {
        try { editor?.OpenTray(); }
        catch (Exception e) { App.Log("시세 창 오류: " + e); }
    }

    /// <summary>엔진 스레드에서 호출: 바깥에서 고친 config.json 을 다시 읽음.</summary>
    public bool ReloadFromDisk()
    {
        try
        {
            var n = Config.Load(App.ConfigPath);
            cfg.Items = n.Items; cfg.Mode = n.Mode; cfg.TaxRate = n.TaxRate; cfg.RefreshCost = n.RefreshCost;
            Engine?.Reevaluate();
            Notify("시세 설정 반영됨", $"config.json 변경 감지 (항목 {n.Items.Count}개)");
            return true;
        }
        catch (Exception e) { App.Log("config.json 오류: " + e.Message); return false; }
    }

    IntPtr WndProcImpl(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            if (editor != null && editor.Handle(h, msg, w, l)) return IntPtr.Zero;
            switch (msg)
            {
                case 0x312: // WM_HOTKEY
                    OpenEditor();
                    return IntPtr.Zero;
                case MSG_UPDATE:
                    (int X, int Y, int W, int H) r;
                    lock (gate) r = rect;
                    if (r != placed && r.W > 0)
                    {
                        placed = r;
                        SetWindowPos(hwnd, HWND_TOPMOST, r.X, r.Y, r.W, r.H, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    }
                    else SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | 0x1 | 0x2); // 맨 위 유지
                    InvalidateRect(hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                case MSG_TRAY:
                    int ev = (int)(l.ToInt64() & 0xFFFF);
                    if (ev == WM_LBUTTONDBLCLK) OpenEditor();
                    else if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU) TrayMenu();
                    return IntPtr.Zero;
                case MSG_PANEL:
                    (PanelState st, (int X, int Y, int W, int H) pr, int ver) want;
                    lock (gate) want = panelWant;
                    if (editor != null)
                    {
                        if (want.st == PanelState.Visible) editor.ShowInShop(want.pr);
                        else if (want.st == PanelState.Hidden) editor.HideInShop();
                        else editor.CloseShop();
                    }
                    return IntPtr.Zero;
                case MSG_NOTIFY:
                    ShowBalloon();
                    return IntPtr.Zero;
                case WM_PAINT:
                    if (h == hwnd) { Paint(); return IntPtr.Zero; }
                    break;
            }
        }
        catch (Exception e) { App.Log("UI 오류: " + e); }
        return DefWindowProcW(h, msg, w, l);
    }

    // ---------------- 그리기 ----------------
    IntPtr Font(int px)
    {
        px = Math.Clamp(px, 9, 60);
        if (!fonts.TryGetValue(px, out var f))
            fonts[px] = f = CreateFontW(-px, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, "Malgun Gothic");
        return f;
    }

    void Paint()
    {
        var dc = BeginPaint(hwnd, out var ps);
        (int X, int Y, int W, int H) r; Snapshot? s; bool vis;
        lock (gate) { r = placed; s = snap; vis = visible; }
        int w = Math.Max(1, r.W), h = Math.Max(1, r.H);
        var mem = CreateCompatibleDC(dc);
        var bmp = CreateCompatibleBitmap(dc, w, h);
        var oldBmp = SelectObject(mem, bmp);
        var all = new RECT { Right = w, Bottom = h };
        FillRect(mem, ref all, keyBrush);
        if (vis && s != null) Draw(mem, s, w, h);
        BitBlt(dc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
        SelectObject(mem, oldBmp);
        DeleteObject(bmp);
        DeleteDC(mem);
        EndPaint(hwnd, ref ps);
    }

    void Frame(IntPtr dc, Box b, uint color, int width)
    {
        var pen = CreatePen(0, width, color);
        var op = SelectObject(dc, pen);
        var ob = SelectObject(dc, GetStockObject(5)); // NULL_BRUSH
        Native.Rectangle(dc, (int)b.X1, (int)b.Y1, (int)b.X2, (int)b.Y2);
        SelectObject(dc, ob); SelectObject(dc, op);
        DeleteObject(pen);
    }

    /// <summary>글자 상자. anchor: 0=위 가운데, 1=아래 가운데, 2=오른쪽 위</summary>
    void Label(IntPtr dc, double x, double y, string text, uint color, int px, int anchor = 0)
    {
        SelectObject(dc, Font(px));
        var m = new RECT();
        DrawTextW(dc, text, -1, ref m, 0x400 | 0x800); // CALCRECT | NOPREFIX
        int tw = m.Right - m.Left, th = m.Bottom - m.Top, pad = Math.Max(3, px / 4);
        int left = anchor == 2 ? (int)x - tw - pad * 2 : (int)(x - tw / 2.0) - pad;
        int top = anchor == 1 ? (int)y - th - pad * 2 : (int)y;
        var bg = new RECT { Left = left, Top = top, Right = left + tw + pad * 2, Bottom = top + th + pad * 2 };
        FillRect(dc, ref bg, panelBrush);
        Frame(dc, new Box(bg.Left, bg.Top, bg.Right, bg.Bottom), color, 1);
        SetBkMode(dc, 1);
        SetTextColor(dc, color);
        var tr = new RECT { Left = left + pad, Top = top + pad, Right = left + pad + tw, Bottom = top + pad + th };
        DrawTextW(dc, text, -1, ref tr, 0x800);
    }

    void Draw(IntPtr dc, Snapshot s, int w, int h)
    {
        int basePx = s.Slots.Count > 0 ? (int)(s.Slots[0].R.H / 5.4) : Math.Max(12, h / 70);
        basePx = Math.Max(11, basePx);
        double crystal = PriceInput.CrystalPrice(cfg);
        string Gain(double huiseok) => (huiseok > 0 ? "+" : "") + (huiseok / crystal).ToString("0.##") + "결";

        foreach (var sl in s.Slots)
        {
            var c = sl.Card;
            var d = sl.D;
            if (sl.Pending) { Frame(dc, d, Yellow, 1); continue; }
            if (c == null)
            {
                Frame(dc, d, Gray, 1);
                Label(dc, d.Cx, d.Y1 - 4, "읽기 실패", Gray, basePx, 1);
                continue;
            }
            switch (c.Status)
            {
                case Verdict.Good:
                    // 사야 할 칸: 굵은 초록 테두리 + 위쪽에 "구매 +N결"
                    Frame(dc, d, Green, Math.Max(4, basePx / 3));
                    Frame(dc, new Box(d.X1 + 5, d.Y1 + 5, d.X2 - 5, d.Y2 - 5), Green, 1);
                    Label(dc, d.Cx, d.Y1 - 4, "구매 " + Gain(c.Diff!.Value), Green, basePx + 4, 1);
                    break;
                case Verdict.Bad:
                    Frame(dc, d, Red, 1);
                    Label(dc, d.Cx, d.Y1 - 4, Gain(c.Diff!.Value), Red, basePx - 1, 1);
                    break;
                default:
                    Frame(dc, d, Gray, 1);
                    Label(dc, d.Cx, d.Y1 - 4, c.Item == null ? "모르는 항목" : "시세 없음", Gray, basePx - 1, 1);
                    break;
            }
        }

        var good = s.Slots.Where(x => x.Card?.Status == Verdict.Good).ToList();
        var lines = new List<string>
        {
            $"이 화면 이득 {Gain(s.PageGain)}  (구매 {good.Count}칸)  [{(cfg.Resell ? "결정화 기준" : "직접 사용 기준")}]",
        };
        if (s.Pages > 0)
            lines.Add($"본 화면 {s.Pages}개 · 평균 이득 {Gain(s.SumGain / s.Pages)} / 새로고침 {(cfg.RefreshCost / crystal):0.##}결");
        if (s.LatencyMs is double ms) lines.Add($"반영 {ms:0}ms");
        if (s.Status.Length > 0) lines.Add(s.Status);
        string text = string.Join("\n", lines);
        if (s.Refresh is Box rb)
            Label(dc, rb.Cx, rb.Y1 - rb.H * 3.5, text, Yellow, basePx + 1, 1);
        else
            Label(dc, w - 16, h * 0.07, text, Yellow, basePx + 1, 2);
    }
}

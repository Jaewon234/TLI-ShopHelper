using System.Runtime.InteropServices;
using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>
/// 게임 창 위에 겹치는 투명 창 (클릭 통과, 캡처 제외) + 트레이 아이콘.
/// Win32/GDI 만 사용.
/// </summary>
sealed class Overlay
{
    const uint MSG_UPDATE = WM_APP + 1, MSG_TRAY = WM_APP + 2, MSG_NOTIFY = WM_APP + 3;
    const uint KEY = 0x010101;
    static readonly uint Green = Rgb(46, 204, 113), Red = Rgb(231, 76, 60), Gray = Rgb(149, 165, 166),
        Yellow = Rgb(241, 196, 15), White = Rgb(230, 230, 230), Panel = Rgb(27, 27, 27);

    readonly Config cfg;
    public Engine? Engine;
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

    public void Notify(string title, string text)
    {
        lock (gate) { notifyTitle = title; notifyText = text; }
        PostMessageW(msgWnd, MSG_NOTIFY, IntPtr.Zero, IntPtr.Zero);
    }

    // ---------------- UI 스레드 ----------------
    public void Run(Action onReady)
    {
        var inst = GetModuleHandleW(null);
        icon = LoadIconW(IntPtr.Zero, new IntPtr(32516)); // IDI_INFORMATION
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = proc, hInstance = inst,
            lpszClassName = "ShopHelperOverlay", hIcon = icon, hIconSm = icon,
        };
        RegisterClassExW(ref wc);
        keyBrush = CreateSolidBrush(KEY);
        panelBrush = CreateSolidBrush(Panel);

        hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            wc.lpszClassName, "ShopHelper", WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        SetLayeredWindowAttributes(hwnd, KEY, 0, LWA_COLORKEY);
        if (!SetWindowDisplayAffinity(hwnd, 0x11)) App.Log("캡처 제외 설정 실패 (Windows 10 2004 이상 필요)");
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);

        msgWnd = CreateWindowExW(WS_EX_TOOLWINDOW, wc.lpszClassName, "ShopHelperTray", WS_POPUP, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        AddTray();
        onReady();

        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
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
        AppendMenuW(menu, cfg.Resell ? 0 : CHECKED, (UIntPtr)1, "직접 사용 기준 (시세)");
        AppendMenuW(menu, cfg.Resell ? CHECKED : 0, (UIntPtr)2, "되팔기 기준 (세금 반영)");
        AppendMenuW(menu, SEP, UIntPtr.Zero, null);
        AppendMenuW(menu, 0, (UIntPtr)3, "시세 설정 열기 (config.json)");
        AppendMenuW(menu, 0, (UIntPtr)4, "시세 설정 다시 읽기");
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
            case 4: ReloadConfig(); break;
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

    void ReloadConfig()
    {
        try
        {
            var n = Config.Load(App.ConfigPath);
            cfg.Items = n.Items; cfg.Mode = n.Mode; cfg.TaxRate = n.TaxRate; cfg.RefreshCost = n.RefreshCost;
            Engine?.Reevaluate();
            lock (gate) { notifyTitle = "시세 설정을 다시 읽었습니다"; notifyText = $"항목 {n.Items.Count}개"; }
            ShowBalloon();
        }
        catch (Exception e) { App.Error("config.json 오류:\n" + e.Message); }
    }

    IntPtr WndProcImpl(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            switch (msg)
            {
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
                    if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU || ev == WM_LBUTTONDBLCLK) TrayMenu();
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
        foreach (var sl in s.Slots)
        {
            var c = sl.Card;
            if (sl.Pending)
            {
                Frame(dc, sl.R, Yellow, 1);
                continue;
            }
            if (c == null)
            {
                Frame(dc, sl.R, Gray, 2);
                Label(dc, sl.R.Cx, sl.R.Y2 + 4, "읽기 실패", Gray, basePx);
                continue;
            }
            uint col = c.Status switch { Verdict.Good => Green, Verdict.Bad => Red, _ => Gray };
            Frame(dc, sl.R, col, c.Status == Verdict.Good ? 4 : 2);
            string main = c.Status == Verdict.Unknown
                ? (c.Item == null ? "모르는 항목" : "시세 없음")
                : (c.Diff > 0 ? "+" : "") + Parser.Fmt(c.Diff);
            Label(dc, sl.R.Cx, sl.R.Y2 + 4, main, col, basePx + 3);
            if (c.Status != Verdict.Unknown)
                Label(dc, sl.R.Cx, sl.R.Y2 + 4 + (basePx + 3) * 1.9, "가치 " + Parser.Fmt(c.Value), White, basePx);
        }

        var good = s.Slots.Where(x => x.Card?.Status == Verdict.Good).ToList();
        var lines = new List<string>
        {
            $"이 화면 이득 +{Parser.Fmt(s.PageGain)}  ({good.Count}칸)  [{(cfg.Resell ? "되팔기" : "직접사용")}]",
        };
        if (s.Pages > 0)
            lines.Add($"본 화면 {s.Pages}개 · 평균 이득 {Parser.Fmt(Math.Round(s.SumGain / s.Pages, 1))} / 새로고침 {Parser.Fmt(cfg.RefreshCost)}");
        if (s.LatencyMs is double ms) lines.Add($"반영 {ms:0}ms");
        if (s.Status.Length > 0) lines.Add(s.Status);
        string text = string.Join("\n", lines);
        if (s.Refresh is Box rb)
            Label(dc, rb.Cx, rb.Y1 - rb.H * 3.5, text, Yellow, basePx + 1, 1);
        else
            Label(dc, w - 16, h * 0.07, text, Yellow, basePx + 1, 2);
    }
}

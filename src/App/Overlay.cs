using System.Runtime.InteropServices;
using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>
/// 게임 창 위에 겹치는 투명 창 (클릭 통과, 캡처 제외) + 트레이 아이콘.
/// Win32/GDI 만 사용.
/// </summary>
sealed class Overlay
{
    const uint MSG_UPDATE = WM_APP + 1, MSG_TRAY = WM_APP + 2, MSG_NOTIFY = WM_APP + 3, MSG_PANEL = WM_APP + 4, MSG_QUIT = WM_APP + 5, MSG_INSTALL = WM_APP + 6, MSG_SAVE = WM_APP + 7;
    // 색 (ARGB)
    static readonly uint Green = Canvas.Argb(255, 34, 197, 94), Red = Canvas.Argb(255, 239, 68, 68),
        Amber = Canvas.Argb(255, 245, 158, 11), Muted = Canvas.Argb(255, 150, 155, 165), Dim = Canvas.Argb(255, 110, 115, 125),
        Txt = Canvas.Argb(255, 232, 234, 238), Gold = Canvas.Argb(255, 234, 179, 8);

    readonly Config cfg;
    public Engine? Engine;
    Editor? editor;
    IntPtr inst;
    public IntPtr EditorHwnd => editor?.Hwnd ?? IntPtr.Zero;
    volatile Updater.Release? update;
    public string? UpdatedFrom, UpdatedTag;
    /// <summary>상점이 열려 있는지 (엔진이 알려줌). 화면 저장 때 오버레이가 잠깐 숨겨져 있어도 그리기 위해</summary>
    public volatile bool ShopOpen;
    volatile bool popupOpen;
    /// <summary>상점 안 다른 창(구매 창 등)이 열렸는지. 바뀌었으면 true.</summary>
    public bool SetPopup(bool open) { if (popupOpen == open) return false; popupOpen = open; return true; }
    readonly ProgressWindow progress = new();
    volatile bool installing;
    /// <summary>오버레이가 화면 캡처에서 빠졌는지 (null = 아직 확인 전).</summary>
    public volatile object? CaptureExcludedBox;
    public bool? CaptureExcluded { get => CaptureExcludedBox as bool?; private set => CaptureExcludedBox = value; }
    readonly WndProc proc;   // GC 방지
    IntPtr hwnd, msgWnd, icon;
    IntPtr memDc, dib, dibBits, oldBmp;
    int dibW, dibH;
    readonly object gate = new();
    (int X, int Y, int W, int H) rect, placed;
    Snapshot? snap;
    bool visible;
    string notifyTitle = "", notifyText = "";

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
        Canvas.Startup();
        var ec = wc; ec.lpszClassName = "ShopHelperEditor"; ec.hbrBackground = CreateSolidBrush(Rgb(14, 16, 22));
        ec.hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512));
        RegisterClassExW(ref ec);
        var pc = ec; pc.lpszClassName = "ShopHelperProgress";
        RegisterClassExW(ref pc);

        hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            wc.lpszClassName, "ShopHelper", WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        App.Log($"오버레이 창 {(hwnd != IntPtr.Zero ? "생성됨" : "생성 실패 (오류 " + Marshal.GetLastWin32Error() + ")")} · 아이콘 {(small != IntPtr.Zero ? "exe" : "기본")}");
        SetWindowDisplayAffinity(hwnd, 0x11); // 처음 그린 뒤 다시 걸고 확인함 (Render)
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);

        msgWnd = CreateWindowExW(WS_EX_TOOLWINDOW, wc.lpszClassName, "ShopHelperTray", WS_POPUP, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        AddTray();
        App.Log("트레이 아이콘 등록");
        if (!RegisterHotKey(msgWnd, 1, 0x2 | 0x4 | 0x4000, 0x50)) App.Log("단축키 Ctrl+Shift+P 등록 실패 (다른 프로그램이 사용 중)");
        if (!RegisterHotKey(msgWnd, 2, 0x2 | 0x4 | 0x4000, 0x53)) App.Log("단축키 Ctrl+Shift+S 등록 실패 (다른 프로그램이 사용 중)");
        editor = new Editor(cfg, () => Engine?.UnknownNames() ?? new List<string>(), () => Engine?.Reevaluate());
        editor.Init("ShopHelperEditor", inst);
        onReady();
        if (UpdatedFrom != null)
        {
            App.Log($"업데이트 완료: v{UpdatedFrom} → v{App.Version}");
            // 소리 없는 완료 창 (업데이트 진행 창과 같은 모양), 1.5초 뒤 저절로 닫힘
            progress.Show("ShopHelperProgress", inst, $"업데이트 v{UpdatedFrom} → v{App.Version}");
            bool notNewer = Version.TryParse(UpdatedFrom, out var fromV) && Updater.Current <= fromV;
            if (notNewer && UpdatedTag != null)
            {
                // 태그는 더 높은데 실제 버전은 그대로/낮음 (태그를 잘못 붙인 릴리스) → 다시 권하지 않게 기억
                cfg.SkipUpdateTag = UpdatedTag;
                try { App.SaveSettings(cfg); } catch { }
                App.Log($"릴리스 {UpdatedTag} 는 실제 버전이 v{App.Version} → 다음부터 건너뜀");
                progress.Set("버전이 오르지 않았습니다", $"{UpdatedTag} 는 실제로 v{App.Version} 입니다. 다음부터 건너뜁니다.", 1, done: true);
            }
            else progress.Set("업데이트 완료", "시세는 그대로 유지됩니다.", 1, done: true);
            var ph = progress.Hwnd;
            new Thread(() => { Thread.Sleep(1500); PostMessageW(ph, 0x10 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero); }) { IsBackground = true }.Start();
        }
        // 업데이트 확인: 켠 뒤 10초, 그 뒤 6시간마다
        new Thread(() =>
        {
            Thread.Sleep(10_000);
            while (true)
            {
                if (cfg.CheckUpdates) CheckUpdate(manual: false);
                Thread.Sleep(TimeSpan.FromHours(6));
            }
        }) { IsBackground = true, Name = "update" }.Start();

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
        uFlags = 0x1 | 0x2 | 0x4 | 0x80 /*NIF_SHOWTIP: 버전 4 에서 툴팁 표시*/, uCallbackMessage = MSG_TRAY, hIcon = icon,
        szTip = "신들의 강복 도우미", szInfo = "", szInfoTitle = "",
    };

    void AddTray()
    {
        var d = TrayData();
        Shell_NotifyIconW(0, ref d);              // NIM_ADD
        d.uVersion = 4;                            // NOTIFYICON_VERSION_4: 오른쪽 클릭을 WM_CONTEXTMENU 한 번으로 받음
        Shell_NotifyIconW(4, ref d);              // NIM_SETVERSION
    }
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
        AppendMenuW(menu, 0, (UIntPtr)11, "지금 화면 그대로 저장  (Ctrl+Shift+S)");
        AppendMenuW(menu, 0, (UIntPtr)5, "현재 화면 저장 (문제 확인용)");
        AppendMenuW(menu, SEP, UIntPtr.Zero, null);
        var up = update;
        AppendMenuW(menu, installing ? GRAYED : 0, (UIntPtr)8,
            installing ? "업데이트 받는 중..." : up != null ? $"업데이트: {up.Tag} 설치 (지금 v{App.Version})" : $"업데이트 확인 (지금 v{App.Version})");
        AppendMenuW(menu, cfg.CheckUpdates ? CHECKED : 0, (UIntPtr)10, "자동으로 업데이트 확인");
        AppendMenuW(menu, SEP, UIntPtr.Zero, null);
        AppendMenuW(menu, 0, (UIntPtr)9, "종료");
        GetCursorPos(out var p);
        SetForegroundWindow(msgWnd);
        // 작업 표시줄과 겹치지 않게: 마우스가 있는 모니터의 작업 영역(작업 표시줄 제외) 안쪽 끝에 붙여서 띄움
        uint flags = 0x100 | 0x2 | 0x4000;   // TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NOANIMATION (바로 뜸)
        int mx = p.X, my = p.Y;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfoW(MonitorFromPoint(p, 2 /*NEAREST*/), ref mi))
        {
            var wa = mi.rcWork;
            if (p.Y >= wa.Bottom) { my = wa.Bottom - 6; flags |= 0x20; }          // 작업 표시줄이 아래: 메뉴 아래 끝을 그 위에 (TPM_BOTTOMALIGN)
            else if (p.Y < wa.Top) { my = wa.Top + 6; }                          // 위
            if (p.X >= wa.Right) { mx = wa.Right - 6; flags |= 0x8; }            // 오른쪽 (TPM_RIGHTALIGN)
            else if (p.X < wa.Left) { mx = wa.Left + 6; }                        // 왼쪽
            else if (p.X > (wa.Left + wa.Right) / 2) flags |= 0x8;              // 화면 오른쪽 절반이면 메뉴를 왼쪽으로 펼침
        }
        int cmd = TrackPopupMenu(menu, flags, mx, my, 0, msgWnd, IntPtr.Zero);
        PostMessageW(msgWnd, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);   // 메뉴 밖을 눌렀을 때 바로 닫히고 클릭이 씹히지 않게 (Windows 권장)
        DestroyMenu(menu);
        switch (cmd)
        {
            case 7: OpenEditor(); break;
            case 5: Engine?.RequestSave(); break;
            case 11: SaveAsSeenLater(); break;   // 메뉴가 완전히 사라진 뒤에 찍음
            case 8: if (update != null) StartInstall(); else new Thread(() => CheckUpdate(manual: true)) { IsBackground = true }.Start(); break;
            case 10: cfg.CheckUpdates = !cfg.CheckUpdates; try { App.SaveSettings(cfg); } catch { } break;
            case 9: PostQuitMessage(0); break;
        }
    }

    // ---------------- 업데이트 ----------------
    void CheckUpdate(bool manual)
    {
        try
        {
            var r = Updater.Latest(cfg.UpdateRepo).GetAwaiter().GetResult();
            App.Log($"업데이트 확인: 최신 {r.Tag}, 지금 v{App.Version}");
            if (r.Tag == cfg.SkipUpdateTag && r.Tag.Length > 0)
            {
                App.Log($"업데이트 확인: {r.Tag} 는 예전에 받아 보니 실제 버전이 오르지 않아 건너뜀");
                if (manual) Info($"최신 릴리스 {r.Tag} 는 실제로는 새 버전이 아니라서 건너뜁니다.\n(태그를 잘못 붙인 릴리스일 수 있어요)\n\n지금 v{App.Version}");
            }
            else if (r.Ver > Updater.Current)
            {
                update = r;
                PostMessageW(hwnd, MSG_UPDATE, IntPtr.Zero, IntPtr.Zero);   // 이익 카드에 '새 버전' 표시
                if (manual)
                {
                    // 직접 누른 확인은 알림 대신 창으로 (알림이 꺼져 있어도 보이게)
                    if (Ask($"새 버전 {r.Tag} 이 있습니다. (지금 v{App.Version})\n\n지금 설치할까요? 시세는 그대로 유지됩니다."))
                        PostMessageW(msgWnd, MSG_INSTALL, IntPtr.Zero, IntPtr.Zero);
                }
                else Notify($"새 버전 {r.Tag} 이 있습니다", "이 알림을 누르거나 트레이 메뉴 > 업데이트 를 누르세요. 시세는 그대로 유지됩니다.");
            }
            else if (manual) Info($"최신 버전입니다.\n\n지금 v{App.Version} · GitHub 최신 {r.Tag}");
        }
        catch (Exception e)
        {
            App.Log("업데이트 확인 실패: " + e.Message);
            if (manual)
                Info("업데이트를 확인하지 못했습니다.\n\n" + e.Message +
                     $"\n\n확인하는 곳: github.com/{cfg.UpdateRepo}\n(저장소가 비공개면 로그인 없이 볼 수 없어 확인이 안 됩니다)");
        }
    }

    static void Info(string text) => MessageBoxW(IntPtr.Zero, text, "신들의 강복 도우미 · 업데이트", 0x40 | 0x40000 | 0x10000);
    static bool Ask(string text) => MessageBoxW(IntPtr.Zero, text, "신들의 강복 도우미 · 업데이트", 0x4 | 0x20 | 0x40000 | 0x10000) == 6;

    void StartInstall()
    {
        var r = update;
        if (r == null || installing) return;
        if (!Updater.IsReleaseInstall)
        {
            Notify("자동 업데이트를 할 수 없는 위치입니다", "릴리스 zip 을 푼 폴더에서 실행할 때만 됩니다. 릴리스 페이지에서 직접 받아 주세요.");
            if (r.Page.Length > 0) ShellExecuteW(IntPtr.Zero, "open", r.Page, null, null, 1);
            return;
        }
        installing = true;
        progress.Show("ShopHelperProgress", inst, $"업데이트 v{App.Version} → {r.Tag}");
        new Thread(() =>
        {
            try
            {
                progress.Set("받는 중...", "", 0);
                string dir = Updater.Download(r,
                    (got, total) => progress.Set("받는 중...", $"{got / 1048576.0:0.0} / {total / 1048576.0:0.0} MB ({(total > 0 ? got * 100 / total : 0)}%)",
                        total > 0 ? (double)got / total : -1),
                    st => progress.Set(st, "", 1)).GetAwaiter().GetResult();
                App.Log($"업데이트 {r.Tag} 받음 → {dir}");
                progress.Set("설치 준비 완료 · 다시 시작합니다", "잠시 뒤 새 버전이 켜지면 '업데이트 완료' 창이 뜹니다.", 1, done: true);
                Thread.Sleep(1200);
                Updater.Launch(dir, r.Tag);
                PostMessageW(msgWnd, MSG_QUIT, IntPtr.Zero, IntPtr.Zero);   // 새 버전이 이 프로그램이 끝나길 기다림
            }
            catch (Exception e)
            {
                App.Log("업데이트 실패: " + e);
                installing = false;
                progress.Set("업데이트 실패", e.Message, -1, failed: true);
            }
        }) { IsBackground = true, Name = "install" }.Start();
    }

    /// <summary>
    /// 지금 화면 그대로 저장: 게임 화면 + 오버레이 + 시세 창을 직접 합쳐서 PNG 로.
    /// (오버레이와 시세 창은 OCR 에 섞이지 않게 화면 캡처에서 빠져 있어서, 일반 캡처 도구로는 안 찍힘)
    /// </summary>
    void SaveAsSeenLater() =>
        new Thread(() => { Thread.Sleep(250); PostMessageW(msgWnd, MSG_SAVE, IntPtr.Zero, IntPtr.Zero); }) { IsBackground = true }.Start();

    void SaveAsSeen()
    {
        try
        {
            (int X, int Y, int W, int H) r; bool vis;
            lock (gate) { r = placed; vis = visible; }
            if (r.W <= 0)
            {
                var gh = GameWindow.Find(cfg);
                if (GameWindow.ClientRect(gh) is { } gr) r = gr;
            }
            if (r.W <= 0) { Notify("저장 실패", "게임 창을 찾지 못했습니다."); return; }
            using var g = new Grabber();
            var f = g.Grab(r.X, r.Y, r.W, r.H);
            var outF = new Frame((byte[])f.Px.Clone(), f.W, f.H);
            // 1) 오버레이 (미리 곱한 알파로 겹치기)
            Snapshot? sn; lock (gate) sn = snap;
            if ((vis || ShopOpen) && sn != null && r.W > 0)
            {
                // 지금 보이는 결과를 다시 그려서 합침 (상점을 닫은 뒤의 예전 결과는 그리지 않음)
                var ov = new byte[r.W * r.H * 4];
                var hbits = Marshal.AllocHGlobal(ov.Length);
                try
                {
                    unsafe { new Span<byte>((void*)hbits, ov.Length).Clear(); }
                    using (var c = Canvas.OnBits(hbits, r.W, r.H)) { c.Clear(0); Draw(c, sn, r); }
                    Marshal.Copy(hbits, ov, 0, ov.Length);
                }
                finally { Marshal.FreeHGlobal(hbits); }
                var px = outF.Px;
                for (int i = 0; i < ov.Length; i += 4)
                {
                    int a = ov[i + 3];
                    if (a == 0) continue;
                    for (int c = 0; c < 3; c++) px[i + c] = (byte)Math.Min(255, ov[i + c] + px[i + c] * (255 - a) / 255);
                }
            }
            // 2) 시세 창 / [시세 입력] 버튼
            if (editor != null)
                foreach (var (wf, wnd) in editor.RenderVisible())
                {
                    GetWindowRect(wnd, out var wr);
                    int ww = wf.W, wh = wf.H;
                    int ox = wr.Left - r.X, oy = wr.Top - r.Y;
                    var shape = CreateRectRgn(0, 0, 0, 0);
                    bool hasShape = GetWindowRgn(wnd, shape) != 0;   // 둥근 모서리 모양 (없으면 사각형 전체)
                    for (int y = 0; y < wh; y++)
                    {
                        int ty = oy + y; if (ty < 0 || ty >= r.H) continue;
                        for (int x = 0; x < ww; x++)
                        {
                            int tx = ox + x; if (tx < 0 || tx >= r.W) continue;
                            int so = (y * ww + x) * 4, to = (ty * r.W + tx) * 4;
                            if (hasShape && !PtInRegion(shape, x, y)) continue;   // 둥근 모서리 바깥은 건너뜀
                            outF.Px[to] = wf.Px[so]; outF.Px[to + 1] = wf.Px[so + 1]; outF.Px[to + 2] = wf.Px[so + 2];
                        }
                    }
                    DeleteObject(shape);
                }
            string path = Path.Combine(App.Dir, $"screen_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            ImageFile.SavePng(outF, path);
            App.Log("지금 화면 그대로 저장: " + path);
            ShellExecuteW(IntPtr.Zero, "open", "explorer.exe", $"/select,\"{path}\"", null, 1);
        }
        catch (Exception e) { App.Log("화면 저장 실패: " + e); Notify("저장 실패", e.Message); }
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
            var n = App.LoadConfig();
            cfg.Items = n.Items; cfg.Mode = n.Mode; cfg.TaxRate = n.TaxRate; cfg.RefreshCost = n.RefreshCost; cfg.CrystalRate = n.CrystalRate; cfg.RereadSec = n.RereadSec;
            Engine?.Reevaluate();
            Notify("시세 설정 반영됨", $"파일 변경 감지 (항목 {n.Items.Count}개)");
            return true;
        }
        catch (Exception e) { App.Log("config.json 오류: " + e.Message); return false; }
    }

    IntPtr WndProcImpl(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            if (editor?.Handle(h, msg, w, l) is IntPtr handled) return handled;
            if (progress.Handle(h, msg, w, l) is IntPtr handled2) return handled2;
            switch (msg)
            {
                case 0x312: // WM_HOTKEY
                    if (w.ToInt64() == 2) SaveAsSeen(); else OpenEditor();
                    return IntPtr.Zero;
                case MSG_SAVE:
                    SaveAsSeen();
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
                    Render();
                    return IntPtr.Zero;
                case MSG_TRAY:
                    int ev = (int)(l.ToInt64() & 0xFFFF);
                    if (ev == 0x405 /*NIN_BALLOONUSERCLICK*/ && update != null && !installing) StartInstall();
                    else if (ev == WM_LBUTTONDBLCLK) OpenEditor();
                    else if (ev == WM_CONTEXTMENU) TrayMenu();   // 버전 4: 오른쪽 클릭 한 번 = WM_CONTEXTMENU 한 번
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
                    Render(); // 이익 카드를 시세 창 위로 옮김
                    return IntPtr.Zero;
                case MSG_INSTALL:
                    StartInstall();
                    return IntPtr.Zero;
                case MSG_QUIT:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                case MSG_NOTIFY:
                    ShowBalloon();
                    return IntPtr.Zero;
            }
        }
        catch (Exception e) { App.Log("UI 오류: " + e); }
        return DefWindowProcW(h, msg, w, l);
    }

    // ---------------- 그리기 (반투명, UpdateLayeredWindow) ----------------
    void EnsureDib(int w, int h)
    {
        if (w == dibW && h == dibH && dib != IntPtr.Zero) return;
        if (memDc != IntPtr.Zero) { SelectObject(memDc, oldBmp); DeleteDC(memDc); }
        if (dib != IntPtr.Zero) DeleteObject(dib);
        var scr = GetDC(IntPtr.Zero);
        memDc = CreateCompatibleDC(scr);
        ReleaseDC(IntPtr.Zero, scr);
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        dib = CreateDIBSection(memDc, ref bi, 0, out dibBits, IntPtr.Zero, 0);
        oldBmp = SelectObject(memDc, dib);
        dibW = w; dibH = h;
    }

    void Render()
    {
        (int X, int Y, int W, int H) r; Snapshot? s; bool vis;
        lock (gate) { r = placed; s = snap; vis = visible; }
        if (r.W <= 0 || r.H <= 0) return;
        EnsureDib(r.W, r.H);
        using (var c = Canvas.OnBits(dibBits, r.W, r.H))
        {
            c.Clear(0);
            if (vis && s != null) Draw(c, s, r);
        }
        var dst = new POINT { X = r.X, Y = r.Y };
        var size = new SIZE { cx = r.W, cy = r.H };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        var scr = GetDC(IntPtr.Zero);
        UpdateLayeredWindow(hwnd, scr, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
        ReleaseDC(IntPtr.Zero, scr);
        if (CaptureExcluded == null)
        {
            // 오버레이가 화면 캡처(OCR)에 찍히면 자기 글자를 상품으로 읽음 → 실제로 빠졌는지 확인
            SetWindowDisplayAffinity(hwnd, 0x11);
            bool ok = GetWindowDisplayAffinity(hwnd, out uint a) && a == 0x11;
            CaptureExcluded = ok;
            App.Log(ok ? "오버레이 캡처 제외: 적용됨" : $"오버레이 캡처 제외: 실패 (값 {a}) → 게임 창 직접 캡처(PrintWindow) 사용");
        }
    }

    string Gain(double huiseok)
    {
        double v = huiseok / PriceInput.CrystalPrice(cfg);
        return (v > 0 ? "+" : "") + v.ToString("0.##") + "결";
    }

    void Draw(Canvas c, Snapshot s, (int X, int Y, int W, int H) client)
    {
        // 글자 크기는 카드 글자 높이에 맞춤 (UI 크기 따라감)
        double th = s.Slots.Count > 0 ? s.Slots[0].R.H / 4.8 : Math.Max(14, client.H / 60.0);
        th = Math.Clamp(th, 11, 40);
        double big = th * 1.15, small = th * 0.78, tiny = th * 0.62;
        var darkBg = Canvas.Argb(225, 12, 14, 20);

        foreach (var sl in s.Slots)
        {
            var d = sl.D; var cd = sl.Card;
            double rr = th * 0.45;
            if (sl.Pending && cd == null) { c.StrokeRound(d.X1, d.Y1, d.W, d.H, rr, Canvas.WithAlpha(Gold, 120), 1.5); continue; }
            // 다시 읽는 중에도 이전 결과를 그대로 그림 (사라졌다 나타나며 번쩍이지 않게)
            if (cd == null)
            {
                c.Pill(d.Cx, d.Y1, "읽기 실패", small, true, darkBg, Muted);
                continue;
            }
            switch (cd.Status)
            {
                case Verdict.Sold:
                case Verdict.Skip:
                    break; // 매진(게임이 이미 표시) / 도박(판정 안 함)
                case Verdict.Good:
                    c.Glow(d.X1, d.Y1, d.W, d.H, rr, Green, (int)Math.Max(6, th * 0.5));
                    c.StrokeRound(d.X1, d.Y1, d.W, d.H, rr, Green, Math.Max(3, th * 0.18));
                    c.Pill(d.Cx, d.Y1, "구매  " + Gain(cd.Diff!.Value), big, true, Green, Canvas.Argb(255, 6, 30, 14));
                    if (cd.Item?.IsGamble == true)
                        c.Pill(d.Cx, d.Y1 + big * 1.6, GambleLine(cd), tiny, true, Canvas.Argb(215, 6, 30, 14), Canvas.Argb(255, 190, 240, 205));
                    break;
                case Verdict.Gamble:
                    c.StrokeRound(d.X1, d.Y1, d.W, d.H, rr, Canvas.WithAlpha(Amber, 230), 2);
                    c.Pill(d.Cx, d.Y1, Efficiency(cd), small, true, Canvas.Argb(235, 40, 28, 6), Amber);
                    c.Pill(d.Cx, d.Y1 + small * 1.75, GambleLine(cd), tiny, true, Canvas.Argb(215, 40, 28, 6), Canvas.Argb(255, 220, 190, 120));
                    break;
                case Verdict.Bad:
                    c.FillRound(d.X1, d.Y1, d.W, d.H, rr, Canvas.Argb(140, 8, 10, 16));   // 손해 칸은 눌러줌
                    c.Pill(d.Cx, d.Y1, Gain(cd.Diff!.Value), small, true, Canvas.Argb(225, 30, 12, 14), Red);
                    break;
                default:
                    c.FillRound(d.X1, d.Y1, d.W, d.H, rr, Canvas.Argb(110, 8, 10, 16));
                    c.Pill(d.Cx, d.Y1, cd.Item == null ? "모르는 항목" : "시세 없음", small, true, Canvas.Argb(225, 30, 32, 38), Muted);
                    break;
            }
        }
    }

    string Efficiency(Card cd)
    {
        if (cd.Value is not double v || cd.Price <= 0) return "도박 · 범위 미입력";
        return $"도박 · 효율 {v / cd.Price * 100:0}%";
    }

    string GambleLine(Card cd)
    {
        if (cd.Diff is not double d) return "알려주시면 계산";
        string range = cd.RangeLo is double lo && cd.RangeHi is double hi ? $" · {Gain(lo)} ~ {Gain(hi)}" : "";
        return $"기대 {Gain(d)}{range}";
    }

}

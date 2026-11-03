using System.Diagnostics;
using System.Runtime.InteropServices;
using static ShopHelper.Native;

namespace ShopHelper;

static class App
{
    public static readonly string Dir = AppContext.BaseDirectory;
    /// <summary>
    /// 시세 설정은 프로그램 폴더 밖(%AppData%\ShopHelper)에 저장.
    /// 새 버전 zip 을 같은 폴더에 풀어도 입력한 시세가 덮어써지지 않음.
    /// </summary>
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShopHelper");
    /// <summary>시세만 (업데이트해도 절대 바뀌지 않음)</summary>
    public static readonly string PricesPath = Path.Combine(DataDir, "prices.json");
    /// <summary>그 밖의 설정</summary>
    public static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");
    static readonly string[] LegacyConfigs = { Path.Combine(DataDir, "config.json"), Path.Combine(Dir, "config.json") };

    public static Config LoadConfig()
    {
        Directory.CreateDirectory(DataDir);
        var c = Config.LoadSplit(SettingsPath, PricesPath, LegacyConfigs);
        ConfigSaved();
        return c;
    }

    public static void SavePrices(Config c) { c.SavePrices(PricesPath); ConfigSaved(); }
    public static void SaveSettings(Config c) { c.SaveSettings(SettingsPath); ConfigSaved(); }

    public static readonly string LogPath = Path.Combine(Dir, "helper.log");
    static readonly object logLock = new();

    public static void Log(string msg)
    {
        lock (logLock)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}"); } catch { }
        }
    }

    static (DateTime, DateTime) savedAt;
    static (DateTime, DateTime) Stamp() => (File.GetLastWriteTimeUtc(PricesPath), File.GetLastWriteTimeUtc(SettingsPath));
    public static void ConfigSaved() { try { savedAt = Stamp(); } catch { } }
    /// <summary>메모장 등으로 prices.json / settings.json 을 바꿨는지 (우리가 저장한 건 제외).</summary>
    public static bool ConfigChangedOutside()
    {
        try
        {
            var t = Stamp();
            if (t == savedAt) return false;
            savedAt = t; return true;
        }
        catch { return false; }
    }

    public static void Error(string msg) => MessageBoxW(IntPtr.Zero, msg, "신들의 강복 도우미", 0x10);

    public const string Version = "1.6.14";

    [STAThread]
    static int Main(string[] args)
    {
        try { File.WriteAllText(LogPath, ""); } catch { }
        // 어떤 스레드에서든 처리 못 한 오류가 나면: 로그에 남기고 창으로 알려준 뒤 종료
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log("CRASH: " + e.ExceptionObject);
            Error("오류로 종료됩니다.\n\n" + (e.ExceptionObject as Exception)?.Message +
                  "\n\nShopHelper.exe 옆의 helper.log 내용을 보내주세요.");
        };
        try { return Run(args); }
        catch (Exception e)
        {
            Log("CRASH(main): " + e);
            Error("오류로 종료됩니다.\n\n" + e.Message + "\n\nShopHelper.exe 옆의 helper.log 내용을 보내주세요.");
            return 1;
        }
    }

    static int Run(string[] args)
    {
        Log($"시작 v{Version} · {Environment.OSVersion} · {Environment.ProcessPath}");
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // Per-monitor v2
        // 자동 업데이트: 새 버전이 임시 폴더에서 이 옵션으로 실행되어 설치 폴더에 자기를 복사
        if (args.Length >= 3 && args[0] == "--install-update" && int.TryParse(args[2], out int oldPid))
            return Updater.InstallHere(args[1], oldPid, args.Length >= 4 ? args[3] : null, args.Length >= 5 ? args[4] : null);
        string? updatedFrom = args.Length >= 2 && args[0] == "--updated-from" ? args[1] : null;
        string? updatedTag = args.Length >= 3 && args[0] == "--updated-from" ? args[2] : null;
        Updater.Cleanup();
        Config cfg;
        try { cfg = LoadConfig(); Log("시세 파일: " + PricesPath); }
        catch (Exception e) { Error("시세/설정 파일을 읽을 수 없습니다:\n" + e.Message + "\n\n" + DataDir); return 1; }

        Log("설정 읽음 · 항목 " + cfg.Items.Count + "개");
        IOcr ocr;
        try
        {
            var stack = new OcrStack(cfg);
            ocr = stack;
            Log("OCR: " + stack.Describe);
        }
        catch (Exception e) { Log("OCR 오류: " + e); Error("OCR 을 준비하지 못했습니다:\n" + e.Message); return 1; }

        // 스크린샷을 exe 위로 끌어다 놓거나 --image 파일
        string? image = args.Length >= 2 && args[0] == "--image" ? args[1]
            : args.Length == 1 && File.Exists(args[0]) ? args[0] : null;
        if (image != null)
        {
            try
            {
                var f = ImageFile.Load(image);
                var report = Diagnose.Run(f, ocr, cfg, Path.ChangeExtension(Path.GetFullPath(image), null) + "_result");
                MessageBoxW(IntPtr.Zero, report, "인식 결과 (txt/png로도 저장됨)", 0x40);
            }
            catch (Exception e) { Error("이미지 테스트 실패:\n" + e); }
            return 0;
        }

        using var single = new Mutex(true, "ShopHelper_TLI_GodsBlessing", out bool first);
        if (!first) { Error("이미 실행 중입니다. (작업 표시줄 오른쪽 트레이 아이콘 확인)"); return 0; }

        ConfigSaved();
        var overlay = new Overlay(cfg) { UpdatedFrom = updatedFrom, UpdatedTag = updatedTag };
        var engine = new Engine(cfg, ocr, overlay);
        overlay.Engine = engine;
        overlay.Run(engine.Start); // 창을 만든 뒤 엔진 시작
        return 0;
    }
}

enum PanelState { Off, Hidden, Visible }

/// <summary>화면 감시 + 추적 스레드.</summary>
sealed class Engine
{
    readonly Config cfg;
    readonly IOcr ocr;
    readonly Overlay overlay;
    readonly Tracker tracker;
    readonly Stopwatch sw = Stopwatch.StartNew();
    readonly AutoResetEvent wake = new(false);
    volatile bool shopOpen, openedNow, reevaluate, saveRequested;
    public volatile string LogInfo = "로그 찾는 중";

    public Engine(Config cfg, IOcr ocr, Overlay overlay)
    {
        this.cfg = cfg; this.ocr = ocr; this.overlay = overlay;
        tracker = new Tracker(ocr, cfg) { Log = App.Log };
    }

    public void Start() => new Thread(Run) { IsBackground = true, Name = "engine" }.Start();
    public void Reevaluate() { reevaluate = true; wake.Set(); }
    volatile List<string> unknown = new();
    int unknownVer;
    public List<string> UnknownNames() => unknown;
    public void RequestSave() { saveRequested = true; wake.Set(); }

    readonly HashSet<string> popups = new();
    /// <summary>상점 안에서 다른 창(구매 창 등)이 열려 있는지: 열려 있으면 왼쪽 패널을 숨김.</summary>
    bool PopupOpen { get { lock (popups) return popups.Count > 0; } }

    void OnView(string name, bool open)
    {
        lock (popups) { if (open) popups.Add(name); else popups.Remove(name); }
        App.Log($"상점 안 창 {(open ? "열림" : "닫힘")}: {name}");
        wake.Set();
    }

    void OnShop(bool open)
    {
        lock (popups) popups.Clear();
        App.Log(open ? "상점 열림" : "상점 닫힘");
        shopOpen = open;
        overlay.ShopOpen = open;
        if (open) openedNow = true;
        wake.Set();
    }

    void FindLogLoop()
    {
        while (true)
        {
            var (path, how) = GameWindow.FindLog(cfg);
            if (path != null)
            {
                App.Log($"로그: {path} ({how})");
                LogInfo = $"로그 연결됨 ({how})";
                overlay.Notify("신들의 강복 도우미 실행 중", $"로그를 찾았습니다 ({how}).\n상점을 열면 자동으로 켜집니다.");
                new LogWatcher(path, OnShop) { OnView = OnView }.Start();
                return;
            }
            LogInfo = "로그를 못 찾음 · 게임을 켜면 다시 찾습니다";
            Thread.Sleep(3000);
        }
    }

    static readonly uint MyPid = (uint)Environment.ProcessId;
    /// <summary>앞에 있는 창이 이 프로그램 자신의 창인지 (트레이 메뉴, 시세 창, 버튼 …)</summary>
    static bool IsOurWindow(IntPtr w)
    {
        GetWindowThreadProcessId(w, out uint pid);
        if (pid == MyPid) return true;
        // 트레이 아이콘을 누르면 작업 표시줄이 먼저 앞으로 옴 → 게임에서 떠난 게 아님
        var sb = new System.Text.StringBuilder(64);
        GetClassNameW(w, sb, 64);
        return sb.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    void Run()
    {
        // 엔진에서 오류가 나도 프로그램이 꺼지지 않게: 로그에 남기고 다시 시작
        while (true)
        {
            try { Loop(); }
            catch (Exception e) { App.Log("엔진 오류 (1초 뒤 다시 시작): " + e); Thread.Sleep(1000); }
        }
    }

    bool logThreadStarted;

    void Loop()
    {
        if (!logThreadStarted)
        {
            logThreadStarted = true;
            new Thread(() => { try { FindLogLoop(); } catch (Exception e) { App.Log("로그 검색 오류: " + e); } }) { IsBackground = true }.Start();
        }
        using var grab = new Grabber();
        IntPtr hwnd = IntPtr.Zero;
        double lastFind = -1e9;
        bool shown = false;
        bool printWindowBroken = false;
        IntPtr curHwnd = IntPtr.Zero; int curW = 0, curH = 0;
        using var cleanGrab = new Grabber();
        tracker.CleanFrame = () =>
        {
            if (overlay.CaptureExcluded == true || printWindowBroken || curHwnd == IntPtr.Zero) return null;
            var cf = cleanGrab.GrabWindow(curHwnd, curW, curH);
            if (cf == null) { printWindowBroken = true; App.Log("PrintWindow 가 검은 화면 → 화면 캡처로 읽음 (오버레이 글자는 OCR 에서 걸러냄)"); }
            return cf;
        };
        double lastCfgCheck = 0;
        while (true)
        {
            double now = sw.Elapsed.TotalMilliseconds;
            if (now - lastCfgCheck > 1000)
            {
                lastCfgCheck = now;
                if (App.ConfigChangedOutside() && overlay.ReloadFromDisk()) App.Log("config.json 변경 감지 → 다시 읽음");
            }
            if (reevaluate) { reevaluate = false; tracker.Reevaluate(); if (shown) overlay.Show(null, tracker.Snap()); }
            if (saveRequested)
            {
                saveRequested = false;
                SaveDiagnostics(hwnd);
            }
            if (!shopOpen)
            {
                overlay.Panel(PanelState.Off, default, 0);
                if (shown) { overlay.Hide(); shown = false; }
                wake.WaitOne(200);
                continue;
            }
            if (openedNow) { openedNow = false; tracker.Reset(now); }
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || now - lastFind > 5000)
            {
                hwnd = GameWindow.Find(cfg); lastFind = now;
            }
            var rect = GameWindow.ClientRect(hwnd);
            var fg = GetForegroundWindow();
            if (rect != null && fg != hwnd && fg != IntPtr.Zero && IsOurWindow(fg))
            {
                // 시세 창·트레이 메뉴 등 이 프로그램 창이 앞에 있을 때: 게임이 뒤로 간 게 아니므로 결과를 계속 보여줌
                overlay.Panel(PopupOpen ? PanelState.Hidden : PanelState.Visible, rect.Value, unknownVer);
                if (!shown) { overlay.Show(rect.Value, tracker.Snap()); shown = true; }
                wake.WaitOne(60);
                continue;
            }
            if (rect == null || fg != hwnd)
            {
                overlay.Panel(PanelState.Hidden, default, 0);
                if (shown) { overlay.Hide(); shown = false; }
                Thread.Sleep(60);
                continue;
            }
            var (x, y, w, h) = rect.Value;
            var rows = tracker.Rows;
            // 오버레이가 캡처에서 빠지면 빠른 화면 캡처, 아니면 게임 창만 직접 받아옴 (오버레이 글자가 섞이지 않게)
            // 감시는 항상 빠른 화면 캡처 (게임을 건드리지 않음, 깜빡임 없음).
            // 글자를 읽을 때만, 오버레이가 캡처에서 안 빠지는 PC 에서는 게임 창을 직접 받아옴 (PrintWindow).
            curHwnd = hwnd; curW = w; curH = h;
            var frame = rows is { } rr ? grab.Grab(x, y, w, h, rr.From, rr.To) : grab.Grab(x, y, w, h);
            bool dirty = tracker.Update(frame, now);
            if (dirty)
            {
                var un = tracker.UnknownNames();
                if (!un.SequenceEqual(unknown)) unknownVer++;
                unknown = un;
            }
            bool pop = PopupOpen;
            overlay.Panel(pop ? PanelState.Hidden : PanelState.Visible, rect.Value, unknownVer);
            if (overlay.SetPopup(pop)) dirty = true;
            if (dirty || !shown || overlay.RectChanged(rect.Value))
            {
                overlay.Show(rect.Value, tracker.Snap());
                shown = true;
            }
            double spent = sw.Elapsed.TotalMilliseconds - now;
            int sleep = (int)Math.Max(1, 25 - spent);
            wake.WaitOne(sleep);
        }
    }

    void SaveDiagnostics(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) hwnd = GameWindow.Find(cfg);
            var rect = GameWindow.ClientRect(hwnd);
            using var g = new Grabber();
            var f = rect is { } r ? g.Grab(r.X, r.Y, r.W, r.H) : null;
            if (f == null) { overlay.Notify("저장 실패", "게임 창을 찾지 못했습니다."); return; }
            string baseName = Path.Combine(App.Dir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}");
            ImageFile.SavePng(f, baseName + ".png");
            Diagnose.Run(f, ocr, cfg, baseName + "_result");
            overlay.Notify("화면 저장됨", Path.GetFileName(baseName) + ".png / _result.txt");
        }
        catch (Exception e) { App.Log("저장 실패: " + e); }
    }
}

/// <summary>스크린샷 한 장으로 인식 결과를 확인 (txt + 칸 표시 png).</summary>
static class Diagnose
{
    /// <summary>
    /// 스크린샷 한 장으로 인식 결과를 확인 (txt + 칸 표시 png).
    /// 실제 오버레이와 같은 과정(처음 전체 읽기 → 못 읽은 칸은 칸 단위로 다시 읽기)을 거침.
    /// </summary>
    public static string Run(Frame f, IOcr ocr, Config cfg, string outBase)
    {
        var sb = new System.Text.StringBuilder();
        var log = new List<string>();
        var sw = Stopwatch.StartNew();
        var tracker = new Tracker(ocr, cfg) { Log = log.Add };
        tracker.Reset(0, 0);
        double t = 0;
        for (int i = 0; i < 60; i++, t += 50)
        {
            tracker.Update(f, t);
            var sn = tracker.Snap();
            if (sn.Slots.Count > 0 && sn.Slots.All(x => !x.Pending) && i > 2) break;
        }
        var snap = tracker.Snap();
        long ms = sw.ElapsedMilliseconds;

        sb.AppendLine($"ShopHelper v{App.Version} · 이미지 {f.W}x{f.H} · 처리 {ms}ms · 신력의 휘석 1개 = {Parser.FmtPrice(cfg.HuiseokPrice)}결");
        sb.Append(Parser.Describe(snap.Slots.Select(x => x.Card), cfg));
        sb.AppendLine(snap.Refresh != null ? "새로고침 버튼: 찾음" : "새로고침 버튼: 못 찾음");
        if (snap.Status.Length > 0) sb.AppendLine("상태: " + snap.Status);
        sb.AppendLine("--- 처리 기록 ---");
        foreach (var l in log) sb.AppendLine(l);

        // 참고용: 화면 전체 OCR 원문
        int top = (int)(f.H * cfg.ScanRegion.Top), bottom = (int)(f.H * cfg.ScanRegion.Bottom);
        var crop = f.Crop(0, top, f.W, Math.Max(1, bottom - top));
        double sc = Math.Min(cfg.OcrScale, ocr.MaxDim / (double)Math.Max(crop.W, crop.H));
        var lines = ocr.Recognize(crop.Scale(sc), new Box(0, top, f.W, bottom), sc);
        sb.AppendLine("--- 화면 전체 OCR 원문 ---");
        foreach (var ws in lines) sb.AppendLine(string.Join(" | ", ws.Select(w => w.Text)));
        File.WriteAllText(outBase + ".txt", sb.ToString());

        // 칸 표시 그림: 카드 전체 테두리 (초록=이득, 빨강=손해, 노랑=도박, 회색=시세 없음/못 읽음)
        var img = new Frame((byte[])f.Px.Clone(), f.W, f.H);
        foreach (var sl in snap.Slots)
        {
            var b = sl.D;
            switch (sl.Card?.Status)
            {
                case Verdict.Good: img.DrawRect(b, 46, 204, 113, 4); break;
                case Verdict.Bad: img.DrawRect(b, 231, 76, 60, 2); break;
                case Verdict.Gamble: img.DrawRect(b, 245, 158, 11, 3); break;
                case Verdict.Sold: img.DrawRect(b, 90, 90, 90, 1); break;
                default: img.DrawRect(b, 149, 165, 166, 2); break;
            }
        }
        ImageFile.SavePng(img, outBase + ".png");
        return sb.ToString();
    }
}

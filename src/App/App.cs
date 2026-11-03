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
    public static readonly string ConfigPath = Path.Combine(DataDir, "config.json");
    static readonly string OldConfigPath = Path.Combine(Dir, "config.json");

    /// <summary>예전 버전처럼 exe 옆에 config.json 이 있고 새 위치에는 없으면, 한 번만 옮겨옴.</summary>
    static void MoveOldConfig()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            if (!File.Exists(ConfigPath) && File.Exists(OldConfigPath))
            {
                File.Copy(OldConfigPath, ConfigPath);
                Log("예전 config.json 을 " + ConfigPath + " 로 옮김");
            }
        }
        catch (Exception e) { Log("설정 폴더 오류: " + e.Message); }
    }
    public static readonly string LogPath = Path.Combine(Dir, "helper.log");
    static readonly object logLock = new();

    public static void Log(string msg)
    {
        lock (logLock)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}"); } catch { }
        }
    }

    static DateTime savedAt;
    public static void ConfigSaved() { try { savedAt = File.GetLastWriteTimeUtc(ConfigPath); } catch { } }
    /// <summary>메모장 등으로 config.json 을 바꿨는지 (우리가 저장한 건 제외).</summary>
    public static bool ConfigChangedOutside()
    {
        try
        {
            var t = File.GetLastWriteTimeUtc(ConfigPath);
            if (t == savedAt) return false;
            savedAt = t; return true;
        }
        catch { return false; }
    }

    public static void Error(string msg) => MessageBoxW(IntPtr.Zero, msg, "신들의 강복 도우미", 0x10);

    public const string Version = "1.5.1";

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
        Config cfg;
        MoveOldConfig();
        Log("설정 파일: " + ConfigPath);
        try { cfg = Config.Load(ConfigPath); }
        catch (Exception e) { Error("config.json 을 읽을 수 없습니다:\n" + e.Message); return 1; }

        Log("설정 읽음 · 항목 " + cfg.Items.Count + "개");
        WinOcr ocr;
        try { ocr = new WinOcr(); Log("OCR 준비됨 (최대 " + ocr.MaxDim + "px)"); }
        catch (Exception e) { Error(e.Message); return 1; }

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
        var overlay = new Overlay(cfg);
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

    void OnShop(bool open)
    {
        App.Log(open ? "상점 열림" : "상점 닫힘");
        shopOpen = open;
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
                new LogWatcher(path, OnShop).Start();
                return;
            }
            LogInfo = "로그를 못 찾음 · 게임을 켜면 다시 찾습니다";
            Thread.Sleep(3000);
        }
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
            if (rect != null && fg != hwnd && fg != IntPtr.Zero && fg == overlay.EditorHwnd)
            {
                // 시세 입력 창을 쓰는 동안: 화면 추적은 멈추고 결과만 계속 보여줌
                overlay.Panel(PanelState.Visible, rect.Value, unknownVer);
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
            var frame = rows is { } rr ? grab.Grab(x, y, w, h, rr.From, rr.To) : grab.Grab(x, y, w, h);
            bool dirty = tracker.Update(frame, now);
            if (dirty)
            {
                var un = tracker.UnknownNames();
                if (!un.SequenceEqual(unknown)) unknownVer++;
                unknown = un;
            }
            overlay.Panel(PanelState.Visible, rect.Value, unknownVer);
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
    public static string Run(Frame f, IOcr ocr, Config cfg, string outBase)
    {
        var sw = Stopwatch.StartNew();
        int top = (int)(f.H * cfg.ScanRegion.Top), bottom = (int)(f.H * cfg.ScanRegion.Bottom);
        var crop = f.Crop(0, top, f.W, Math.Max(1, bottom - top));
        double sc = Math.Min(cfg.OcrScale, ocr.MaxDim / (double)Math.Max(crop.W, crop.H));
        var lines = ocr.Recognize(crop.Scale(sc), new Box(0, top, f.W, bottom), sc);
        foreach (var ws in lines) foreach (var w in ws) { w.X /= sc; w.Y = w.Y / sc + top; w.W /= sc; w.H /= sc; }
        long ms = sw.ElapsedMilliseconds;
        var (cards, refresh) = Parser.Analyze(lines, cfg);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"이미지 {f.W}x{f.H} · 전체 OCR {ms}ms (확대 {sc:0.##}배)");
        sb.AppendLine("--- OCR 원문 ---");
        foreach (var ws in lines) sb.AppendLine(string.Join(" | ", ws.Select(w => w.Text)));
        sb.Append(Parser.Describe(cards, cfg));
        sb.AppendLine(refresh != null ? "새로고침 버튼: 찾음" : "새로고침 버튼: 못 찾음");
        File.WriteAllText(outBase + ".txt", sb.ToString());
        var img = new Frame((byte[])f.Px.Clone(), f.W, f.H);
        foreach (var c in cards)
        {
            var b = new Box(c.B.X1 - 6, c.B.Y1 - 6, c.B.X2 + 6, c.B.Y2 + 6);
            switch (c.Status)
            {
                case Verdict.Good: img.DrawRect(b, 46, 204, 113); break;
                case Verdict.Bad: img.DrawRect(b, 231, 76, 60); break;
                default: img.DrawRect(b, 149, 165, 166); break;
            }
        }
        ImageFile.SavePng(img, outBase + ".png");
        return sb.ToString();
    }
}

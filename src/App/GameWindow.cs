using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>게임 창 찾기, 화면 캡처, 로그 위치 찾기.</summary>
static class GameWindow
{
    public static IntPtr Find(Config cfg)
    {
        IntPtr found = IntPtr.Zero;
        var sb = new StringBuilder(256);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            sb.Clear(); GetClassNameW(h, sb, 256);
            if (cfg.WindowClass.Length > 0 && sb.ToString() != cfg.WindowClass) return true;
            if (cfg.WindowTitleContains.Length > 0)
            {
                sb.Clear(); GetWindowTextW(h, sb, 256);
                if (!sb.ToString().Contains(cfg.WindowTitleContains)) return true;
            }
            if (cfg.WindowClass.Length == 0 && cfg.WindowTitleContains.Length == 0) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>게임 창 클라이언트 영역의 화면 좌표 (x, y, w, h).</summary>
    public static (int X, int Y, int W, int H)? ClientRect(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h) || IsIconic(h)) return null;
        if (!GetClientRect(h, out var r)) return null;
        var p = new POINT();
        ClientToScreen(h, ref p);
        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
        if (w < 200 || hh < 200) return null;
        return (p.X, p.Y, w, hh);
    }

    public static string? ExePath(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // ---------------- 로그 위치 ----------------
    static readonly string LogRel = Path.Combine("UE_game", "TorchLight", "Saved", "Logs", "UE_game.log");

    static IEnumerable<string> SteamRoots()
    {
        (RegistryKey hive, string sub, string name)[] keys =
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };
        foreach (var (hive, sub, name) in keys)
        {
            string? v = null;
            try { using var k = hive.OpenSubKey(sub); v = k?.GetValue(name) as string; } catch { }
            if (!string.IsNullOrEmpty(v)) yield return Path.GetFullPath(v);
        }
    }

    static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in SteamRoots())
        {
            if (seen.Add(root)) yield return root;
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            string text = "";
            try { text = File.ReadAllText(vdf); } catch { }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var p = m.Groups[1].Value.Replace(@"\\", @"\");
                if (seen.Add(p)) yield return p;
            }
        }
    }

    static string? FromSteam()
    {
        foreach (var lib in SteamLibraries())
        {
            var common = Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            foreach (var d in Directory.EnumerateDirectories(common))
                if (Path.GetFileName(d).Contains("torchlight", StringComparison.OrdinalIgnoreCase))
                {
                    var p = Path.Combine(d, LogRel);
                    if (File.Exists(p)) return p;
                }
        }
        return null;
    }

    static string? FromProcess(Config cfg)
    {
        var h = Find(cfg);
        if (h == IntPtr.Zero) return null;
        var exe = ExePath(h);
        if (exe == null) return null;
        var d = Path.GetDirectoryName(exe);
        for (int i = 0; i < 7 && d != null; i++, d = Path.GetDirectoryName(d))
            foreach (var c in new[] { Path.Combine(d, "Saved", "Logs", "UE_game.log"), Path.Combine(d, "TorchLight", "Saved", "Logs", "UE_game.log"), Path.Combine(d, LogRel) })
                if (File.Exists(c)) return c;
        return null;
    }

    public static (string? path, string how) FindLog(Config cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.LogPath) && cfg.LogPath != "auto" && File.Exists(cfg.LogPath))
            return (cfg.LogPath, "config.json");
        try { var p = FromSteam(); if (p != null) return (p, "스팀 라이브러리"); } catch (Exception e) { App.Log("스팀 검색 오류: " + e.Message); }
        try { var p = FromProcess(cfg); if (p != null) return (p, "실행 중인 게임"); } catch (Exception e) { App.Log("프로세스 검색 오류: " + e.Message); }
        return (null, "");
    }
}

/// <summary>GDI BitBlt 화면 캡처 (빠름, 1080p 약 5~15ms).</summary>
sealed class Grabber : IDisposable
{
    IntPtr memDc, dib, bits, old;
    int w, h;
    Frame? frame;

    /// <summary>클라이언트 영역을 캡처. rowFrom~rowTo 가 주어지면 그 줄만 새로 찍음 (버퍼 재사용).</summary>
    public Frame Grab(int x, int y, int width, int height, int rowFrom = 0, int rowTo = int.MaxValue)
    {
        if (width != w || height != h || dib == IntPtr.Zero) { Alloc(width, height); rowFrom = 0; rowTo = int.MaxValue; }
        rowFrom = Math.Clamp(rowFrom, 0, h - 1);
        rowTo = Math.Clamp(rowTo, rowFrom + 1, h);
        int rows = rowTo - rowFrom;
        var scr = GetDC(IntPtr.Zero);
        BitBlt(memDc, 0, rowFrom, w, rows, scr, x, y + rowFrom, SRCCOPY);
        ReleaseDC(IntPtr.Zero, scr);
        Marshal.Copy(bits + rowFrom * w * 4, frame!.Px, rowFrom * w * 4, rows * w * 4);
        return frame;
    }

    /// <summary>
    /// 게임 창 내용만 직접 받아옴 (PrintWindow). 다른 창(오버레이 등)은 안 찍힘.
    /// 캡처 제외 설정이 안 되는 PC 용. 검은 화면이면 null.
    /// </summary>
    public Frame? GrabWindow(IntPtr hwnd, int width, int height)
    {
        if (width != w || height != h || dib == IntPtr.Zero) Alloc(width, height);
        if (!PrintWindow(hwnd, memDc, 0x1 | 0x2)) return null; // CLIENTONLY | RENDERFULLCONTENT
        Marshal.Copy(bits, frame!.Px, 0, frame.Px.Length);
        // 검은 화면인지 (DirectX 창에서 실패하는 경우)
        long sum = 0;
        var px = frame.Px;
        for (int i = 0; i < px.Length; i += 4 * 997) sum += px[i] + px[i + 1] + px[i + 2];
        return sum == 0 ? null : frame;
    }

    void Alloc(int width, int height)
    {
        Free();
        w = width; h = height;
        var scr = GetDC(IntPtr.Zero);
        memDc = CreateCompatibleDC(scr);
        ReleaseDC(IntPtr.Zero, scr);
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        dib = CreateDIBSection(memDc, ref bi, 0, out bits, IntPtr.Zero, 0);
        old = SelectObject(memDc, dib);
        frame = new Frame(w, h);
    }

    void Free()
    {
        if (memDc != IntPtr.Zero) { SelectObject(memDc, old); DeleteDC(memDc); memDc = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
    }

    public void Dispose() => Free();
}

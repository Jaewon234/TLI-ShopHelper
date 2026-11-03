using System.Runtime.InteropServices;
using System.Text;

namespace ShopHelper;

/// <summary>
/// 숫자 입력칸: 숫자와 소수점(한 번)만 입력됨. 한글 입력(IME)은 꺼지고, 붙여넣기는 숫자와 첫 소수점만 남김.
/// </summary>
static class NumericEdit
{
    delegate IntPtr SubclassProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr h, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("imm32.dll")] static extern IntPtr ImmAssociateContext(IntPtr h, IntPtr himc);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint msg, out int wp, out int lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr wp, string lp);

    const uint WM_CHAR = 0x102, WM_PASTE = 0x302, WM_IME_STARTCOMPOSITION = 0x10D, WM_IME_CHAR = 0x286,
        EM_GETSEL = 0xB0, EM_REPLACESEL = 0xC2;

    static readonly SubclassProc Proc = WndProc;   // GC 방지

    public static void Attach(IntPtr edit)
    {
        if (edit == IntPtr.Zero) return;
        ImmAssociateContext(edit, IntPtr.Zero);   // 한/영 전환해도 한글이 안 들어감
        SetWindowSubclass(edit, Proc, (UIntPtr)1, UIntPtr.Zero);
    }

    static string Text(IntPtr h)
    {
        int n = GetWindowTextLengthW(h);
        var sb = new StringBuilder(n + 1);
        GetWindowTextW(h, sb, n + 1);
        return sb.ToString();
    }

    /// <summary>지금 글자에서 선택된 부분을 뺀 나머지에 소수점이 있는지.</summary>
    static bool DotOutsideSelection(IntPtr h)
    {
        var t = Text(h);
        SendMessageW(h, EM_GETSEL, out int a, out int b);
        a = Math.Clamp(a, 0, t.Length); b = Math.Clamp(b, a, t.Length);
        return (t[..a] + t[b..]).Contains('.');
    }

    /// <summary>숫자와 첫 소수점만 남김.</summary>
    public static string Filter(string s, bool allowDot = true)
    {
        var sb = new StringBuilder();
        bool dot = !allowDot;
        foreach (var ch in s)
        {
            if (ch is >= '0' and <= '9') sb.Append(ch);
            else if (ch is >= '０' and <= '９') sb.Append((char)('0' + (ch - '０')));
            else if ((ch == '.' || ch == '．') && !dot) { sb.Append('.'); dot = true; }
        }
        return sb.ToString();
    }

    static string? Clipboard(IntPtr owner)
    {
        if (!OpenClipboard(owner)) return null;
        try
        {
            var h = GetClipboardData(13 /*CF_UNICODETEXT*/);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    static IntPtr WndProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, UIntPtr data)
    {
        switch (msg)
        {
            case WM_CHAR:
            {
                int ch = (int)wp.ToInt64();
                if (ch < 0x20 || ch == 0x7F) break;                       // Backspace, Ctrl+A/C/V/X/Z 등
                if (ch is >= '0' and <= '9') break;
                if (ch == '.' && !DotOutsideSelection(h)) break;
                return IntPtr.Zero;                                       // 그 밖의 글자는 무시 (소리 없음)
            }
            case WM_IME_STARTCOMPOSITION:
            case WM_IME_CHAR:
                return IntPtr.Zero;
            case WM_PASTE:
            {
                var clip = Clipboard(h);
                if (clip == null) return IntPtr.Zero;
                var f = Filter(clip, allowDot: !DotOutsideSelection(h));
                if (f.Length > 0) SendMessageW(h, EM_REPLACESEL, new IntPtr(1), f);
                return IntPtr.Zero;
            }
        }
        return DefSubclassProc(h, msg, wp, lp);
    }
}

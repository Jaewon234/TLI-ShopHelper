using System.Text;
using System.Text.RegularExpressions;

namespace ShopHelper;

/// <summary>UE_game.log 끝에 추가되는 줄만 읽어서 상점 열림/닫힘을 알림 (30ms 간격).</summary>
sealed class LogWatcher
{
    static readonly Regex Open = new(@"Page@ OpenView GroceryStoreV3\s*$", RegexOptions.Compiled);
    static readonly Regex Close = new(@"Page@ CloseView \d+_GroceryStoreV3Ctrl", RegexOptions.Compiled);

    static readonly Regex AnyOpen = new(@"Page@ OpenView (\S+)\s*$", RegexOptions.Compiled);
    static readonly Regex AnyClose = new(@"Page@ CloseView \d+_(\S+?)Ctrl\s*$", RegexOptions.Compiled);

    readonly string path;
    readonly Action<bool> onChange;
    /// <summary>상점 말고 다른 창이 열리고 닫힐 때 (이름, 열림)</summary>
    public Action<string, bool>? OnView;
    public bool IsOpen { get; private set; }

    public LogWatcher(string path, Action<bool> onChange) { this.path = path; this.onChange = onChange; }

    public void Start() => new Thread(Run) { IsBackground = true, Name = "log" }.Start();

    static FileStream OpenShared(string p) =>
        new(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    long InitialState()
    {
        try
        {
            using var fs = OpenShared(path);
            long size = fs.Length;
            long start = Math.Max(0, size - 512 * 1024);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[size - start];
            fs.ReadExactly(buf);
            foreach (var line in Encoding.UTF8.GetString(buf).Split('\n'))
            {
                if (Open.IsMatch(line.TrimEnd('\r'))) IsOpen = true;
                else if (Close.IsMatch(line)) IsOpen = false;
            }
            return size;
        }
        catch { return 0; }
    }

    void Run()
    {
        long pos = InitialState();
        onChange(IsOpen);
        var pending = new List<byte>();
        while (true)
        {
            try
            {
                using var fs = OpenShared(path);
                long size = fs.Length;
                if (size < pos) { pos = 0; pending.Clear(); App.Log("로그 파일이 새로 시작됨"); }
                if (size > pos)
                {
                    fs.Seek(pos, SeekOrigin.Begin);
                    var buf = new byte[size - pos];
                    int n = fs.Read(buf, 0, buf.Length);
                    pos += n;
                    int start = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i] != (byte)'\n') continue;
                        pending.AddRange(new ArraySegment<byte>(buf, start, i - start));
                        Handle(Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r'));
                        pending.Clear();
                        start = i + 1;
                    }
                    pending.AddRange(new ArraySegment<byte>(buf, start, n - start));
                }
            }
            catch { }
            Thread.Sleep(30);
        }
    }

    void Handle(string line)
    {
        if (IsOpen && OnView != null)
        {
            var mo = AnyOpen.Match(line);
            if (mo.Success && mo.Groups[1].Value != "GroceryStoreV3") OnView(mo.Groups[1].Value, true);
            var mc = AnyClose.Match(line);
            if (mc.Success && mc.Groups[1].Value != "GroceryStoreV3") OnView(mc.Groups[1].Value, false);
        }
        if (Open.IsMatch(line)) { if (!IsOpen) { IsOpen = true; onChange(true); } }
        else if (Close.IsMatch(line)) { if (IsOpen) { IsOpen = false; onChange(false); } }
    }
}

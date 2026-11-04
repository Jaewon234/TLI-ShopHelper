using System.Text;

namespace ShopHelper;

/// <summary>UE_game.log 끝에 추가되는 줄만 읽어서 상점 열림/닫힘을 알림 (30ms 간격).</summary>
sealed class LogWatcher
{
    readonly string path;
    readonly Action<bool> onChange;
    /// <summary>상점 말고 다른 창이 열리고 닫힐 때 (이름, 열림)</summary>
    public Action<string, bool>? OnView;
    public bool IsOpen { get; private set; }

    public LogWatcher(string path, Action<bool> onChange) { this.path = path; this.onChange = onChange; }

    volatile bool stopped;
    public string FilePath => path;
    public void Stop() => stopped = true;

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
                var (ev, _) = LogLine.Parse(line.TrimEnd('\r'));
                if (ev == LogEv.ShopOpen) IsOpen = true;
                else if (ev == LogEv.ShopClose) IsOpen = false;
            }
            return size;
        }
        catch { return 0; }
    }

    void Run()
    {
        long pos = InitialState();
        if (stopped) return;
        onChange(IsOpen);
        var pending = new List<byte>();
        while (!stopped)
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

    const string ShopCovered = "상점 가려짐";
    bool covered;

    void Handle(string line)
    {
        if (stopped) return;
        var (ev, name) = LogLine.Parse(line);
        switch (ev)
        {
            case LogEv.ShopOpen:
                if (!IsOpen) { IsOpen = true; covered = false; onChange(true); }
                else if (covered) { covered = false; OnView?.Invoke(ShopCovered, false); }   // 다른 화면에서 상점으로 돌아옴
                break;
            case LogEv.ShopClose:
                if (IsOpen) { IsOpen = false; covered = false; onChange(false); }
                break;
            case LogEv.ShopHidden:
                if (IsOpen && !covered) { covered = true; OnView?.Invoke(ShopCovered, true); }
                break;
            case LogEv.ViewOpen:
            case LogEv.ViewClose:
                if (IsOpen) OnView?.Invoke(name, ev == LogEv.ViewOpen);
                break;
        }
    }
}

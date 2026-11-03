namespace ShopHelper;

public interface IOcr
{
    int MaxDim { get; }
    /// <summary>이미지를 읽어 줄 단위 단어 목록을 돌려줌 (이미지 좌표). src/scale 은 테스트용 정보.</summary>
    List<List<OcrWord>> Recognize(Frame img, Box src, double scale);
}

public sealed class SlotView
{
    public Box R;
    public Box D;   // 카드 전체(그림 포함) - 표시용
    public Card? Card;
    public bool Pending;
}

public sealed class Snapshot
{
    public List<SlotView> Slots = new();
    public Box? Refresh;
    public int Pages;
    public double SumGain;
    public double PageGain;
    public double? LatencyMs;
    public string Status = "";
}

/// <summary>
/// 상점 칸 추적기.
/// 1) 처음엔 화면 전체를 OCR 해서 칸 위치를 찾음
/// 2) 그 뒤로는 칸마다 지문(축소 이미지)만 비교해서 바뀐 칸만 처리
/// 3) 한 번 본 칸 그림은 기억해 두었다가 OCR 없이 바로 결과를 냄
/// </summary>
public sealed class Tracker
{
    sealed class Slot
    {
        public Box R, PriceR, D;
        public byte[]? SettledFp, SettledPfp, PrevFp, PrevPfp;
        public Card? Card;
        public bool Pending;
        public bool Stale;   // 다시 읽기에 실패해서 이전 결과를 보여주는 중 (계속 다시 시도)
        public double ChangeT;
    }

    sealed class CacheEntry
    {
        public byte[] Fp = Array.Empty<byte>(), Pfp = Array.Empty<byte>();
        public Card Card = new();
    }

    readonly IOcr ocr;
    readonly Config cfg;
    readonly List<CacheEntry> cache = new();
    List<Slot>? slots;
    Box? refresh;
    int frameW, frameH;
    double nextFullScan;
    double? batchStart;
    string? lastSig;
    int pages;
    double sumGain;
    public double? LastLatencyMs { get; private set; }
    public string Status { get; private set; } = "";
    public Action<string>? Log;
    /// <summary>
    /// 글자를 읽을 때만 쓰는 '깨끗한' 화면 (오버레이가 안 찍힌 것). null 이면 감시용 화면을 그대로 씀.
    /// 감시(지문 비교)는 매 프레임 빠른 캡처로, 이건 새로고침 때 한 번 정도만 불림.
    /// </summary>
    public Func<Frame?>? CleanFrame;
    Frame? clean;
    bool cleanTried;

    Frame Src(Frame f)
    {
        if (CleanFrame == null) return f;
        if (!cleanTried) { cleanTried = true; clean = CleanFrame(); }
        return clean ?? f;
    }

    public const double StableMs = 40, MaxWaitMs = 400, RetryMs = 500;

    /// <summary>칸 위치를 알면 그 줄 범위만 캡처하면 됨 (null 이면 전체).</summary>
    public (int From, int To)? Rows => slots == null || slots.Count == 0 ? null
        : ((int)slots.Min(s => s.R.Y1) - 2, (int)slots.Max(s => s.R.Y2) + 3);

    public Tracker(IOcr ocr, Config cfg) { this.ocr = ocr; this.cfg = cfg; }

    /// <summary>상점이 새로 열릴 때. 칸 위치는 다시 찾지만 그림 기억(cache)은 유지.</summary>
    public void Reset(double now, double delayMs = 250)
    {
        slots = null; refresh = null; batchStart = null; lastSig = null;
        nextFullScan = now + delayMs;
        Status = "칸 찾는 중";
    }

    public void Reevaluate()
    {
        foreach (var e in cache) Parser.Evaluate(e.Card, cfg);
        if (slots != null) foreach (var s in slots) if (s.Card != null) Parser.Evaluate(s.Card, cfg);
    }

    /// <summary>프레임 하나 처리. 화면에 보일 내용이 바뀌면 true.</summary>
    public bool Update(Frame f, double now)
    {
        clean = null; cleanTried = false;
        if (f.W != frameW || f.H != frameH)
        {
            frameW = f.W; frameH = f.H;
            slots = null;
        }
        if (slots == null)
        {
            if (now < nextFullScan) return false;
            FullScan(f, now);
            return true;
        }

        bool dirty = false;
        int cellsAll = 0;
        foreach (var s in slots)
        {
            var fp = Fingerprint.Make(f, s.R);
            var pfp = Fingerprint.Make(f, s.PriceR);
            cellsAll = Fingerprint.Cells(fp);
            if (!s.Pending && (s.Card == null || s.Stale) && now - s.ChangeT >= RetryMs)
            {
                // 못 읽은 칸은 0.5초마다 다시 시도
                s.Pending = true; s.ChangeT = now - StableMs; s.PrevFp = fp; s.PrevPfp = pfp;
            }
            else if (!s.Pending)
            {
                bool changed = Fingerprint.DiffCells(fp, s.SettledFp) > Math.Max(2, cellsAll / 50)
                               || Fingerprint.DiffCells(pfp, s.SettledPfp) > 0;
                if (changed)
                {
                    s.Pending = true; s.ChangeT = now; s.PrevFp = fp; s.PrevPfp = pfp;
                    batchStart ??= now;
                    dirty = true;
                }
            }
            else
            {
                bool stable = Fingerprint.DiffCells(fp, s.PrevFp) <= cellsAll / 100
                              && Fingerprint.DiffCells(pfp, s.PrevPfp) == 0;
                s.PrevFp = fp; s.PrevPfp = pfp;
                double age = now - s.ChangeT;
                if ((stable && age >= StableMs) || age >= MaxWaitMs)
                {
                    Resolve(f, s, fp, pfp, now);
                    dirty = true;
                }
            }
        }

        if (dirty && batchStart != null && slots.All(s => !s.Pending))
        {
            LastLatencyMs = now - batchStart.Value;
            batchStart = null;
            CountPage();
            int failed = slots.Count(s => s.Card == null);
            if (failed > slots.Count / 2)
            {
                Log?.Invoke($"칸 {failed}개를 못 읽음 → 칸 위치 다시 찾기");
                Reset(now, 0);
            }
        }
        return dirty;
    }

    void Resolve(Frame f, Slot s, byte[] fp, byte[] pfp, double now)
    {
        s.Pending = false;
        s.ChangeT = now;
        s.SettledFp = fp; s.SettledPfp = pfp;
        int cells = Fingerprint.Cells(fp);
        foreach (var e in cache)
        {
            if (Fingerprint.DiffCells(e.Pfp, pfp) == 0 && Fingerprint.DiffCells(e.Fp, fp) <= cells / 100)
            {
                s.Card = e.Card.Clone();
                return;
            }
        }
        var card = OcrSlot(f, s);
        if (card == null && s.Card != null)
        {
            s.Stale = true;          // 못 읽으면 이전 결과를 그대로 보여주고 다시 시도 (번쩍임 방지)
            return;
        }
        s.Stale = false;
        s.Card = card;
        if (card != null)
        {
            cache.Add(new CacheEntry { Fp = fp, Pfp = pfp, Card = card.Clone() });
            if (cache.Count > 400) cache.RemoveAt(0);
        }
    }

    Card? OcrSlot(Frame f, Slot s)
    {
        var crop = Src(f).Crop((int)s.R.X1, (int)s.R.Y1, (int)s.R.W, (int)s.R.H).AutoContrast();
        double sc = Math.Min(cfg.SlotOcrScale, ocr.MaxDim / (double)Math.Max(crop.W, crop.H));
        var img = Math.Abs(sc - 1) > 1e-6 ? crop.Scale(sc) : crop;
        var lines = ocr.Recognize(img, s.R, sc);
        foreach (var ws in lines)
            foreach (var w in ws) { w.X = w.X / sc + s.R.X1; w.Y = w.Y / sc + s.R.Y1; w.W /= sc; w.H /= sc; }
        var (cards, _) = Parser.Analyze(lines, cfg);
        if (cards.Count == 0)
        {
            Log?.Invoke("칸 읽기 실패: " + string.Join(" | ", lines.Select(l => string.Join(" ", l.Select(w => w.Text)))));
            return null;
        }
        return cards.OrderBy(c => Math.Abs(c.B.Cx - s.R.Cx)).First();
    }

    void FullScan(Frame f, double now)
    {
        int top = (int)(f.H * cfg.ScanRegion.Top), bottom = (int)(f.H * cfg.ScanRegion.Bottom);
        var crop = Src(f).Crop(0, top, f.W, Math.Max(1, bottom - top));
        double sc = Math.Min(cfg.OcrScale, ocr.MaxDim / (double)Math.Max(crop.W, crop.H));
        var img = Math.Abs(sc - 1) > 1e-6 ? crop.Scale(sc) : crop;
        var src = new Box(0, top, f.W, bottom);
        var lines = ocr.Recognize(img, src, sc);
        foreach (var ws in lines)
            foreach (var w in ws) { w.X /= sc; w.Y = w.Y / sc + top; w.W /= sc; w.H /= sc; }
        var segs = Parser.ToSegments(lines);
        var cards = Parser.ToCards(Parser.ToColumns(segs));
        var rf = Parser.FindRefresh(segs);
        if (cards.Count < 3)
        {
            Status = $"칸을 찾지 못함 ({cards.Count}개) · 다시 시도";
            Log?.Invoke(Status + " :: " + string.Join(" | ", segs.Select(s => s.Text)));
            nextFullScan = now + 300;
            return;
        }
        foreach (var c in cards) Parser.Evaluate(c, cfg);
        slots = BuildSlots(cards, segs);
        refresh = rf?.B;
        foreach (var s in slots)
        {
            s.SettledFp = Fingerprint.Make(f, s.R);
            s.SettledPfp = Fingerprint.Make(f, s.PriceR);
            s.Card = cards.FirstOrDefault(c => c.B.Cx >= s.R.X1 && c.B.Cx <= s.R.X2 && c.PriceBox.Y1 >= s.R.Y1 && c.PriceBox.Y1 <= s.R.Y2);
            if (s.Card != null)
                cache.Add(new CacheEntry { Fp = s.SettledFp, Pfp = s.SettledPfp, Card = s.Card.Clone() });
            else
                s.Pending = true; // 처음 스캔에서 못 읽은 칸은 칸 단위로 다시 읽음
            s.ChangeT = now - MaxWaitMs;
        }
        Status = "";
        Log?.Invoke($"칸 {slots.Count}개 찾음 (OCR {cards.Count}개)");
        CountPage();
    }

    static double Median(IEnumerable<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a[a.Length / 2];
    }

    static List<Slot> BuildSlots(List<Card> cards, List<Seg>? segs = null)
    {
        double h = Median(cards.Select(c => c.H));
        // 줄별로 묶기 (UI 크기에 따라 8개 한 줄, 6+2 두 줄 등)
        var rows = new List<List<Card>>();
        foreach (var cd in cards.OrderBy(c => c.PriceBox.Y1))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].PriceBox.Y1 - cd.PriceBox.Y1) < 1.5 * h);
            if (row == null) rows.Add(new List<Card> { cd }); else row.Add(cd);
        }
        // 칸 간격 = 같은 줄 안에서 가장 가까운 두 칸 사이
        double spacing = double.MaxValue;
        foreach (var r in rows)
        {
            var xs = r.Select(c => c.B.Cx).OrderBy(x => x).ToList();
            for (int i = 1; i < xs.Count; i++) spacing = Math.Min(spacing, xs[i] - xs[i - 1]);
        }
        if (spacing == double.MaxValue || spacing < 3 * h) spacing = cards.Max(c => c.B.W) * 1.15;
        else
        {
            // 간격을 줄 전체 길이로 다시 계산 (가까운 두 칸만 보면 1px 오차가 칸마다 쌓임)
            var wide = rows.OrderByDescending(r => r.Max(c => c.B.Cx) - r.Min(c => c.B.Cx)).First();
            double span = wide.Max(c => c.B.Cx) - wide.Min(c => c.B.Cx);
            int steps = (int)Math.Round(span / spacing);
            if (steps > 0) spacing = span / steps;
        }
        // 비교 범위는 카드 안쪽만 (카드 폭 = 간격의 83~90%). 카드 밖 배경이 섞이면
        // 마우스를 올려 카드만 어두워질 때 '바뀐 것'으로 잘못 잡힘.
        double half = spacing * 0.40;
        double dhalf = spacing * 0.44;
        var slots = new List<Slot>();
        foreach (var r in rows)
        {
            double py1 = Median(r.Select(c => c.PriceBox.Y1));
            double py2 = Median(r.Select(c => c.PriceBox.Y2));
            var cx = r.Select(c => c.B.Cx).OrderBy(x => x).ToList();
            // 줄 양 끝: 가격을 못 읽었어도 그 자리에 상품 이름 글자가 있으면 칸을 늘림
            // (예: 맨 오른쪽 신비의 휘석(대) 가격 500 을 OCR 이 놓친 경우)
            double nameTop = py1 - 4.6 * h, nameBot = py1;
            bool HasName(double x) => segs != null && segs.Any(sg =>
                System.Text.RegularExpressions.Regex.IsMatch(sg.Text, "[가-힣]") &&
                Math.Abs(sg.B.Cx - x) < spacing * 0.35 && sg.B.Y1 >= nameTop - h && sg.B.Y2 <= nameBot + h &&
                Math.Abs(sg.H - h) < h * 0.4);
            double first = cx[0], last = cx[^1];
            for (int k = 0; k < 8 && HasName(first - spacing); k++) first -= spacing;
            for (int k = 0; k < 8 && HasName(last + spacing); k++) last += spacing;
            // 줄 안의 빈 칸(못 읽은 칸)도 일정한 간격으로 채움
            for (double x = first; x <= last + spacing * 0.5; x += spacing)
            {
                double near = cx.OrderBy(c => Math.Abs(c - x)).First();
                double c0 = Math.Abs(near - x) < spacing * 0.3 ? near : x;
                slots.Add(new Slot
                {
                    R = new Box(c0 - half, py1 - 4.6 * h, c0 + half, py2 + 0.2 * h),
                    PriceR = new Box(c0 - half, py1 - 0.2 * h, c0 + half, py2 + 0.2 * h),
                    D = new Box(c0 - dhalf, py1 - 12.3 * h, c0 + dhalf, py2 + 0.6 * h),
                });
            }
        }
        return slots;
    }

    void CountPage()
    {
        if (slots == null) return;
        var known = slots.Where(s => s.Card != null && !s.Card.Sold).ToList();
        if (known.Count < 3) return;
        var sig = slots.Select(s => s.Card == null ? "?" : $"{s.Card.Name}|{s.Card.Qty}|{s.Card.Price}").ToArray();
        var prev = lastSig?.Split('\n');
        int diff = prev == null || prev.Length != sig.Length ? sig.Length : sig.Where((x, i) => x != prev[i]).Count();
        lastSig = string.Join('\n', sig);
        if (diff >= Math.Max(1, sig.Length / 2))
        {
            pages++;
            sumGain += CurrentGain();
        }
    }

    double CurrentGain() => slots?.Where(s => s.Card?.Status == Verdict.Good).Sum(s => s.Card!.Diff!.Value) ?? 0;

    /// <summary>지금 화면에서 시세표에 없는 항목 이름 (시세 입력 창에 자동으로 추가).</summary>
    public List<string> UnknownNames() =>
        slots?.Where(s => s.Card != null && s.Card.Item == null).Select(s => s.Card!.Name).ToList() ?? new();

    public Snapshot Snap() => new()
    {
        Slots = slots?.Select(s => new SlotView { R = s.R, D = s.D, Card = s.Card, Pending = s.Pending }).ToList() ?? new(),
        Refresh = refresh,
        Pages = pages,
        SumGain = sumGain,
        PageGain = CurrentGain(),
        LatencyMs = LastLatencyMs,
        Status = Status,
    };
}

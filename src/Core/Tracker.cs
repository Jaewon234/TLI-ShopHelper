namespace ShopHelper;

public interface IOcr
{
    int MaxDim { get; }
    /// <summary>이미지를 읽어 줄 단위 단어 목록을 돌려줌 (이미지 좌표). src/scale 은 테스트용 정보.</summary>
    List<List<OcrWord>> Recognize(Frame img, Box src, double scale);
    /// <summary>숫자용 (영어 OCR). 한국어 OCR 이 숫자를 한글/한자로 읽는 경우(200→2예, 60→匍)를 보완. 없으면 null.</summary>
    List<List<OcrWord>>? RecognizeDigits(Frame img, Box src, double scale) => null;
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
    public int Refreshes, Bought, UnknownBought;
    public double NetGain;   // 휘석: 산 것들의 이득 합 − 새로고침 × 비용
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
        public int Fails;    // 연속 실패 횟수 (다시 시도 간격을 늘림)
        public string? FixKey;   // 자동 읽기에서 지금 값과 다르게 읽힌 값 (다음에도 같으면 고침)
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
    /// <summary>OCR 을 여러 스레드에서 동시에 불러도 되면 true. 그리고 설정 parallel_reread 가 켜져 있으면 자동 읽기 때 모든 칸을 동시에 읽음 (CPU 를 더 쓰는 대신 한 바퀴가 빠름).</summary>
    public bool ParallelReread;
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
    double lastRescan;
    System.Threading.Tasks.Task<List<(Slot slot, Card card, List<List<OcrWord>>?)>>? bg;   // 뒤에서 도는 동시 읽기
    List<Slot>? bgSlots;
    int rescanIdx = -1;   // 이번 바퀴에서 다음에 읽을 칸 (-1 = 쉬는 중)

    /// <summary>
    /// 자동 읽기 (TartarusSolver 처럼 몇 초마다): 바뀐 칸이 없어도 모든 칸을 다시 읽어서,
    /// 처음에 잘못 읽고 기억해 둔 값이 있으면 바로잡음.
    /// </summary>
    bool Rescan(Frame f, double now)
    {
        if (slots == null || cfg.RereadSec <= 0) { rescanIdx = -1; return false; }
        if (bg == null && slots.Any(s => s.Pending)) return false;   // 새로고침 처리 중이면 끝난 뒤에
        if (rescanIdx < 0 && bg == null)
        {
            if (now - lastRescan < cfg.RereadSec * 1000) return false;
            lastRescan = now; rescanIdx = 0;            // 새 바퀴 시작
        }
        // 기본: 한 번에 한 칸씩 (모든 칸을 한꺼번에 읽으면 그동안 새로고침 감지가 멈춤)
        // 동시 읽기: 모든 칸 숫자를 뒤에서(다른 스레드) 한꺼번에 읽고, 끝나면 다음 프레임에 반영 (화면 처리는 안 멈춤)
        bool changed = false;
        List<Slot> todo;
        List<List<OcrWord>>?[] ens;
        if (ParallelReread && cfg.ParallelReread)
        {
            if (bg == null)
            {
                if (rescanIdx < 0) return false;          // 쉬는 중
                rescanIdx = -1;
                var list = slots.Where(x => x.Card is { Sold: false, Status: not Verdict.Skip }).ToList();
                if (list.Count == 0) return false;
                var src0 = Src(f);
                var jobs = list.Select(x => (slot: x, card: x.Card!, prep: Prep(src0, x.R, cfg.SlotOcrScale))).ToList();
                var lay = slots;
                bgSlots = lay;
                bg = System.Threading.Tasks.Task.Run(() =>
                {
                    var res = new List<List<OcrWord>>?[jobs.Count];
                    System.Threading.Tasks.Parallel.For(0, jobs.Count,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) },
                        i => res[i] = ReadPrepped(jobs[i].prep.img, jobs[i].slot.R, jobs[i].prep.sc, digits: true));
                    return jobs.Select((j, i) => (j.slot, j.card, res[i])).ToList();
                });
                return false;
            }
            if (!bg.IsCompleted) return false;
            var done = bg; bg = null;
            if (done.IsFaulted) { Log?.Invoke("자동 읽기 오류: " + done.Exception?.GetBaseException().Message); return false; }
            if (!ReferenceEquals(bgSlots, slots)) return false;   // 그사이 칸 배치가 바뀜
            // 그사이 새로고침·구매로 바뀐 칸은 버림 (읽기 시작할 때와 같은 카드일 때만 반영)
            var ok = done.Result.Where(x => !x.slot.Pending && ReferenceEquals(x.slot.Card, x.card)).ToList();
            todo = ok.Select(x => x.slot).ToList();
            ens = ok.Select(x => x.Item3).ToArray();
        }
        else
        {
            if (rescanIdx < 0) return false;
            if (rescanIdx >= slots.Count) { rescanIdx = -1; return false; }
            todo = new List<Slot> { slots[rescanIdx++] };
            if (rescanIdx >= slots.Count) rescanIdx = -1;
            todo.RemoveAll(x => x.Card is { Sold: true } or { Status: Verdict.Skip });   // 매진·도박 칸은 다시 읽지 않음
            var src = Src(f);
            ens = todo.Select(x => x.Card is { Sold: false } ? Read(src, x.R, cfg.SlotOcrScale, digits: true) : null).ToArray();
        }
        for (int ti = 0; ti < todo.Count; ti++)
        {
            var s = todo[ti];
            // 이미 읽은 칸은 숫자(가격·개수)만 다시 확인 (이름은 거의 안 틀리고, 숫자만 읽는 게 훨씬 빠름)
            if (s.Card is { Sold: false } cur)
            {
                var en = ens[ti];
                if (en != null)
                {
                    var (p, q, marked) = DigitsFromEx(en, s);
                    // 자동 읽기에서 개수는 "X 숫자" 로 읽었을 때만 바꿈 (숫자만 읽힌 글자가 맞게 읽은 개수를 덮어쓰지 않게)
                    if (!marked) q = null;
                    var fixedCard = cur.Clone();
                    ApplyDigits(fixedCard, p, q, quiet: true);
                    if (fixedCard.Price == cur.Price && fixedCard.Qty == cur.Qty) { s.FixKey = null; continue; }
                    // 한 번 튀는 값은 무시: 다시 읽은 값이 두 번 연속 같을 때만 고침
                    var key = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(cur)}|{fixedCard.Price}|{fixedCard.Qty}";
                    if (s.FixKey != key) { s.FixKey = key; continue; }
                    s.FixKey = null;
                    Log?.Invoke($"다시 읽기로 고침 (2번 연속 같은 값): {Describe1(cur)} → {Describe1(fixedCard)}");
                    s.Card = fixedCard;
                    if (s.SettledFp != null && s.SettledPfp != null)
                    {
                        cache.RemoveAll(e => Fingerprint.DiffCells(e.Pfp, s.SettledPfp) == 0 && Fingerprint.DiffCells(e.Fp, s.SettledFp) <= Fingerprint.Cells(s.SettledFp) / 100);
                        cache.Add(new CacheEntry { Fp = s.SettledFp, Pfp = s.SettledPfp, Card = fixedCard.Clone() });
                    }
                    changed = true;
                    continue;
                }
            }
            var card = OcrSlot(f, s);
            if (card == null) continue;                 // 못 읽으면 지금 결과 유지
            var old = s.Card;
            if (old != null && old.Name == card.Name && old.Qty == card.Qty && old.Price == card.Price && old.Sold == card.Sold) continue;
            Log?.Invoke($"다시 읽기로 고침: {Describe1(old)} → {Describe1(card)}");
            s.Card = card; s.Stale = false; s.Fails = 0;
            // 기억해 둔 값도 고침 (같은 그림이면 다음부터 맞는 값)
            if (s.SettledFp != null && s.SettledPfp != null)
            {
                cache.RemoveAll(e => Fingerprint.DiffCells(e.Pfp, s.SettledPfp) == 0 && Fingerprint.DiffCells(e.Fp, s.SettledFp) <= Fingerprint.Cells(s.SettledFp) / 100);
                cache.Add(new CacheEntry { Fp = s.SettledFp, Pfp = s.SettledPfp, Card = card.Clone() });
            }
            changed = true;
        }
        if (changed) CountPage();
        return changed;
    }

    static string Describe1(Card? c) => c == null ? "(없음)" : c.Sold ? $"{c.Name} 매진" : $"{c.Name} x{c.Qty} {c.Price}휘";

    /// <summary>칸 위치를 알면 그 줄 범위만 캡처하면 됨 (null 이면 전체).</summary>
    public (int From, int To)? Rows => slots == null || slots.Count == 0 || needFull ? null
        : ((int)slots.Min(s => s.R.Y1) - 2, (int)slots.Max(s => s.R.Y2) + 3);

    public Tracker(IOcr ocr, Config cfg) { this.ocr = ocr; this.cfg = cfg; }

    /// <summary>상점이 새로 열릴 때. 칸 위치는 다시 찾지만 그림 기억(cache)은 유지.</summary>
    byte[]? openFp;
    double openStableSince, openWaitStart, relayoutAt = -1;
    bool needFull, firstLayout;
    /// <summary>처음 칸을 잡고 몇 ms 뒤에 위치를 한 번 더 확인할지 (0 = 안 함)</summary>
    public double RelayoutMs = 2000;

    public void Reset(double now, double delayMs = 250)
    {
        slots = null; refresh = null; batchStart = null; lastSig = null;
        openFp = null; needFull = false; relayoutAt = -1; firstLayout = true;
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
        if (slots == null || needFull)
        {
            if (now < nextFullScan) return false;
            if (slots == null)
            {
                // 상점이 열릴 때 카드가 움직이며 나타나므로, 화면이 0.3초 멈춘 뒤에 칸 위치를 잡음 (최대 2초 기다림)
                var ofp = Fingerprint.Make(f, new Box(0, 0, f.W, f.H * 0.6));
                if (openFp == null) openWaitStart = now;
                if (openFp == null || Fingerprint.DiffCells(ofp, openFp) > Fingerprint.Cells(ofp) / 200)
                {
                    openFp = ofp; openStableSince = now;
                    if (now - openWaitStart < 2000) return false;
                }
                if (now - openStableSince < 300 && now - openWaitStart < 2000) return false;
            }
            else Log?.Invoke("칸 위치 다시 확인");
            needFull = false;
            var swScan = System.Diagnostics.Stopwatch.StartNew();
            FullScan(f, now);
            // 처음 읽기가 빨랐으면(1초 미만) 2초 뒤 한 번 더 위치를 확인 (애니메이션 중에 잡혔을 수 있음)
            relayoutAt = firstLayout && RelayoutMs > 0 && swScan.ElapsedMilliseconds < 1000 ? now + RelayoutMs : -1;
            firstLayout = false;
            lastRescan = now;
            return true;
        }
        if (relayoutAt > 0 && now >= relayoutAt && slots.All(s => !s.Pending))
        {
            relayoutAt = -1; needFull = true;   // 다음 프레임은 화면 전체를 받아서 다시 잡음 (Rows = null)
            return false;
        }

        bool dirty = false;
        int cellsAll = 0;
        foreach (var s in slots)
        {
            var fp = Fingerprint.Make(f, s.R);
            var pfp = Fingerprint.Make(f, s.PriceR);
            cellsAll = Fingerprint.Cells(fp);
            if (!s.Pending && (s.Card == null || s.Stale) && now - s.ChangeT >= RetryMs * (1 << Math.Min(3, s.Fails)))
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

        if (!dirty && Rescan(f, now)) dirty = true;
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

    /// <summary>
    /// 가격 줄에 빨간 '매진' 글자가 있는지 (OCR 없이 색으로). 가격 숫자는 흰색, 보석 아이콘은 보라색이라 구분됨.
    /// </summary>
    public static bool SoldByColor(Frame f, Box pr)
    {
        int x0 = Math.Max(0, (int)pr.X1), x1 = Math.Min(f.W, (int)pr.X2), y0 = Math.Max(0, (int)pr.Y1), y1 = Math.Min(f.H, (int)pr.Y2);
        int red = 0, total = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int o = (y * f.W + x) * 4;
                int b = f.Px[o], g = f.Px[o + 1], r = f.Px[o + 2];
                total++;
                if (r > 140 && r > g * 1.7 && r > b * 1.7) red++;
            }
        return total > 0 && red > total * 0.004;   // 실제 화면: 매진 칸 약 1.6%, 일반 칸 0%
    }

    void Resolve(Frame f, Slot s, byte[] fp, byte[] pfp, double now)
    {
        var before = s.Card;
        ResolveInner(f, s, fp, pfp, now);
        // 팔던 칸이 매진으로 바뀜 = 구매
        if (before is { Sold: false } && s.Card is { Sold: true }) NoteBought(before);
    }

    void NoteBought(Card c)
    {
        if (c.Status is Verdict.Good or Verdict.Bad or Verdict.Minor && c.Diff is double d)
        {
            Bought++; BoughtGain += d;
            Log?.Invoke($"구매: {c.Name} x{c.Qty} {c.Price}휘 → 이득 {d:+0.##;-0.##}휘");
        }
        else { UnknownBought++; Log?.Invoke($"구매 (이득 모름): {c.Name} x{c.Qty} {c.Price}휘"); }
    }

    void ResolveInner(Frame f, Slot s, byte[] fp, byte[] pfp, double now)
    {
        s.Pending = false;
        s.ChangeT = now;
        s.SettledFp = fp; s.SettledPfp = pfp;
        if (SoldByColor(f, s.PriceR))
        {
            // 매진: 판정할 것이 없으므로 OCR 없이 바로 건너뜀
            s.Card = new Card { Name = s.Card?.Name ?? "", Qty = s.Card?.Qty ?? 1, Sold = true, Status = Verdict.Sold, Raw = "매진(색)" };
            s.Stale = false; s.Fails = 0;
            return;
        }
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
        s.Fails = card == null ? s.Fails + 1 : 0;   // 계속 못 읽으면 0.5 → 1 → 2 → 4초 간격으로
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

    /// <summary>영역을 잘라 대비를 올리고 확대해서 OCR. 좌표는 화면 기준으로 되돌림. digits=true 면 영어 OCR (없으면 null).</summary>
    List<List<OcrWord>>? Read(Frame src, Box r, double scale, bool digits)
    {
        var (img, sc) = Prep(src, r, scale);
        return ReadPrepped(img, r, sc, digits);
    }

    /// <summary>OCR 할 그림 준비 (자르기·대비·확대). 결과는 새 그림이라 화면 버퍼가 바뀌어도 안전.</summary>
    (Frame img, double sc) Prep(Frame src, Box r, double scale)
    {
        var crop = src.Crop((int)r.X1, (int)r.Y1, (int)r.W, (int)r.H).AutoContrast();
        double sc = Math.Min(scale, ocr.MaxDim / (double)Math.Max(crop.W, crop.H));
        return (Math.Abs(sc - 1) > 1e-6 ? crop.Scale(sc) : crop, sc);
    }

    List<List<OcrWord>>? ReadPrepped(Frame img, Box r, double sc, bool digits)
    {
        var lines = digits ? ocr.RecognizeDigits(img, r, sc) : ocr.Recognize(img, r, sc);
        if (lines == null) return null;
        foreach (var ws in lines)
            foreach (var w in ws) { w.X = w.X / sc + r.X1; w.Y = w.Y / sc + r.Y1; w.W /= sc; w.H /= sc; }
        return lines;
    }

    /// <summary>영어 OCR 결과에서 가격(가격 줄의 숫자)과 개수(X 뒤 숫자, 또는 이름 둘째 줄의 숫자).</summary>
    (int? price, int? qty) DigitsFrom(List<List<OcrWord>>? en, Slot s) { var (p, q, _) = DigitsFromEx(en, s); return (p, q); }

    /// <summary>qtyMarked = 개수를 "X 숫자" 로 읽었는지 (숫자만 읽은 것보다 믿을 만함)</summary>
    (int? price, int? qty, bool qtyMarked) DigitsFromEx(List<List<OcrWord>>? en, Slot s)
    {
        if (en == null) return (null, null, false);
        var segs = Parser.ToSegments(en);
        double h = Math.Max(4, s.PriceR.H / 1.4);
        int? price = null, qty = null;
        var ps = segs.Where(g => g.B.Cx > s.R.X1 && g.B.Cx < s.R.X2 && (g.B.Y1 + g.B.Y2) / 2 > s.PriceR.Y1 - h * 0.3
                                 && (g.B.Y1 + g.B.Y2) / 2 < s.PriceR.Y2 + h * 0.3 && Parser.IsPrice(g.Text))
                     .OrderByDescending(g => Parser.PriceValue(g.Text).ToString().Length).FirstOrDefault();
        if (ps != null) price = Parser.PriceValue(ps.Text);
        // 개수: "X 10" 처럼 X 뒤 숫자를 먼저 씀. 숫자만 있는 글자는 가격 바로 위 (이름 둘째 줄) 에 있을 때만 씀.
        // 둘 다 가격에 가장 가까운 것 (카드 그림 쪽에서 잘못 읽힌 숫자가 개수로 들어가지 않게)
        (double y, int v)? marked = null, plain = null;
        foreach (var g in segs.Where(g => g.B.Cx > s.R.X1 && g.B.Cx < s.R.X2 && g.B.Y2 > s.R.Y1 && (g.B.Y1 + g.B.Y2) / 2 < s.PriceR.Y1 - h * 0.3))
        {
            double cy = (g.B.Y1 + g.B.Y2) / 2;
            var t = Parser.Norm(g.Text);
            var m = System.Text.RegularExpressions.Regex.Match(t, @"[XxХх×&*]\s*([0-9]{1,4})$");
            // 상점에 나오지 않는 개수(예: 28)는 잘못 읽은 것으로 보고 버림
            if (m.Success) { int v = int.Parse(m.Groups[1].Value); if (cfg.QtyAllowed(v) && (marked == null || cy > marked.Value.y)) marked = (cy, v); continue; }
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[0-9]{1,4}$") && cy > s.PriceR.Y1 - h * 2.2 && cfg.QtyAllowed(int.Parse(t)) && (plain == null || cy > plain.Value.y))
                plain = (cy, int.Parse(t));
        }
        qty = marked?.v ?? plain?.v;
        return (price, qty, marked != null);
    }

    Card? OcrSlot(Frame f, Slot s)
    {
        var src = Src(f);
        var lines = Read(src, s.R, cfg.SlotOcrScale, digits: false)!;
        var (cards, _) = Parser.Analyze(lines, cfg);
        Card? card = cards.OrderBy(c => Math.Abs(c.B.Cx - s.R.Cx)).FirstOrDefault();
        // 숫자는 영어 OCR 로 한 번 더 (한국어 OCR 이 200 → 2예, 60 → 匍 처럼 읽는 경우)
        var (enPrice, enQty) = DigitsFrom(Read(src, s.R, cfg.SlotOcrScale, digits: true), s);
        var nameLines = lines.Where(l => l.Count > 0 && l.Average(w => w.Y + w.H / 2) < s.PriceR.Y1).ToList();
        if (card == null)
        {
            // 작은 숫자(예: 가격 6)는 칸 전체로 읽으면 놓치는 경우가 많음 → 가격 줄만 크게 확대해서 다시
            var plines = Read(src, s.PriceR, 4.0, digits: false)!;
            (cards, _) = Parser.Analyze(nameLines.Concat(plines).ToList(), cfg);
            card = cards.OrderBy(c => Math.Abs(c.B.Cx - s.R.Cx)).FirstOrDefault();
            if (card == null && enPrice == null) enPrice = DigitsFrom(Read(src, s.PriceR, 4.0, digits: true), s).price;
            if (card == null && enPrice is int ep0)
            {
                card = Parser.MakeCard(nameLines, ep0);   // 이름은 한국어, 가격은 영어 OCR
                if (card != null) Parser.Evaluate(card, cfg);
            }
            if (card == null)
            {
                Log?.Invoke("칸 읽기 실패: " + string.Join(" | ", lines.Concat(plines).Select(l => string.Join(" ", l.Select(w => w.Text)))));
                return null;
            }
        }
        ApplyDigits(card, enPrice, enQty);
        return card;
    }

    /// <summary>영어 OCR 로 읽은 가격·개수로 바로잡음.</summary>
    void ApplyDigits(Card card, int? enPrice, int? enQty, bool quiet = false)
    {
        bool fixedUp = false;
        if (!card.Sold && enPrice is int ep && ep != card.Price) { if (!quiet) Log?.Invoke($"가격 {card.Price} → {ep} (영어 OCR)"); card.Price = ep; fixedUp = true; }
        // 개수: 한국어 결과에 X 가 있거나 2개 이상일 때만 영어 쪽 숫자로 바로잡음 (개수 없는 상품에 엉뚱한 숫자가 붙지 않게)
        if (enQty is int eq && eq > 0 && eq != card.Qty && cfg.QtyAllowed(eq) && (card.Qty > 1 || Parser.HasQtyMark(card.Raw)))
        { if (!quiet) Log?.Invoke($"개수 {card.Qty} → {eq} (영어 OCR)"); card.Qty = eq; fixedUp = true; }
        if (fixedUp) Parser.Evaluate(card, cfg);
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
        slots = BuildSlots(cards, segs, f);
        refresh = rf?.B;
        // 화면 전체도 영어 OCR 로 한 번 더 읽어 숫자 바로잡기 (상점을 처음 열 때 한 번)
        var enAll = ocr.RecognizeDigits(img, src, sc);
        if (enAll != null)
            foreach (var ws in enAll)
                foreach (var w in ws) { w.X /= sc; w.Y = w.Y / sc + top; w.W /= sc; w.H /= sc; }
        foreach (var s in slots)
        {
            if (enAll != null)
            {
                var c0 = cards.FirstOrDefault(c => c.B.Cx >= s.R.X1 && c.B.Cx <= s.R.X2 && c.PriceBox.Y1 >= s.R.Y1 && c.PriceBox.Y1 <= s.R.Y2);
                if (c0 != null) { var (p0, q0) = DigitsFrom(enAll, s); ApplyDigits(c0, p0, q0); }
            }
            s.SettledFp = Fingerprint.Make(f, s.R);
            s.SettledPfp = Fingerprint.Make(f, s.PriceR);
            s.Card = cards.FirstOrDefault(c => c.B.Cx >= s.R.X1 && c.B.Cx <= s.R.X2 && c.PriceBox.Y1 >= s.R.Y1 && c.PriceBox.Y1 <= s.R.Y2);
            if (s.Card != null)
                cache.Add(new CacheEntry { Fp = s.SettledFp, Pfp = s.SettledPfp, Card = s.Card.Clone() });
            else
                s.Pending = !SoldByColor(f, s.PriceR); // 처음 스캔에서 못 읽은 칸은 칸 단위로 다시 읽음 (매진이면 건너뜀)
            if (s.Card == null && !s.Pending) s.Card = new Card { Sold = true, Status = Verdict.Sold, Raw = "매진(색)" };
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

    static List<Slot> BuildSlots(List<Card> cards, List<Seg>? segs = null, Frame? f = null)
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
            // 글자를 하나도 못 읽은 카드(매진되어 흐린 카드 등)도: 그 자리에 카드 그림이 있으면 칸으로 봄
            Box RAt(double x) => new(x - half, py1 - 4.6 * h, x + half, py2 + 0.2 * h);
            double refStd = f == null ? 0 : Median(cx.Select(x => LumaStd(f, RAt(x))));
            bool HasCard(double x)
            {
                if (f == null || refStd <= 0) return false;
                var b = RAt(x);
                if (b.X1 < 0 || b.X2 > f.W) return false;
                return LumaStd(f, b) > refStd * 0.35;
            }
            bool Occupied(double x) => HasName(x) || HasCard(x);
            double first = cx[0], last = cx[^1];
            for (int k = 0; k < 8 && Occupied(first - spacing); k++) first -= spacing;
            for (int k = 0; k < 8 && Occupied(last + spacing); k++) last += spacing;
            // 줄 안의 빈 칸(못 읽은 칸)도 일정한 간격으로 채움
            for (double x = first; x <= last + spacing * 0.5; x += spacing)
            {
                double near = cx.OrderBy(c => Math.Abs(c - x)).First();
                double c0 = Math.Abs(near - x) < spacing * 0.3 ? near : x;
                slots.Add(new Slot
                {
                    R = new Box(c0 - half, py1 - 4.6 * h, c0 + half, py2 + 0.2 * h),
                    PriceR = new Box(c0 - half, py1 - 0.2 * h, c0 + half, py2 + 0.2 * h),
                    // 카드 높이 ≈ 칸 간격의 1.49배 (작은 UI 191/133, 큰 UI 303/201). 글자 높이는 OCR 종류마다 달라서 간격으로 계산
                    D = new Box(c0 - dhalf, py2 + 0.6 * h - spacing * 1.49, c0 + dhalf, py2 + 0.6 * h),
                });
            }
        }
        return slots;
    }

    /// <summary>칸 영역 밝기의 표준편차 (카드 그림·글자가 있으면 크고, 빈 배경이면 작음).</summary>
    static double LumaStd(Frame f, Box b)
    {
        int x0 = Math.Max(0, (int)b.X1), x1 = Math.Min(f.W, (int)b.X2), y0 = Math.Max(0, (int)b.Y1), y1 = Math.Min(f.H, (int)b.Y2);
        if (x1 - x0 < 4 || y1 - y0 < 4) return 0;
        double sum = 0, sq = 0; int n = 0;
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                int o = (y * f.W + x) * 4;
                double v = (f.Px[o] * 29 + f.Px[o + 1] * 150 + f.Px[o + 2] * 77) >> 8;
                sum += v; sq += v * v; n++;
            }
        double m = sum / n;
        return Math.Sqrt(Math.Max(0, sq / n - m * m));
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
            // 상점을 처음 열었을 때 화면(prev 없음)은 새로고침이 아님
            if (prev != null) { Refreshes++; Log?.Invoke($"새로고침 감지 #{Refreshes} · {diff}칸 바뀜"); }
        }
    }

    // ---------------- 순이익 (프로그램을 켠 뒤부터) ----------------
    public int Refreshes, Bought, UnknownBought;
    public double BoughtGain;   // 휘석
    public void ResetProfit() { Refreshes = Bought = UnknownBought = 0; BoughtGain = 0; }

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
        Refreshes = Refreshes, Bought = Bought, UnknownBought = UnknownBought,
        NetGain = BoughtGain - Refreshes * cfg.RefreshCost,
    };
}

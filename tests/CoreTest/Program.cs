using ShopHelper;

// ---- 가짜 화면: 스크린샷(1920x1080)과 같은 배치 ----
sealed record TextItem(string Text, Box B);

sealed class Scene
{
    public List<TextItem> Items = new();
    static readonly double[] Xs = { 443, 576, 709, 841, 974, 1107, 1240, 1372 };

    public static Scene Make(string[][] names, string[] prices)
    {
        var s = new Scene();
        void Put(string text, double cx, double y)
        {
            var parts = text.Split(' ');
            double total = parts.Sum(p => p.Length * 12) + (parts.Length - 1) * 4;
            double x = cx - total / 2;
            foreach (var p in parts) { s.Items.Add(new TextItem(p, new Box(x, y, x + p.Length * 12, y + 14))); x += p.Length * 12 + 4; }
        }
        for (int i = 0; i < 8; i++)
        {
            if (names[i].Length == 2) { Put(names[i][0], Xs[i], 200); Put(names[i][1], Xs[i], 220); }
            else Put(names[i][0], Xs[i], 220);
            Put(prices[i], Xs[i], 253);
        }
        Put("신들의 강복", 140, 18); Put("385", 1780, 18); Put("50", 1750, 772); Put("새로고침", 1745, 806);
        return s;
    }

    public Box? Hover;   // 마우스를 올린 카드 (어둡게)

    /// <summary>UI 최대 크기 스크린샷: 6개 + 2개 두 줄, 6번째 칸은 매진.</summary>
    public static Scene MakeBig(string[][] names, string[] prices)
    {
        var s = new Scene();
        void Put(string text, double cx, double y)
        {
            var parts = text.Split(' ');
            double total = parts.Sum(p => p.Length * 20) + (parts.Length - 1) * 6;
            double x = cx - total / 2;
            foreach (var p in parts) { s.Items.Add(new TextItem(p, new Box(x, y, x + p.Length * 20, y + 22))); x += p.Length * 20 + 6; }
        }
        for (int i = 0; i < 8; i++)
        {
            double cx = 693 + 201 * (i % 6);
            double off = i < 6 ? 0 : 320;
            if (names[i].Length == 2) { Put(names[i][0], cx, 312 + off); Put(names[i][1], cx, 342 + off); }
            else Put(names[i][0], cx, 342 + off);
            Put(prices[i], cx, 397 + off);
        }
        Put("신들의 강복", 220, 28); Put("0", 1696, 30); Put("50", 1660, 902); Put("새로고침", 1646, 958);
        return s;
    }

    public Frame Render()
    {
        var f = new Frame(1920, 1080);
        for (int i = 0; i < f.Px.Length; i += 4) { f.Px[i] = 40; f.Px[i + 1] = 30; f.Px[i + 2] = 20; f.Px[i + 3] = 255; }
        foreach (var it in Items)
        {
            int seed = it.Text.Aggregate(17, (a, ch) => a * 31 + ch);
            var rnd = new Random(seed);
            for (int y = (int)it.B.Y1; y < (int)it.B.Y2; y++)
                for (int x = (int)it.B.X1; x < (int)it.B.X2; x++)
                {
                    byte v = (byte)(rnd.Next(2) == 0 ? 20 : 235);
                    int o = (y * f.W + x) * 4; f.Px[o] = v; f.Px[o + 1] = v; f.Px[o + 2] = v;
                }
        }
        if (Hover is Box hb)   // 게임처럼 카드 전체를 회색 쪽으로 어둡게: p' = 0.6p + 40
            for (int y = (int)hb.Y1; y < (int)hb.Y2; y++)
                for (int x = (int)hb.X1; x < (int)hb.X2; x++)
                {
                    int o = (y * f.W + x) * 4;
                    for (int c = 0; c < 3; c++) f.Px[o + c] = (byte)(f.Px[o + c] * 0.6 + 40);
                }
        return f;
    }
}

sealed class FakeOcr : IOcr
{
    public Scene Scene = new();
    public int Calls;
    public bool Fail;   // 툴팁 등으로 글자를 못 읽는 상황
    public HashSet<string> MissInFullScan = new();   // 전체 화면 OCR 에서만 놓치는 글자 (칸 단위로는 읽힘)
    public HashSet<string> MissUnlessPriceOnly = new(); // 가격 줄만 크게 읽을 때만 읽히는 글자 (작은 숫자)
    public HashSet<string> NeverRead = new();          // 아예 못 읽는 글자 (매진되어 흐린 카드)
    public Dictionary<string, string> MisreadOnce = new(); // 처음 한 번만 잘못 읽는 글자 (588 → 432)
    public Dictionary<string, string> KoMisread = new();   // 한국어 OCR 만 잘못 읽는 숫자 (200 → 2예, 60 → 匍)
    public bool HasEnglish;
    bool en;
    public List<List<OcrWord>>? RecognizeDigits(Frame img, Box src, double scale)
    {
        if (!HasEnglish) return null;
        en = true; try { return Recognize(img, src, scale); } finally { en = false; }
    }
    public int MaxDim => 10000;
    public List<List<OcrWord>> Recognize(Frame img, Box src, double scale)
    {
        Calls++;
        if (Fail) return new();
        bool full = src.W > 1000;
        bool priceOnly = src.H < 40;
        string Read(string t)
        {
            if (MisreadOnce.TryGetValue(t, out var wrong)) { MisreadOnce.Remove(t); return wrong; }
            if (!en && KoMisread.TryGetValue(t, out var kw)) return kw;
            return t;
        }
        var words = Scene.Items.Where(t => !(full && MissInFullScan.Contains(t.Text)) && !NeverRead.Contains(t.Text)
                                         && !(!priceOnly && MissUnlessPriceOnly.Contains(t.Text))).Where(t => t.B.X1 >= src.X1 - 1 && t.B.X2 <= src.X2 + 1 && t.B.Y1 >= src.Y1 - 1 && t.B.Y2 <= src.Y2 + 1)
            .Select(t => new OcrWord(Read(t.Text), (t.B.X1 - src.X1) * scale, (t.B.Y1 - src.Y1) * scale, t.B.W * scale, t.B.H * scale)).ToList();
        // Windows OCR 처럼 같은 높이의 단어는 한 줄로 (칸 사이까지 이어 붙임)
        return words.GroupBy(w => Math.Round(w.Y)).Select(g => g.OrderBy(w => w.X).ToList()).ToList();
    }
}

static class T
{
    static int fails;
    static void Check(bool ok, string msg) { Console.WriteLine((ok ? "  OK   " : "  FAIL ") + msg); if (!ok) fails++; }

    static readonly string[][] NamesA = {
        new[]{"이계의 메아리 X","10"}, new[]{"에너지 코어 X","10"}, new[]{"승화의 쐐기(매","직) X 20"}, new[]{"최초의 불꽃","가루 X 60"},
        new[]{"이계의 메아리 X","20"}, new[]{"최초의 불꽃","결정 X 10"}, new[]{"최초의 불꽃","결정"}, new[]{"신비의 휘석(소)"} };
    static readonly string[] PricesA = { "◆66", "77", "13", "12", "107", "390", "56", "200" };
    static readonly string[][] NamesB = {
        new[]{"승화의 쐐기(레","어) X 10"}, new[]{"에너지 코어 X","20"}, new[]{"절대자 엠버"}, new[]{"이계의 메아리 X","10"},
        new[]{"최초의 불꽃","결정 X 5"}, new[]{"승화의 쐐기(레","전드)"}, new[]{"최초의 불꽃","결정"}, new[]{"에너지 코어 X","10"} };
    static readonly string[] PricesB = { "28", "41", "980", "50", "230", "300", "47", "18" };

    static void Run(Tracker t, FakeOcr ocr, Scene s, ref double now, int frames = 20)
    {
        ocr.Scene = s; var f = s.Render();
        for (int i = 0; i < frames; i++) { t.Update(f, now); now += 30; }
    }

    /// <summary>내장 OCR 로 실제 스크린샷(raw BGRA)을 읽어 칸 결과 출력.</summary>
    static int PaddleRun(string[] files)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var po = new PaddleOcr();
        Console.WriteLine($"모델 로드 {sw.ElapsedMilliseconds}ms");
        foreach (var file in files)
        {
            var b = File.ReadAllBytes(file);
            int w = BitConverter.ToInt32(b, 0), h = BitConverter.ToInt32(b, 4);
            var f = new Frame(b[8..], w, h);
            var cfg = new Config();
            var tr = new Tracker(po, cfg) { Log = m => Console.WriteLine("    log: " + m) };
            tr.Reset(0, 0);
            sw.Restart();
            double t = 0;
            for (int i = 0; i < 40; i++, t += 50) { tr.Update(f, t); if (i > 2 && tr.Snap().Slots.Count > 0 && tr.Snap().Slots.All(x => !x.Pending)) break; }
            Console.WriteLine($"== {Path.GetFileName(file)} {w}x{h} · 처리 {sw.ElapsedMilliseconds}ms");
            Console.Write(Parser.Describe(tr.Snap().Slots.Select(x => x.Card), cfg));
            var d0 = tr.Snap().Slots[0].D; Console.WriteLine($"    첫 칸 테두리: x {d0.X1:0}~{d0.X2:0}, y {d0.Y1:0}~{d0.Y2:0}");
        }
        return 0;
    }

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-config") { new Config().Save(args[1]); return 0; }
        if (args.Length >= 2 && args[0] == "--paddle") return PaddleRun(args.Skip(1).ToArray());
        var cfg = new Config { CrystalRate = 50, RereadSec = 0 };   // 테스트는 결정 1개 = 50휘석, 자동 읽기 끔 (기억 동작 확인용)
        Console.WriteLine("[1] 파서: 스크린샷 배치");
        var ocr = new FakeOcr { Scene = Scene.Make(NamesA, PricesA) };
        var (cards, refresh) = Parser.Analyze(ocr.Recognize(new Frame(1, 1), new Box(0, 0, 1920, 1080), 1), cfg);
        Console.Write(Parser.Describe(cards, cfg));
        Check(cards.Count == 8, "8칸 인식");
        Check(cards.Select(c => c.Price).SequenceEqual(new[] { 66, 77, 13, 12, 107, 390, 56, 200 }), "가격");
        Check(cards.Select(c => c.Qty).SequenceEqual(new[] { 10, 10, 20, 60, 20, 10, 1, 1 }), "수량");
        Check(cards[5].Item?.Name == "최초의 불꽃 결정" && cards[5].Diff == 110, "결정 x10 = +110");
        Check(cards[6].Diff == -6, "결정 x1 = -6");
        Check(refresh != null, "새로고침 버튼 위치");
        cfg.Mode = "resell"; foreach (var c in cards) Parser.Evaluate(c, cfg);
        Check(cards[0].Value == 49 && cards[4].Value == 97 && cards[5].Value == 500, "되팔기: 메아리10=49, 메아리20=97, 결정 세금없음");
        cfg.Mode = "use";

        Console.WriteLine("[1a] 가격: OCR 이 0 을 O 로 읽어도 (200 → 2 로 읽던 문제)");
        foreach (var (txt, want) in new (string, int)[] { ("2OO", 200), ("十5OO", 500), ("◆1O7", 107), ("◆66", 66), ("十48", 48), ("l66", 66), ("534", 534), ("1,025", 1025), ("200", 200) })
            Check(Parser.IsPrice(txt) && Parser.PriceValue(txt) == want, $"{txt} → {(Parser.IsPrice(txt) ? Parser.PriceValue(txt) : -1)}");
        foreach (var txt in new[] { "가루X60", "직)X20", "직X20", "X20", "결정", "이계의메아리X", "2OOx", "새로고침" })
            Check(!Parser.IsPrice(txt), $"{txt} 는 가격 아님");
        var gs = new Config(); var gcard = new Card { Name = "신비의휘석(소)", Qty = 1, Price = Parser.PriceValue("2OO") }; Parser.Evaluate(gcard, gs);
        Check(gcard.Diff == 0, $"신비의 휘석(소) 2OO → 200 → 기대 0 (전에는 +198휘 = +3.56결)");

        Console.WriteLine("[1b] 개수: OCR 이 X 를 놓쳐도");
        foreach (var (lines2, want) in new (string[], int)[] {
            (new[] { "최초의 불꽃", "결정 10", "390" }, 10), (new[] { "최초의 불꽃", "결정10", "390" }, 10),
            (new[] { "이계의 메아리", "20", "107" }, 20), (new[] { "최초의 불꽃", "결정", "56" }, 1), (new[] { "신비의 휘석(소)", "200" }, 1),
            (new[] { "에너지코어&", "10", "77" }, 10), (new[] { "에너지코어*", "20", "153" }, 20) })
        {
            var ws = new List<List<OcrWord>>(); double yy = 200;
            foreach (var ln in lines2) { double xx = 400; var row = new List<OcrWord>(); foreach (var w in ln.Split(' ')) { row.Add(new OcrWord(w, xx, yy, w.Length * 12, 14)); xx += w.Length * 12 + 4; } ws.Add(row); yy += 20; }
            var (qc, _) = Parser.Analyze(ws, cfg);
            Check(qc.Count == 1 && qc[0].Qty == want, $"{string.Join(" / ", lines2)} → {(qc.Count == 1 ? qc[0].Qty : -1)}개");
        }

        Console.WriteLine("[2] 추적기: 첫 스캔");
        ocr = new FakeOcr();
        var tr = new Tracker(ocr, cfg) { Log = m => Console.WriteLine("    log: " + m), RelayoutMs = 0 };
        double now = 0; tr.Reset(now);
        Run(tr, ocr, Scene.Make(NamesA, PricesA), ref now);
        var snap = tr.Snap();
        Check(snap.Slots.Count == 8 && snap.Slots.All(s => s.Card != null), "8칸 모두 결과");
        Check(ocr.Calls == 1, $"OCR 1번 (전체) - 실제 {ocr.Calls}");

        Console.WriteLine("[3] 새로고침 → 새 항목 (처음 보는 그림)");
        ocr.Calls = 0;
        Run(tr, ocr, Scene.Make(NamesB, PricesB), ref now);
        snap = tr.Snap();
        Console.Write(Parser.Describe(snap.Slots.Select(s => s.Card), cfg));
        Check(snap.Slots.Select(s => s.Card?.Price ?? -1).SequenceEqual(new[] { 28, 41, 980, 50, 230, 300, 47, 18 }), "새 가격 반영");
        Check(ocr.Calls == 8, $"칸 OCR 8번 - 실제 {ocr.Calls}");
        Check(tr.LastLatencyMs <= 90, $"반영 지연 {tr.LastLatencyMs}ms (프레임 30ms 기준)");

        Console.WriteLine("[4] 다시 처음 화면 → 기억한 그림이라 OCR 없음");
        ocr.Calls = 0;
        Run(tr, ocr, Scene.Make(NamesA, PricesA), ref now);
        snap = tr.Snap();
        Check(snap.Slots.Select(s => s.Card?.Price ?? -1).SequenceEqual(new[] { 66, 77, 13, 12, 107, 390, 56, 200 }), "가격 복원");
        Check(ocr.Calls == 0, $"OCR 0번 - 실제 {ocr.Calls}");

        Console.WriteLine("[5] 가격 한 자리만 바뀜 (66 → 68)");
        ocr.Calls = 0;
        var p = (string[])PricesA.Clone(); p[0] = "◆68";
        Run(tr, ocr, Scene.Make(NamesA, p), ref now);
        snap = tr.Snap();
        Check(snap.Slots[0].Card?.Price == 68, "68 감지");
        Check(ocr.Calls == 1, $"OCR 1번 - 실제 {ocr.Calls}");
        Check(snap.Pages >= 3, $"본 화면 수 {snap.Pages}");

        Console.WriteLine("[5b] 마우스 올림 (카드가 어두워짐) → 다시 읽지 않고 결과 유지");
        ocr.Calls = 0;
        foreach (int k in new[] { 5, 0, 7 })
        {
            double cx = new[] { 443.0, 576, 709, 841, 974, 1107, 1240, 1372 }[k];
            var hs = Scene.Make(NamesA, p); hs.Hover = new Box(cx - 55, 137, cx + 55, 329);
            Run(tr, ocr, hs, ref now, 10);
        }
        Run(tr, ocr, Scene.Make(NamesA, p), ref now, 10);   // 마우스 치움
        snap = tr.Snap();
        Check(ocr.Calls == 0, $"OCR 0번 - 실제 {ocr.Calls}");
        Check(snap.Slots[5].Card?.Price == 390 && snap.Slots[0].Card?.Price == 68 && snap.Slots[7].Card?.Price == 200, "결과 유지");

        Console.WriteLine("[5b2] 다시 읽기에 실패해도 이전 결과 유지 (번쩍임 방지)");
        var noisy = Scene.Make(NamesA, p); noisy.Items.Add(new TextItem("잡음", new Box(1090, 230, 1124, 244)));  // 결정 칸 이름 쪽에 무언가 겹침
        ocr.Fail = true;
        Run(tr, ocr, noisy, ref now, 10);
        Check(tr.Snap().Slots[5].Card?.Price == 390, "실패 중에도 390 표시");
        ocr.Fail = false;
        Run(tr, ocr, noisy, ref now, 30);   // 0.5초 뒤 다시 시도해서 읽힘
        Check(tr.Snap().Slots[5].Card?.Price == 390 && ocr.Calls > 0, "다시 시도해서 읽음");

        Console.WriteLine("[5c] 자동 대비");
        var dim = new Frame(4, 1);
        byte[] vals = { 60, 70, 80, 90 };
        for (int i = 0; i < 4; i++) { dim.Px[i * 4] = dim.Px[i * 4 + 1] = dim.Px[i * 4 + 2] = vals[i]; }
        var ac = dim.AutoContrast();
        Check(ac.Px[0] < 10 && ac.Px[12] > 245, $"60~90 → {ac.Px[0]}~{ac.Px[12]}");

        Console.WriteLine("[5d] UI 최대 크기: 6+2 두 줄, 매진 칸");
        string[][] bigNames = {
            new[]{"이계의 메아리 X","10"}, new[]{"에너지 코어 X","10"}, new[]{"승화의 쐐기(매","직) X 20"}, new[]{"최초의 불꽃","가루 X 60"},
            new[]{"이계의 메아리 X","20"}, new[]{"최초의 불꽃","결정 X 10"}, new[]{"최초의 불꽃","결정"}, new[]{"신비의 휘석(소)"} };
        string[] bigPrices = { "66", "77", "13", "12", "107", "매진", "56", "200" };
        var bocr = new FakeOcr();
        var btr = new Tracker(bocr, cfg) { Log = m => Console.WriteLine("    log: " + m) };
        double bt = 0; btr.Reset(bt);
        var bigScene = Scene.MakeBig(bigNames, bigPrices);
        Run(btr, bocr, bigScene, ref bt);
        var bs = btr.Snap();
        Console.Write(Parser.Describe(bs.Slots.Select(x => x.Card), cfg));
        Check(bs.Slots.Count == 8, $"8칸 (두 줄) - 실제 {bs.Slots.Count}");
        Check(bs.Slots.Take(6).All(x => x.R.Y2 < 430) && bs.Slots.Skip(6).All(x => x.R.Y1 > 600), "윗줄 6, 아랫줄 2");
        Check(bs.Slots[5].Card?.Status == Verdict.Sold, "6번 매진");
        Check(bs.Slots[6].Card?.Price == 56 && bs.Slots[7].Card?.Price == 200, "아랫줄 가격");
        Check(bs.Slots.All(x => x.D.X2 - x.D.X1 < 182 && x.R.X2 - x.R.X1 < 182), "비교/표시 범위가 카드 폭(182) 안");
        Check(Math.Abs(bs.Slots[0].D.Y1 - 125) < 10, $"카드 위쪽 끝 ≈125 - 계산 {bs.Slots[0].D.Y1:0}");
        bocr.Calls = 0;
        Run(btr, bocr, bigScene, ref bt, 40);
        Check(bocr.Calls == 0, $"매진 칸을 다시 읽지 않음 - OCR {bocr.Calls}번");

        Console.WriteLine("[5d2] 전체 OCR 이 가격을 놓쳐도 (534, 165, 맨 끝 500)");
        string[][] n6 = {
            new[]{"최초의 불꽃","결정"}, new[]{"에너지 코어 X","10"}, new[]{"최초의 불꽃","가루 X 60"}, new[]{"승화의 쐐기(매","직) X 20"},
            new[]{"이계의 메아리 X","100"}, new[]{"에너지 코어 X","20"}, new[]{"이계의 메아리 X","10"}, new[]{"신비의 휘석(대)"} };
        string[] p6 = { "48", "74", "6", "12", "534", "165", "71", "500" };
        var ocr6 = new FakeOcr { MissInFullScan = new() { "534", "165", "500" } };
        var tr6 = new Tracker(ocr6, cfg) { Log = m => Console.WriteLine("    log: " + m) };
        double t6 = 0; tr6.Reset(t6);
        Run(tr6, ocr6, Scene.Make(n6, p6), ref t6, 30);
        var s6 = tr6.Snap();
        Console.Write(Parser.Describe(s6.Slots.Select(x => x.Card), cfg));
        foreach (var sl in s6.Slots) Console.WriteLine($"    slot R x {sl.R.X1:0}~{sl.R.X2:0} y {sl.R.Y1:0}~{sl.R.Y2:0}");
        Check(s6.Slots.Count == 8, $"8칸 - 실제 {s6.Slots.Count}");
        Check(s6.Slots.Select(x => x.Card?.Price ?? -1).SequenceEqual(new[] { 48, 74, 6, 12, 534, 165, 71, 500 }), "칸 단위로 다시 읽어 가격 채움");
        Check(s6.Slots[7].Card?.Item?.Name == "신비의 휘석(대)" && s6.Slots[7].Card?.Status == Verdict.Gamble, "맨 끝 신비의 휘석(대) = 도박");

        Console.WriteLine("[5d3] 매진된 첫 칸(글자 못 읽음) + 가격 6 을 칸 전체로는 못 읽음 (구매 스크린샷)");
        string[][] n7 = {
            new[]{"최초의 불꽃","결정 "}, new[]{"에너지 코어 X","10"}, new[]{"최초의 불꽃","가루 X 60"}, new[]{"승화의 쐐기(매","직) X 20"},
            new[]{"이계의 메아리 X","100"}, new[]{"에너지 코어 X","20"}, new[]{"이계의 메아리 X","10"}, new[]{"신비의 휘석(대)"} };
        string[] p7 = { "매진", "74", "6", "12", "534", "165", "71", "500" };
        var ocr7 = new FakeOcr { NeverRead = new() { "결정 ", "매진" }, MissUnlessPriceOnly = new() { "6", "500" } };
        var sc7 = Scene.Make(n7, p7);
        sc7.Items.RemoveAll(t => t.Text == "최초의" && t.B.X1 < 500); sc7.Items.RemoveAll(t => t.Text == "불꽃" && t.B.X1 < 500);
        sc7.Items.Add(new TextItem("흐린카드그림", new Box(400, 196, 486, 270)));   // 글자는 못 읽지만 카드 그림은 있음
        ocr7.NeverRead.Add("흐린카드그림");
        var tr7 = new Tracker(ocr7, cfg) { Log = m => Console.WriteLine("    log: " + m) };
        double t7 = 0; tr7.Reset(t7);
        Run(tr7, ocr7, sc7, ref t7, 30);
        var s7 = tr7.Snap();
        Check(s7.Slots.Count == 8 && Math.Abs(s7.Slots[0].R.Cx - 443) < 6, $"첫 칸도 칸으로 잡음 (8칸) - 실제 {s7.Slots.Count}칸, 첫 칸 {s7.Slots[0].R.Cx:0}");
        Check(s7.Slots[2].Card?.Price == 6 && s7.Slots[7].Card?.Price == 500, $"가격 줄만 크게 다시 읽어 6, 500 - {s7.Slots[2].Card?.Price}, {s7.Slots[7].Card?.Price}");

        Console.WriteLine("[5d4] 처음에 잘못 읽어도 자동 읽기(1초마다)로 바로잡음 (메아리 100개 588 → 432)");
        var p8 = (string[])p6.Clone(); p8[4] = "588";
        var ocr8 = new FakeOcr { MisreadOnce = new() { ["588"] = "432" } };
        var cfg8 = new Config { CrystalRate = 50, RereadSec = 1 };
        var tr8 = new Tracker(ocr8, cfg8) { Log = m => Console.WriteLine("    log: " + m), RelayoutMs = 0 };
        double t8 = 0; tr8.Reset(t8);
        Run(tr8, ocr8, Scene.Make(n6, p8), ref t8, 22);   // 첫 스캔 (0.25초 + 화면이 0.3초 멈춘 뒤)
        Check(tr8.Snap().Slots[4].Card?.Price == 432, "처음엔 432 로 잘못 읽음");
        Run(tr8, ocr8, Scene.Make(n6, p8), ref t8, 40);   // 1.2초
        Check(tr8.Snap().Slots[4].Card?.Price == 588 && tr8.Snap().Slots[4].Card?.Status == Verdict.Bad, "1초 뒤 588 로 고쳐 손해로 표시");
        cfg8.RereadSec = 0; ocr8.Calls = 0;
        Run(tr8, ocr8, Scene.Make(n6, p8), ref t8, 80);
        Check(ocr8.Calls == 0, $"끄기면 다시 읽지 않음 - OCR {ocr8.Calls}번");

        Console.WriteLine("[5d5] 한국어 OCR 이 숫자를 한글로 읽어도 영어 OCR 로 바로잡음 (200 → 2예, 60 → 匍)");
        string[][] n9 = {
            new[]{"최초의 불꽃","결정"}, new[]{"승화의 쐐기(매","직) X 20"}, new[]{"에너지 코어 X","10"}, new[]{"이계의 메아리 X","10"},
            new[]{"이계의 메아리 X","20"}, new[]{"최초의 불꽃","가루 X 60"}, new[]{"최초의 불꽃","가루 X 20"}, new[]{"신비의 휘석(소)"} };
        string[] p9 = { "57", "8", "57", "40", "171", "10", "3", "200" };
        foreach (bool withEn in new[] { false, true })
        {
            var ocr9 = new FakeOcr { KoMisread = new() { ["200"] = "2예", ["60"] = "匍" }, HasEnglish = withEn };
            var tr9 = new Tracker(ocr9, cfg);
            double t9 = 0; tr9.Reset(t9);
            Run(tr9, ocr9, Scene.Make(n9, p9), ref t9, 30);
            var s9 = tr9.Snap();
            string got = $"가루 x{s9.Slots[5].Card?.Qty}, 신비 {(s9.Slots[7].Card == null ? "읽기 실패" : s9.Slots[7].Card!.Price + "휘")}";
            if (withEn) Check(s9.Slots[5].Card?.Qty == 60 && s9.Slots[7].Card?.Price == 200 && s9.Slots[7].Card?.Diff == 0, "영어 OCR 있음: " + got);
            else Console.WriteLine("    (영어 OCR 없으면: " + got + ")");
        }

        Console.WriteLine("[5e] 오버레이 글자가 캡처에 섞여도 (스크린샷 찐빠 재현)");
        var mixed = Scene.Make(NamesA, PricesA);
        void Add(string t, double x, double y, double w, double hh = 14) => mixed.Items.Add(new TextItem(t, new Box(x, y, x + w, y + hh)));
        Add("읽기", 566, 48, 24, 11); Add("실패", 594, 48, 24, 11);            // 카드 위 표시
        Add("-0.38결", 962, 83, 44, 11); Add("구매", 1325, 82, 34, 16); Add("+3.96결", 1375, 82, 66, 16);
        Add("기대", 1305, 113, 20, 9); Add("+3.96결", 1328, 113, 36, 9);
        Add("모르는", 24, 320, 40, 11); Add("항목", 66, 320, 24, 11);           // 왼쪽 가짜 칸
        Add("이", 30, 440, 8, 11); Add("화면", 42, 440, 24, 11); Add("+3.96결", 32, 460, 104, 28);
        Add("평균", 30, 512, 24, 11); Add("이득", 58, 512, 24, 11); Add("+0.66결", 86, 512, 40, 11); Add("124ms", 214, 512, 34, 11);
        var mocr = new FakeOcr { Scene = mixed };
        var (mc, _) = Parser.Analyze(mocr.Recognize(new Frame(1, 1), new Box(0, 0, 1920, 1080), 1), cfg);
        Check(mc.Count == 8 && mc.Select(c => c.Price).SequenceEqual(new[] { 66, 77, 13, 12, 107, 390, 56, 200 }), $"8칸·가격 정상 - {string.Join(",", mc.Select(c => c.Price))}");
        Check(Parser.IsOverlayText("+3.96결") && Parser.IsOverlayText("-0.38결") && Parser.IsOverlayText("구매") && !Parser.IsOverlayText("결정") && !Parser.IsOverlayText("최초의불꽃"), "오버레이 글자 판별");

        Console.WriteLine("[5f] 상점이 열리며 카드가 움직이는 동안에는 칸 위치를 잡지 않음");
        var ocrA = new FakeOcr(); var trA = new Tracker(ocrA, cfg); double tA = 0; trA.Reset(tA);
        for (int i = 0; i < 20; i++)   // 0.6초 동안 카드가 오른쪽에서 밀려 들어옴
        {
            var moving = Scene.Make(NamesA, PricesA);
            double dx = (20 - i) * 6;
            moving.Items = moving.Items.Select(t => t.B.Y1 > 150 && t.B.Y1 < 300 ? t with { B = t.B.Offset(dx, 0) } : t).ToList();
            ocrA.Scene = moving; trA.Update(moving.Render(), tA); tA += 30;
        }
        Run(trA, ocrA, Scene.Make(NamesA, PricesA), ref tA, 20);
        var sA = trA.Snap();
        Check(sA.Slots.Count == 8 && Math.Abs(sA.Slots[0].R.Cx - 443) < 4 && Math.Abs(sA.Slots[7].R.Cx - 1372) < 4,
              $"멈춘 뒤의 위치로 잡음 (첫 칸 {sA.Slots[0].R.Cx:0}, 끝 칸 {sA.Slots[7].R.Cx:0})");

        Console.WriteLine("[6] 시세 모드 변경 후 재평가");
        cfg.Mode = "resell"; tr.Reevaluate();
        Check(tr.Snap().Slots[3].Card?.Status == Verdict.Unknown, "불꽃 가루는 시세 없음");

        Console.WriteLine("[7] 시세 입력 해석 (1개당 결정)");
        double? v;
        Check(PriceInput.TryParse("0.11", 50, out v) && v == 0.11, "0.11");
        Check(PriceInput.TryParse("1/20", 50, out v) && v == 0.05, "1/20 = 0.05");
        Check(PriceInput.TryParse("5.5휘", 50, out v) && v == 0.11, "5.5휘 = 0.11");
        Check(PriceInput.TryParse("40휘/20", 50, out v) && v == 0.04, "40휘/20 = 0.04");
        Check(PriceInput.TryParse("20.5결", 50, out v) && v == 20.5, "20.5결 = 20.5");
        Check(PriceInput.TryParse("", 50, out v) && v == null, "빈칸 = 시세 없음");
        Check(!PriceInput.TryParse("abc", 50, out _) && !PriceInput.TryParse("5/0", 50, out _), "잘못된 입력 거부");

        Check(Parser.FmtPrice(0.006) == "0.006" && Parser.FmtPrice(0.11) == "0.11" && Parser.FmtPrice(0.123456) == "0.12346" && Parser.FmtPrice(20.5) == "20.5" && Parser.FmtPrice(50) == "50",
              "시세 표시 소수점 5자리 (0.006, 0.11, 0.12346, 20.5, 50)");
        foreach (var it in new Config().Items.Where(i => i.Price is double))
            Check(PriceInput.TryParse(Parser.FmtPrice(it.Price!.Value), 50, out var back) && back == it.Price, $"{it.Name}: 표시→다시 읽기 그대로 ({Parser.FmtPrice(it.Price!.Value)})");

        Console.WriteLine("[8] 신비의 휘석(소) 도박");
        var gc = new Config();
        Card G(int price) { var c = new Card { Name = "신비의휘석(소)", Qty = 1, Price = price }; Parser.Evaluate(c, gc); return c; }
        var g200 = G(200); var g150 = G(150);
        Check(g200.Status == Verdict.Gamble && g200.Diff == 0 && g200.RangeLo == -150 && g200.RangeHi == 150, "200휘석: 기대 0, -150~+150");
        Check(g150.Status == Verdict.Good && g150.Diff == 50, "150휘석: 기대 +50 → 구매");

        Card GL(int price) { var c = new Card { Name = "신비의휘석(대)", Qty = 1, Price = price }; Parser.Evaluate(c, gc); return c; }
        var gl = GL(500);
        Check(gl.Status == Verdict.Gamble && gl.Diff == 0 && gl.RangeLo == -400 && gl.RangeHi == 400, "대 500휘석: 기대 0, -400~+400 (100~900)");
        var oldG = new Config(); oldG.Items.Single(i => i.Name == "신비의 휘석(대)").GambleMin = null; oldG.Items.Single(i => i.Name == "신비의 휘석(대)").GambleMax = null;
        Check(oldG.Migrate() && oldG.Items.Single(i => i.Name == "신비의 휘석(대)").GambleMax == 900, "예전 prices.json 의 빈 범위를 100~900 으로 채움");

        Console.WriteLine("[9] 예전 설정(1개당 휘석) 자동 변환");
        var old = new Config { PriceUnit = "", Items = new() {
            new() { Name = "최초의 불꽃 결정", Price = 50, Taxed = false }, new() { Name = "이계의 메아리", Price = 5.5 },
            new() { Name = "절대자 엠버", Price = 1025 }, new() { Name = "신비의 휘석(소)", Price = null } } };
        Check(old.Migrate(), "변환함");
        Check(old.CrystalRate == 50 && old.Items[0].Price == 1 && old.Items[1].Price == 0.11 && old.Items[2].Price == 20.5, "50휘→1결, 5.5휘→0.11결, 1025휘→20.5결");
        Check(old.Items.Single(i => i.Name == "신비의 휘석(소)").IsGamble, "신비의 휘석 → 도박 항목");
        Check(!old.Migrate(), "두 번째는 변환 안 함");
        var tmp = Path.Combine(Path.GetTempPath(), "shophelper_old_config.json");
        File.WriteAllText(tmp, "{ \"mode\": \"resell\", \"items\": [ { \"name\": \"최초의 불꽃 결정\", \"price\": 50, \"taxed\": false }, { \"name\": \"에너지 코어\", \"price\": 2 } ] }");
        var loaded = Config.Load(tmp);
        Check(loaded.Items.Single(i => i.Name == "에너지 코어").Price == 0.04 && loaded.PriceUnit == "crystal", "예전 파일 읽으면 자동 변환 (코어 2휘 → 0.04결)");
        Check(File.ReadAllText(tmp).Contains("price_unit"), "변환 후 파일에 저장");

        Console.WriteLine("[10] 업데이트해도 입력한 시세 유지");
        var mine = new Config();
        mine.Items.Single(i => i.Name == "이계의 메아리").Price = 0.13;
        mine.Items.RemoveAll(i => i.Name == "절대자 엠버");   // 예전 버전에 없던 항목이라고 가정
        mine.Save(tmp);
        var re = Config.Load(tmp);
        Check(re.Items.Single(i => i.Name == "이계의 메아리").Price == 0.13, "메아리 0.13 그대로");
        Check(re.Items.Single(i => i.Name == "절대자 엠버").Price == 20.5, "새 기본 항목만 추가");

        Console.WriteLine("[10b] 신력의 휘석 1개 = 0.018 결정");
        var hc = new Config();
        Check(hc.HuiseokPrice == 0.018 && Math.Abs(hc.CrystalRate - 55.5555556) < 1e-6, $"기본 0.018결 → 결정 1개 = {hc.CrystalRate:0.####}휘");
        var hdir = Path.Combine(Path.GetTempPath(), "shophelper_h_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(hdir);
        hc.HuiseokPrice = 0.017; hc.SaveSplit(Path.Combine(hdir, "s.json"), Path.Combine(hdir, "p.json"));
        var hl = Config.LoadSplit(Path.Combine(hdir, "s.json"), Path.Combine(hdir, "p.json"));
        Check(Parser.FmtPrice(hl.HuiseokPrice) == "0.017" && File.ReadAllText(Path.Combine(hdir, "p.json")).Contains("\"huiseok_price\": 0.017"), "저장·불러오기 후에도 0.017 그대로");
        var ec = new Card { Name = "에너지코어", Qty = 20, Price = 40 }; Parser.Evaluate(ec, hl);
        Check(ec.Value == Math.Ceiling(Math.Round(0.04 / 0.017 * 20 * 0.875, 6)), $"계산에 반영 (코어 20개 결정화 = {ec.Value}휘)");
        Directory.Delete(hdir, true);

        Console.WriteLine("[11] 시세(prices.json) / 설정(settings.json) 분리");
        var dir = Path.Combine(Path.GetTempPath(), "shophelper_split_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string sp = Path.Combine(dir, "settings.json"), pp = Path.Combine(dir, "prices.json"), lp = Path.Combine(dir, "config.json");
        var legacyCfg = new Config(); legacyCfg.Items.Single(i => i.Name == "에너지 코어").Price = 0.05; legacyCfg.Mode = "use"; legacyCfg.Save(lp);
        var c1 = Config.LoadSplit(sp, pp, lp);
        Check(File.Exists(sp) && File.Exists(pp) && !File.Exists(lp) && File.Exists(lp + ".bak"), "예전 config.json → 두 파일로 나눔 (.bak 남김)");
        Check(c1.Mode == "use" && c1.Items.Single(i => i.Name == "에너지 코어").Price == 0.05, "값 그대로 옮김");
        Check(!File.ReadAllText(sp).Contains("items") && File.ReadAllText(pp).Contains("에너지 코어"), "설정 파일엔 시세 없음, 시세 파일엔 있음");
        c1.Mode = "resell"; c1.SaveSettings(sp);
        var c2 = Config.LoadSplit(sp, pp);
        Check(c2.Mode == "resell" && c2.Items.Single(i => i.Name == "에너지 코어").Price == 0.05, "설정만 바꿔 저장해도 시세 유지");
        File.Delete(sp);   // 새 버전에서 settings.json 이 없어진 상황
        var c3 = Config.LoadSplit(sp, pp);
        Check(c3.Items.Single(i => i.Name == "에너지 코어").Price == 0.05 && File.Exists(sp), "설정 파일이 없어도 시세는 그대로");
        Directory.Delete(dir, true);

        Console.WriteLine(fails == 0 ? "\n모든 테스트 통과" : $"\n실패 {fails}개");
        return fails;
    }
}

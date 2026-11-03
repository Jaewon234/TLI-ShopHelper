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
        return f;
    }
}

sealed class FakeOcr : IOcr
{
    public Scene Scene = new();
    public int Calls;
    public int MaxDim => 10000;
    public List<List<OcrWord>> Recognize(Frame img, Box src, double scale)
    {
        Calls++;
        var words = Scene.Items.Where(t => t.B.X1 >= src.X1 - 1 && t.B.X2 <= src.X2 + 1 && t.B.Y1 >= src.Y1 - 1 && t.B.Y2 <= src.Y2 + 1)
            .Select(t => new OcrWord(t.Text, (t.B.X1 - src.X1) * scale, (t.B.Y1 - src.Y1) * scale, t.B.W * scale, t.B.H * scale)).ToList();
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

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-config") { new Config().Save(args[1]); return 0; }
        var cfg = new Config();
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

        Console.WriteLine("[2] 추적기: 첫 스캔");
        ocr = new FakeOcr();
        var tr = new Tracker(ocr, cfg) { Log = m => Console.WriteLine("    log: " + m) };
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

        Console.WriteLine("[6] 시세 모드 변경 후 재평가");
        cfg.Mode = "resell"; tr.Reevaluate();
        Check(tr.Snap().Slots[3].Card?.Status == Verdict.Unknown, "불꽃 가루는 시세 없음");

        Console.WriteLine(fails == 0 ? "\n모든 테스트 통과" : $"\n실패 {fails}개");
        return fails;
    }
}

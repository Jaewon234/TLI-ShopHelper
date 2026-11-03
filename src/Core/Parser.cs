using System.Text.RegularExpressions;

namespace ShopHelper;

/// <summary>OCR 결과(단어 + 위치)를 상점 칸으로 바꾸고 판정함. 좌표 고정 없음.</summary>
public static class Parser
{
    static double Median(IEnumerable<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a[a.Length / 2];
    }

    /// <summary>한 줄 안에서 간격이 넓은 곳을 끊어 칸별 조각으로 나눔.</summary>
    public static List<Seg> ToSegments(List<List<OcrWord>> lines, double gapK = 1.0)
    {
        var segs = new List<Seg>();
        foreach (var line in lines)
        {
            var words = line.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.X).ToList();
            if (words.Count == 0) continue;
            double h = Median(words.Select(w => w.H));
            var cur = new List<OcrWord> { words[0] };
            void Flush()
            {
                segs.Add(new Seg
                {
                    Text = string.Concat(cur.Select(w => w.Text)),
                    B = new Box(cur.Min(w => w.X), cur.Min(w => w.Y), cur.Max(w => w.X + w.W), cur.Max(w => w.Y + w.H)),
                    H = h,
                });
            }
            for (int i = 1; i < words.Count; i++)
            {
                var prev = cur[^1];
                double gap = words[i].X - (prev.X + prev.W);
                if (gap > gapK * h) { Flush(); cur = new List<OcrWord>(); }
                cur.Add(words[i]);
            }
            Flush();
        }
        segs.RemoveAll(s => IsOverlayText(s.Text));
        return segs;
    }

    static readonly Regex OverlayNum = new(@"^[+\-−]?[0-9]+(\.[0-9]+)?결", RegexOptions.Compiled);
    static readonly string[] OverlayWords =
        { "구매", "기대", "도박", "효율", "읽기실패", "모르는항목", "시세없음", "이화면", "평균이득", "본화면", "결정화기준",
          "직접사용기준", "범위미입력", "시세입력", "적용됨", "1개당결정", "입력완료" };

    /// <summary>
    /// 이 프로그램이 화면에 그린 글자인지 (+3.96결, 구매, 기대 …). 오버레이가 캡처에 섞여 들어와도
    /// 상품 이름/가격으로 착각하지 않게 버림. 게임 상품 이름에는 이런 말이 없음.
    /// </summary>
    public static bool IsOverlayText(string t)
    {
        string n = Norm(t);
        if (n.Length == 0) return false;
        if (OverlayNum.IsMatch(n)) return true;
        foreach (var w in OverlayWords) if (n.Contains(w)) return true;
        return false;
    }

    /// <summary>세로로 이어지는 조각을 하나의 칸(열)으로 묶음.</summary>
    public static List<List<Seg>> ToColumns(List<Seg> segs)
    {
        var cols = new List<List<Seg>>();
        foreach (var s in segs.OrderBy(s => s.B.Y1))
        {
            List<Seg>? target = null;
            foreach (var c in cols)
            {
                var last = c[^1];
                double x1 = c.Min(t => t.B.X1), x2 = c.Max(t => t.B.X2);
                double ov = Math.Min(x2, s.B.X2) - Math.Max(x1, s.B.X1);
                bool closeX = ov >= 0.3 * Math.Min(x2 - x1, s.B.W) || Math.Abs((x1 + x2) / 2 - s.B.Cx) < 1.5 * s.H;
                double dy = s.B.Y1 - last.B.Y2;
                double mh = Math.Max(s.H, last.H);
                bool closeY = dy >= -0.5 * mh && dy < 4.5 * mh;
                if (closeX && closeY) { target = c; break; }
            }
            if (target == null) cols.Add(new List<Seg> { s });
            else target.Add(s);
        }
        return cols;
    }

    static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);
    static readonly Regex NonPriceChars = new(@"[\d\s,.]", RegexOptions.Compiled);
    static readonly Regex Hangul = new(@"[가-힣]", RegexOptions.Compiled);
    static readonly Regex TrailingX = new(@"[XxХх×]$", RegexOptions.Compiled);
    static readonly Regex QtyRe = new(@"^(.*?)[XxХх×]\s*(\d+)$", RegexOptions.Compiled);
    // OCR 이 'X' 를 놓친 경우: 상품 이름은 숫자로 끝나지 않으므로 끝의 숫자를 개수로 봄 (예: 결정10, 결정 10)
    static readonly Regex QtyNoXRe = new(@"^(.*[가-힣)])\s*(\d{1,4})$", RegexOptions.Compiled);

    public static bool IsPrice(string t)
    {
        string digits = NonDigit.Replace(t, "");
        string other = NonPriceChars.Replace(t, "");
        return digits.Length > 0 && other.Length <= 2 && digits.Length <= 6;
    }

    public static string Norm(string t) => t.Replace(" ", "").Replace("（", "(").Replace("）", ")");

    public static List<Card> ToCards(List<List<Seg>> cols)
    {
        var cards = new List<Card>();
        foreach (var c in cols)
        {
            int idx = -1;
            bool sold = false;
            for (int i = c.Count - 1; i >= 0; i--)
            {
                if (IsPrice(c[i].Text)) { idx = i; break; }
                if (Norm(c[i].Text).Contains("매진")) { idx = i; sold = true; break; }
            }
            if (idx <= 0) continue;
            string nameText = Norm(string.Concat(c.Take(idx).Select(s => s.Text)));
            if (!Hangul.IsMatch(nameText)) continue;
            if (TrailingX.IsMatch(nameText)) continue; // 수량 뒤 가격을 못 읽음
            string name; int qty;
            var m = QtyRe.Match(nameText);
            if (!m.Success) m = QtyNoXRe.Match(nameText);
            if (m.Success) { name = m.Groups[1].Value; qty = int.Parse(m.Groups[2].Value); }
            else { name = nameText; qty = 1; }
            if (qty <= 0) continue;
            var used = c.Take(idx + 1).ToList();
            cards.Add(new Card
            {
                Raw = string.Join(" / ", used.Select(s => s.Text)),
                Name = name,
                Qty = qty,
                Price = sold ? 0 : int.Parse(NonDigit.Replace(c[idx].Text, "")),
                Sold = sold,
                B = new Box(used.Min(s => s.B.X1), used.Min(s => s.B.Y1), used.Max(s => s.B.X2), used.Max(s => s.B.Y2)),
                PriceBox = c[idx].B,
                H = Median(c.Select(s => s.H)),
            });
        }
        if (cards.Count == 0) return cards;
        // UI 크기에 따라 칸이 한 줄(8개) 또는 여러 줄(6+2 등)로 나옴.
        // 글자 크기가 다른 것(다른 UI 글자)은 빼고, 줄 순서(위→아래, 왼→오른)로 정렬.
        double mh = Median(cards.Select(c => c.H));
        cards = cards.Where(c => c.H > mh * 0.6 && c.H < mh * 1.6).ToList();
        return KeepGridRows(SortRows(cards));
    }

    /// <summary>가격 줄 높이가 같은 카드끼리 한 줄로 묶어서 위→아래, 왼→오른 순서로.</summary>
    public static List<Card> SortRows(List<Card> cards)
    {
        var rows = new List<List<Card>>();
        foreach (var cd in cards.OrderBy(c => c.PriceBox.Y1))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].PriceBox.Y1 - cd.PriceBox.Y1) < 1.5 * cd.H);
            if (row == null) rows.Add(new List<Card> { cd }); else row.Add(cd);
        }
        return rows.SelectMany(r => r.OrderBy(c => c.B.X1)).ToList();
    }

    /// <summary>
    /// 가장 긴 줄을 기준 줄로 보고, 다른 줄은 칸이 기준 줄의 세로줄(같은 간격)에 맞을 때만 남김.
    /// 두 줄 배치(6+2)는 남고, 엉뚱한 위치의 글자로 생긴 가짜 칸은 빠짐.
    /// </summary>
    static List<Card> KeepGridRows(List<Card> cards)
    {
        if (cards.Count == 0) return cards;
        var rows = new List<List<Card>>();
        foreach (var cd in cards)
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].PriceBox.Y1 - cd.PriceBox.Y1) < 1.5 * cd.H);
            if (row == null) rows.Add(new List<Card> { cd }); else row.Add(cd);
        }
        var main = rows.OrderByDescending(r => r.Count).ThenBy(r => r[0].PriceBox.Y1).First();
        if (main.Count < 2) return main;
        var xs = main.Select(c => c.B.Cx).OrderBy(x => x).ToList();
        double spacing = double.MaxValue;
        for (int i = 1; i < xs.Count; i++) spacing = Math.Min(spacing, xs[i] - xs[i - 1]);
        bool OnGrid(Card c)
        {
            double f = (c.B.Cx - xs[0]) / spacing;
            return f > -0.25 && Math.Abs(f - Math.Round(f)) < 0.25;
        }
        double mainH = Median(main.Select(c => c.H));
        return rows.Where(r => r == main || (r.All(OnGrid) && r.All(c => Math.Abs(c.H - mainH) < mainH * 0.3)))
                   .SelectMany(r => r).ToList();
    }

    /// <summary>difflib.SequenceMatcher.ratio 와 비슷한 유사도 (LCS 기반).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length + b.Length == 0) return 1;
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return 2.0 * dp[a.Length, b.Length] / (a.Length + b.Length);
    }

    public static ItemDef? Match(string name, List<ItemDef> items)
    {
        string n = Norm(name);
        ItemDef? best = null; double bestR = 0;
        foreach (var it in items)
            foreach (var cand in it.Aliases.Prepend(it.Name))
            {
                double r = Similarity(n, Norm(cand));
                if (r > bestR) { bestR = r; best = it; }
            }
        return bestR >= 0.6 ? best : null;
    }

    public static void Evaluate(Card cd, Config cfg)
    {
        cd.Item = Match(cd.Name, cfg.Items);
        cd.Value = null; cd.Diff = null;
        cd.RangeLo = cd.RangeHi = null;
        if (cd.Sold) { cd.Status = Verdict.Sold; return; }
        if (cd.Item is { IsGamble: true } g)
        {
            // 도박: 평균 획득 휘석으로 기대 이득, 최소/최대로 범위 (휘석으로 받으니 수수료 없음)
            double? avg = g.GambleAvg ?? (g.GambleMin + g.GambleMax) / 2;
            if (avg is not double a) { cd.Status = Verdict.Gamble; return; }   // 범위 미입력
            cd.Value = a * cd.Qty;
            cd.Diff = Math.Round(cd.Value.Value - cd.Price, 2);
            if (g.GambleMin is double lo) cd.RangeLo = lo * cd.Qty - cd.Price;
            if (g.GambleMax is double hi) cd.RangeHi = hi * cd.Qty - cd.Price;
            cd.Status = cd.Diff > 0 ? Verdict.Good : Verdict.Gamble;
            return;
        }
        if (cd.Item?.Price is not double unit) { cd.Status = Verdict.Unknown; return; }
        double market = unit * cfg.CrystalRate * cd.Qty;   // 휘석
        cd.Value = cfg.Resell && cd.Item.Taxed
            ? Math.Ceiling(Math.Round(market * (1 - cfg.TaxRate), 6))
            : Math.Round(market, 6);
        cd.Diff = Math.Round(cd.Value.Value - cd.Price, 2);
        cd.Status = cd.Diff > 0 ? Verdict.Good : Verdict.Bad;
    }

    public static Seg? FindRefresh(List<Seg> segs) =>
        segs.FirstOrDefault(s => Norm(s.Text).Contains("새로고침")) ?? segs.FirstOrDefault(s => Norm(s.Text).Contains("새로"));

    public static (List<Card> cards, Seg? refresh) Analyze(List<List<OcrWord>> lines, Config cfg)
    {
        var segs = ToSegments(lines);
        var cards = ToCards(ToColumns(segs));
        foreach (var c in cards) Evaluate(c, cfg);
        return (cards, FindRefresh(segs));
    }

    public static string Fmt(double? v)
    {
        if (v is not double d) return "?";
        return Math.Abs(d - Math.Round(d)) < 1e-9 ? ((long)Math.Round(d)).ToString() : d.ToString("0.#");
    }

    /// <summary>시세 표시용: 소수점 5자리까지 (0.006, 0.11 이 잘리지 않게).</summary>
    public static string FmtPrice(double v) => Math.Round(v, 5).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

    public static string Describe(IEnumerable<Card?> cards, Config cfg)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- 기준: {(cfg.Resell ? "되팔기(세금 반영)" : "직접 사용(시세)")} ---");
        int i = 0; double total = 0;
        foreach (var c in cards)
        {
            i++;
            if (c == null) { sb.AppendLine($"{i}. (읽기 실패)"); continue; }
            string nm = c.Item?.Name ?? $"(모름) {c.Name}";
            string tag = c.Status switch { Verdict.Good => "이득", Verdict.Bad => "손해", Verdict.Sold => "매진", Verdict.Gamble => "도박", _ => "시세없음" };
            sb.AppendLine($"{i}. {nm} x{c.Qty} | 가격 {c.Price} | 가치 {Fmt(c.Value)} | {tag} {Fmt(c.Diff)}   [{c.Raw}]");
            if (c.Status == Verdict.Good) total += c.Diff!.Value;
        }
        sb.AppendLine($"이 화면 이득 합계: +{Fmt(total)}");
        return sb.ToString();
    }
}

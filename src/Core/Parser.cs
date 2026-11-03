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
        return segs;
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
            for (int i = c.Count - 1; i >= 0; i--)
                if (IsPrice(c[i].Text)) { idx = i; break; }
            if (idx <= 0) continue;
            string nameText = Norm(string.Concat(c.Take(idx).Select(s => s.Text)));
            if (!Hangul.IsMatch(nameText)) continue;
            if (TrailingX.IsMatch(nameText)) continue; // 수량 뒤 가격을 못 읽음
            string name; int qty;
            var m = QtyRe.Match(nameText);
            if (m.Success) { name = m.Groups[1].Value; qty = int.Parse(m.Groups[2].Value); }
            else { name = nameText; qty = 1; }
            if (qty <= 0) continue;
            var used = c.Take(idx + 1).ToList();
            cards.Add(new Card
            {
                Raw = string.Join(" / ", used.Select(s => s.Text)),
                Name = name,
                Qty = qty,
                Price = int.Parse(NonDigit.Replace(c[idx].Text, "")),
                B = new Box(used.Min(s => s.B.X1), used.Min(s => s.B.Y1), used.Max(s => s.B.X2), used.Max(s => s.B.Y2)),
                PriceBox = c[idx].B,
                H = Median(c.Select(s => s.H)),
            });
        }
        if (cards.Count == 0) return cards;
        // 상품 칸은 같은 높이(가격 줄)에 나란히 있으므로 가장 많은 줄만 남김
        var rows = new List<List<Card>>();
        foreach (var cd in cards.OrderBy(c => c.PriceBox.Y1))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].PriceBox.Y1 - cd.PriceBox.Y1) < 1.5 * cd.H);
            if (row == null) rows.Add(new List<Card> { cd }); else row.Add(cd);
        }
        return rows.OrderByDescending(r => r.Count).First().OrderBy(c => c.B.X1).ToList();
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
        if (cd.Item?.Price is not double unit) { cd.Status = Verdict.Unknown; return; }
        double market = unit * cd.Qty;
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
            string tag = c.Status switch { Verdict.Good => "이득", Verdict.Bad => "손해", _ => "시세없음" };
            sb.AppendLine($"{i}. {nm} x{c.Qty} | 가격 {c.Price} | 가치 {Fmt(c.Value)} | {tag} {Fmt(c.Diff)}   [{c.Raw}]");
            if (c.Status == Verdict.Good) total += c.Diff!.Value;
        }
        sb.AppendLine($"이 화면 이득 합계: +{Fmt(total)}");
        return sb.ToString();
    }
}

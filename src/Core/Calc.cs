using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopHelper;

// =====================================================================
// 부가 기능 계산기 2개 (트레이에서 여는 창). 계산은 여기, 화면은 App/CalcWindow.cs
//   1) 장비 제작비: 등급별 1T/0T 옵션 개수 → 기대 제작비
//   2) 부귀·숭고 스킬 레벨업: 게임 가격 검색으로 기록한 재료 스킬 매물로 상승치 50 이상을 가장 싸게 채우는 조합
// =====================================================================

/// <summary>제작 1회 재료 (한손 기준). 엠버 종류는 등급마다 정해져 있음.</summary>
public sealed class CraftStep
{
    [JsonPropertyName("crystal")] public double Crystal { get; set; }
    /// <summary>최초의 불꽃 결정 가루 (82레벨은 결정 대신 가루)</summary>
    [JsonPropertyName("dust")] public double Dust { get; set; }
    [JsonPropertyName("ember")] public double Ember { get; set; }
    [JsonPropertyName("fossil")] public double Fossil { get; set; }
    [JsonPropertyName("chance")] public double Chance { get; set; }
}

/// <summary>한 레벨의 등급 하나: 1T 붙이기와 0T 승급 (null = 불가).</summary>
public sealed class CraftGrade
{
    [JsonPropertyName("t1")] public CraftStep? T1 { get; set; }
    [JsonPropertyName("t0")] public CraftStep? T0 { get; set; }
}

public static class CraftCalc
{
    public static readonly string[] Grades = { "초급", "진급", "절대자" };
    /// <summary>등급별 엠버: 초급 = 진귀한 엠버, 진급 = 희귀한 엠버, 절대자 = 절대자 엠버</summary>
    public static readonly string[] Embers = { "진귀한 엠버", "희귀한 엠버", "절대자 엠버" };
    public const string Fossil = "신성한 화석";
    public const string Dust = "결정 가루";
    /// <summary>레벨별 최고 티어 이름 (개수 칸 머리글). 없으면 1T</summary>
    public static string TopTier(int level) => level <= 82 ? "2T" : "1T";

    /// <summary>게임 수치 (한손 1회 기준, 사용자가 알려 준 값). 양손은 전부 2배.</summary>
    public static Dictionary<int, CraftGrade[]> DefaultRecipes() => new()
    {
        [82] = new[]
        {
            new CraftGrade { T1 = new() { Dust = 5, Ember = 1, Chance = 0.035 } },
            new CraftGrade { T1 = new() { Dust = 15, Ember = 1, Chance = 0.035 } },
            new CraftGrade(),   // 절대자 불가
        },
        [86] = new[]
        {
            new CraftGrade { T1 = new() { Crystal = 1, Ember = 5, Chance = 0.0333 } },
            new CraftGrade { T1 = new() { Crystal = 3, Ember = 5, Chance = 0.0333 } },
            new CraftGrade(),   // 절대자 불가
        },
        [100] = new[]
        {
            new CraftGrade { T1 = new() { Crystal = 1, Ember = 10, Chance = 0.01 }, T0 = new() { Crystal = 30, Ember = 300, Fossil = 1, Chance = 0.30 } },
            new CraftGrade { T1 = new() { Crystal = 3, Ember = 10, Chance = 0.01 }, T0 = new() { Crystal = 90, Ember = 300, Fossil = 3, Chance = 0.30 } },
            new CraftGrade { T1 = new() { Crystal = 30, Ember = 1, Chance = 0.05 }, T0 = new() { Crystal = 180, Ember = 6, Fossil = 6, Chance = 0.30 } },
        },
    };

    /// <summary>1회 재료비 ÷ 확률 = 성공까지 평균 비용.</summary>
    static double Expected(CraftStep s, double emberPrice, double fossilPrice, double dustPrice) =>
        s.Chance <= 0 ? 0 : (s.Crystal + s.Dust * dustPrice + s.Ember * emberPrice + s.Fossil * fossilPrice) / s.Chance;

    /// <summary>이 레벨 레시피에서 쓰는 재료 (화면에서 안 쓰는 칸은 숨김)</summary>
    public static (bool AbsEmber, bool Fossil, bool Dust, bool AnyT0) Uses(CraftGrade[] recipe)
    {
        var steps = recipe.SelectMany(g => new[] { g.T1, g.T0 }).Where(x => x != null).ToList();
        return (recipe.Length > 2 && recipe[2].T1 != null, steps.Any(x => x!.Fossil > 0), steps.Any(x => x!.Dust > 0), recipe.Any(g => g.T1 != null && g.T0 != null));
    }

    public sealed record GradeCost(bool T1Possible, bool T0Possible, double T1, double T0, double Subtotal);
    public sealed record Result(GradeCost[] Grades, double Craft, double Total);

    /// <summary>
    /// 1T 옵션당 = 1T 1회 ÷ 확률. 0T 옵션당 = 1T 옵션당 + 승급 1회 ÷ 확률 (승급 실패해도 1T 는 남음).
    /// 양손이면 2배. 소계 = 옵션당 × 개수 (보정 전). 제작비 = Σ소계 × 보정%. 총 = 제작비 + 베이스(보정·양손 없음).
    /// </summary>
    public static Result Compute(CraftGrade[] recipe, bool twoHand, double correctionPct,
        double[] emberPrices, double fossilPrice, int[] t1Counts, int[] t0Counts, double baseCost, double dustPrice = 0)
    {
        double hand = twoHand ? 2 : 1;
        var grades = new GradeCost[3];
        double sum = 0;
        for (int g = 0; g < 3; g++)
        {
            var r = g < recipe.Length ? recipe[g] : new CraftGrade();
            bool p1 = r.T1 != null, p0 = r.T1 != null && r.T0 != null;
            double c1 = p1 ? Expected(r.T1!, emberPrices[g], fossilPrice, dustPrice) * hand : 0;
            double c0 = p0 ? c1 + Expected(r.T0!, emberPrices[g], fossilPrice, dustPrice) * hand : 0;
            double sub = (p1 ? c1 * t1Counts[g] : 0) + (p0 ? c0 * t0Counts[g] : 0);
            grades[g] = new GradeCost(p1, p0, c1, c0, sub);
            sum += sub;
        }
        double craft = sum * Math.Max(0, correctionPct) / 100;
        return new Result(grades, craft, craft + baseCost);
    }
}

public static class SkillCalc
{
    public static readonly string[] Tiers = { "0티어", "1티어", "2티어" };
    /// <summary>재료 스킬 1개가 올려 주는 상승치 (0티어 +10, 1티어 +5, 2티어 +2)</summary>
    public static readonly int[] Gain = { 10, 5, 2 };
    public const int Need = 50;
    public const int InspirationCount = 500;

    /// <summary>Buy[i] = i 티어에서 싼 매물부터 몇 개, TierCost[i] = 그 합. Points 는 넘친 것까지 포함한 상승치.</summary>
    public sealed record Result(int[] Buy, double[] TierCost, int Points, double SkillCost, bool Enough, double Total);

    /// <summary>
    /// 티어별 매물 가격 목록(매물 하나 = 스킬 1개) 안에서 상승치 Need 이상을 가장 싸게 채우는 조합.
    /// 50 을 넘겨도 됨 (넘친 만큼은 버려짐). 티어 섞기 가능. 같은 값이면 덜 넘치는 쪽, 그다음 개수가 적은 쪽.
    /// 다 사도 모자라면 Enough=false 로 전부 산 경우를 돌려줌. listings[i] 가 null 이면 매물 없음.
    /// </summary>
    public static Result Compute(IReadOnlyList<double>?[] listings, double inspirationPrice, double baseCost)
    {
        var sorted = new double[3][];
        var prefix = new double[3][];
        for (int i = 0; i < 3; i++)
        {
            sorted[i] = (listings.Length > i && listings[i] != null ? listings[i]!.Where(p => p >= 0) : Enumerable.Empty<double>()).OrderBy(p => p).ToArray();
            prefix[i] = new double[sorted[i].Length + 1];
            for (int k = 0; k < sorted[i].Length; k++) prefix[i][k + 1] = prefix[i][k] + sorted[i][k];
        }
        // 한 티어를 혼자 Need 를 채울 개수보다 많이 사는 건 손해 (가격이 0 이상이라)
        int Cap(int i) => Math.Min(sorted[i].Length, (Need + Gain[i] - 1) / Gain[i]);
        int[]? best = null; double bestCost = double.MaxValue; int bestPts = 0;
        for (int a = 0; a <= Cap(0); a++)
            for (int b = 0; b <= Cap(1); b++)
                for (int c = 0; c <= Cap(2); c++)
                {
                    int pts = a * Gain[0] + b * Gain[1] + c * Gain[2];
                    if (pts < Need) continue;
                    double cost = prefix[0][a] + prefix[1][b] + prefix[2][c];
                    if (cost < bestCost - 1e-9 || (Math.Abs(cost - bestCost) < 1e-9 && (pts < bestPts || (pts == bestPts && a + b + c < best!.Sum()))))
                    { best = new[] { a, b, c }; bestCost = cost; bestPts = pts; }
                }
        double extra = Math.Max(0, inspirationPrice) * InspirationCount + baseCost;
        bool enough = best != null;
        var buy = best ?? new[] { sorted[0].Length, sorted[1].Length, sorted[2].Length };
        var tierCost = new double[3];
        int points = 0;
        for (int i = 0; i < 3; i++) { tierCost[i] = prefix[i][buy[i]]; points += buy[i] * Gain[i]; }
        double skill = tierCost.Sum();
        return new Result(buy, tierCost, points, skill, enough, skill + extra);
    }
}

/// <summary>부귀·숭고 스킬 번호 → 이름 (tlidb 에서 만든 표, exe 에 들어 있음. tools/build_skill_names.py)</summary>
public static class SkillNames
{
    public sealed record Entry(string Name, string Kind);
    static Dictionary<string, Entry>? table;

    public static IReadOnlyDictionary<string, Entry> Table => table ??= Load();

    static Dictionary<string, Entry> Load()
    {
        var d = new Dictionary<string, Entry>();
        try
        {
            using var s = typeof(SkillNames).Assembly.GetManifestResourceStream("skill_names.json");
            if (s == null) return d;
            using var doc = JsonDocument.Parse(s);
            foreach (var p in doc.RootElement.GetProperty("skills").EnumerateObject())
                d[p.Name] = new Entry(p.Value.GetProperty("name").GetString() ?? "", p.Value.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "");
        }
        catch { }
        return d;
    }

    /// <summary>표 → 사용자가 적은 이름 순서로 찾음. 없으면 null</summary>
    public static string? Find(string? id, IReadOnlyDictionary<string, string>? custom = null)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (custom != null && custom.TryGetValue(id, out var c) && c.Length > 0) return c;
        return Table.TryGetValue(id, out var e) ? e.Name : null;
    }
}

/// <summary>계산기 입력값 (설정\calc.json). 창을 닫았다 열어도, 업데이트해도 그대로.</summary>
public sealed class CalcState
{
    [JsonPropertyName("craft")] public CraftState Craft { get; set; } = new();
    [JsonPropertyName("skill")] public SkillState Skill { get; set; } = new();
    /// <summary>제작 레시피 (한손 1회). 게임 수치가 바뀌면 여기만 고치면 됨</summary>
    [JsonPropertyName("craft_recipes")] public Dictionary<int, CraftGrade[]> Recipes { get; set; } = CraftCalc.DefaultRecipes();

    public sealed class WinPos
    {
        [JsonPropertyName("x")] public int? X { get; set; }
        [JsonPropertyName("y")] public int? Y { get; set; }
        [JsonPropertyName("collapsed")] public bool Collapsed { get; set; }
    }

    public sealed class CraftState
    {
        [JsonPropertyName("level")] public int Level { get; set; } = 100;
        [JsonPropertyName("two_hand")] public bool TwoHand { get; set; }
        [JsonPropertyName("correction")] public double Correction { get; set; } = 100;
        /// <summary>진귀한 엠버, 희귀한 엠버 (절대자 엠버는 상점 시세표 값을 같이 씀)</summary>
        [JsonPropertyName("ember_prices")] public double?[] EmberPrices { get; set; } = { null, null, null };
        [JsonPropertyName("fossil_price")] public double? FossilPrice { get; set; }
        /// <summary>결정 가루 (상점 시세표의 '최초의 불꽃 가루' 를 같이 씀. 그 항목이 없을 때만 이 값)</summary>
        [JsonPropertyName("dust_price")] public double? DustPrice { get; set; }
        /// <summary>레벨별 값 (등급별 개수, 베이스 비용). 레벨을 바꾸면 그 레벨 값을 불러옴</summary>
        [JsonPropertyName("levels")] public Dictionary<int, LevelVals> Levels { get; set; } = new();
        public LevelVals For(int level)
        {
            if (!Levels.TryGetValue(level, out var v)) Levels[level] = v = new LevelVals();
            return v;
        }
        // v1.8.2 까지: 레벨 구분 없이 한 벌. 읽을 때 그때 고른 레벨 값으로 옮기고 다시는 쓰지 않음
        [JsonPropertyName("t1"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int[]? OldT1 { get; set; }
        [JsonPropertyName("t0"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int[]? OldT0 { get; set; }
        [JsonPropertyName("base"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? OldBase { get; set; }
        [JsonPropertyName("window")] public WinPos Window { get; set; } = new();
    }

    public sealed class LevelVals
    {
        /// <summary>등급별(초급·진급·절대자) 1T(82레벨은 2T) 개수</summary>
        [JsonPropertyName("t1")] public int[] T1 { get; set; } = new int[3];
        [JsonPropertyName("t0")] public int[] T0 { get; set; } = new int[3];
        [JsonPropertyName("base")] public double? Base { get; set; }
    }

    public sealed class SkillState
    {
        /// <summary>티어별(0·1·2티어) 마지막으로 기록한 매물 가격 목록 (null = 기록 없음)</summary>
        [JsonPropertyName("listings")] public List<double>?[] Listings { get; set; } = { null, null, null };
        [JsonPropertyName("recorded_at")] public DateTime?[] RecordedAt { get; set; } = { null, null, null };
        /// <summary>기록한 스킬 번호 (게임 로그의 itemGoldId)</summary>
        [JsonPropertyName("skill_id")] public string? SkillId { get; set; }
        /// <summary>이름표에 없는 스킬에 사용자가 붙인 이름</summary>
        [JsonPropertyName("names")] public Dictionary<string, string> Names { get; set; } = new();
        [JsonPropertyName("inspiration_price")] public double? Inspiration { get; set; }
        [JsonPropertyName("base")] public double? Base { get; set; }
        [JsonPropertyName("window")] public WinPos Window { get; set; } = new();
    }

    public static CalcState Load(string path)
    {
        CalcState s;
        try { s = File.Exists(path) ? JsonSerializer.Deserialize<CalcState>(File.ReadAllText(path), Config.Opts) ?? new() : new(); }
        catch { s = new(); }
        // 배열 길이가 모자라면 (손으로 고친 파일 등) 채움
        static T[] Fix<T>(T[]? a) { var r = new T[3]; if (a != null) Array.Copy(a, r, Math.Min(3, a.Length)); return r; }
        s.Craft.EmberPrices = Fix(s.Craft.EmberPrices);
        s.Craft.Levels ??= new();
        // 예전 파일: 레벨 구분 없던 개수·베이스 비용을 그때 고른 레벨로 옮김
        if (s.Craft.OldT1 != null || s.Craft.OldT0 != null || s.Craft.OldBase != null)
        {
            var v = s.Craft.For(s.Craft.Level);
            if (s.Craft.OldT1 != null) v.T1 = Fix(s.Craft.OldT1);
            if (s.Craft.OldT0 != null) v.T0 = Fix(s.Craft.OldT0);
            v.Base ??= s.Craft.OldBase;
            s.Craft.OldT1 = s.Craft.OldT0 = null; s.Craft.OldBase = null;
        }
        foreach (var v in s.Craft.Levels.Values) { v.T1 = Fix(v.T1); v.T0 = Fix(v.T0); }
        s.Skill.Listings = Fix(s.Skill.Listings); s.Skill.RecordedAt = Fix(s.Skill.RecordedAt);
        s.Skill.Names ??= new();
        if (s.Recipes == null || s.Recipes.Count == 0) s.Recipes = CraftCalc.DefaultRecipes();
        // 새 버전에서 추가된 레벨(예: 82)은 예전 파일에 없으니 기본 레시피로 채움 (있는 레벨은 손대지 않음)
        foreach (var (lv, rec) in CraftCalc.DefaultRecipes()) s.Recipes.TryAdd(lv, rec);
        if (!s.Recipes.ContainsKey(s.Craft.Level)) s.Craft.Level = s.Recipes.Keys.Max();
        return s;
    }

    public void Save(string path) => Config.WriteAtomic(path, JsonSerializer.Serialize(this, Config.Opts));
}

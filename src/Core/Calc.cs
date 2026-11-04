using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopHelper;

// =====================================================================
// 부가 기능 계산기 2개 (트레이에서 여는 창). 계산은 여기, 화면은 App/CalcWindow.cs
//   1) 장비 제작비: 등급별 1T/0T 옵션 개수 → 기대 제작비
//   2) 부귀·숭고 스킬 레벨업: 재료 스킬 매물로 상승치 50 을 가장 싸게 채우는 조합
// =====================================================================

/// <summary>제작 1회 재료 (한손 기준). 엠버 종류는 등급마다 정해져 있음.</summary>
public sealed class CraftStep
{
    [JsonPropertyName("crystal")] public double Crystal { get; set; }
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
    public static readonly int[] Levels = { 100, 86 };

    /// <summary>게임 수치 (한손 1회 기준, 사용자가 알려 준 값). 양손은 전부 2배.</summary>
    public static Dictionary<int, CraftGrade[]> DefaultRecipes() => new()
    {
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
    static double Expected(CraftStep s, double emberPrice, double fossilPrice) =>
        s.Chance <= 0 ? 0 : (s.Crystal + s.Ember * emberPrice + s.Fossil * fossilPrice) / s.Chance;

    public sealed record GradeCost(bool T1Possible, bool T0Possible, double T1, double T0, double Subtotal);
    public sealed record Result(GradeCost[] Grades, double Craft, double Total);

    /// <summary>
    /// 1T 옵션당 = 1T 1회 ÷ 확률. 0T 옵션당 = 1T 옵션당 + 승급 1회 ÷ 확률 (승급 실패해도 1T 는 남음).
    /// 양손이면 2배. 소계 = 옵션당 × 개수 (보정 전). 제작비 = Σ소계 × 보정%. 총 = 제작비 + 베이스(보정·양손 없음).
    /// </summary>
    public static Result Compute(CraftGrade[] recipe, bool twoHand, double correctionPct,
        double[] emberPrices, double fossilPrice, int[] t1Counts, int[] t0Counts, double baseCost)
    {
        double hand = twoHand ? 2 : 1;
        var grades = new GradeCost[3];
        double sum = 0;
        for (int g = 0; g < 3; g++)
        {
            var r = g < recipe.Length ? recipe[g] : new CraftGrade();
            bool p1 = r.T1 != null, p0 = r.T1 != null && r.T0 != null;
            double c1 = p1 ? Expected(r.T1!, emberPrices[g], fossilPrice) * hand : 0;
            double c0 = p0 ? c1 + Expected(r.T0!, emberPrices[g], fossilPrice) * hand : 0;
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
    public static readonly string[] Tiers = { "2티어", "1티어", "0티어" };
    /// <summary>재료 스킬 1개가 올려 주는 상승치</summary>
    public static readonly int[] Gain = { 2, 5, 10 };
    public const int Need = 50;
    public const int InspirationCount = 500;

    public sealed record Result(int[] Buy, int Points, double SkillCost, bool Enough, double Total);

    /// <summary>
    /// 매물(개당 가격, 개수) 안에서 상승치 Need 이상을 가장 싸게 채우는 조합 (티어 섞기 가능, 넘쳐도 됨).
    /// 가격을 안 넣은 티어는 매물이 없는 것으로 봄. 다 사도 모자라면 Enough=false 로 전부 산 경우를 돌려줌.
    /// </summary>
    public static Result Compute(double?[] prices, int[] counts, double inspirationPrice, double baseCost)
    {
        var avail = new int[3];
        for (int i = 0; i < 3; i++)
            avail[i] = prices[i] is double p && p >= 0 ? Math.Max(0, counts[i]) : 0;
        // 한 티어를 필요 이상 사는 건 손해라서, 티어마다 Need 를 혼자 채울 개수까지만 보면 됨
        int Cap(int i) => Math.Min(avail[i], (Need + Gain[i] - 1) / Gain[i]);
        int[]? best = null; double bestCost = double.MaxValue; int bestPts = 0;
        for (int a = 0; a <= Cap(0); a++)
            for (int b = 0; b <= Cap(1); b++)
                for (int c = 0; c <= Cap(2); c++)
                {
                    int pts = a * Gain[0] + b * Gain[1] + c * Gain[2];
                    if (pts < Need) continue;
                    double cost = a * (prices[0] ?? 0) + b * (prices[1] ?? 0) + c * (prices[2] ?? 0);
                    // 같은 값이면 상승치가 덜 넘치는 쪽, 그다음 개수가 적은 쪽
                    if (cost < bestCost - 1e-9 || (Math.Abs(cost - bestCost) < 1e-9 && (pts < bestPts || (pts == bestPts && a + b + c < best!.Sum()))))
                    { best = new[] { a, b, c }; bestCost = cost; bestPts = pts; }
                }
        double extra = Math.Max(0, inspirationPrice) * InspirationCount + baseCost;
        if (best != null) return new Result(best, bestPts, bestCost, true, bestCost + extra);
        // 모자람: 있는 걸 다 산 경우
        double all = 0; int allPts = 0;
        for (int i = 0; i < 3; i++) { all += avail[i] * (prices[i] ?? 0); allPts += avail[i] * Gain[i]; }
        return new Result(avail, allPts, all, false, all + extra);
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
        [JsonPropertyName("t1")] public int[] T1 { get; set; } = new int[3];
        [JsonPropertyName("t0")] public int[] T0 { get; set; } = new int[3];
        [JsonPropertyName("base")] public double? Base { get; set; }
        [JsonPropertyName("window")] public WinPos Window { get; set; } = new();
    }

    public sealed class SkillState
    {
        [JsonPropertyName("prices")] public double?[] Prices { get; set; } = { null, null, null };
        [JsonPropertyName("counts")] public int[] Counts { get; set; } = new int[3];
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
        s.Craft.EmberPrices = Fix(s.Craft.EmberPrices); s.Craft.T1 = Fix(s.Craft.T1); s.Craft.T0 = Fix(s.Craft.T0);
        s.Skill.Prices = Fix(s.Skill.Prices); s.Skill.Counts = Fix(s.Skill.Counts);
        if (s.Recipes == null || s.Recipes.Count == 0) s.Recipes = CraftCalc.DefaultRecipes();
        if (!s.Recipes.ContainsKey(s.Craft.Level)) s.Craft.Level = s.Recipes.Keys.Max();
        return s;
    }

    public void Save(string path) => Config.WriteAtomic(path, JsonSerializer.Serialize(this, Config.Opts));
}

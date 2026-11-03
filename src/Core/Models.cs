using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopHelper;

public readonly record struct Box(double X1, double Y1, double X2, double Y2)
{
    public double W => X2 - X1;
    public double H => Y2 - Y1;
    public double Cx => (X1 + X2) / 2;
    public Box Offset(double dx, double dy) => new(X1 + dx, Y1 + dy, X2 + dx, Y2 + dy);
    public Box Scale(double s) => new(X1 / s, Y1 / s, X2 / s, Y2 / s);
}

public sealed class OcrWord
{
    public string Text = "";
    public double X, Y, W, H;
    public OcrWord() { }
    public OcrWord(string t, double x, double y, double w, double h) { Text = t; X = x; Y = y; W = w; H = h; }
}

public sealed class Seg
{
    public string Text = "";
    public Box B;
    public double H;
}

public enum Verdict { Unknown, Good, Bad, Sold, Gamble }

public sealed class Card
{
    public string Raw = "";
    public string Name = "";
    public int Qty;
    public int Price;
    public bool Sold;      // 매진
    public Box B;          // 이름 + 가격 전체
    public Box PriceBox;   // 가격 줄
    public double H;
    public ItemDef? Item;
    public double? Value;
    public double? Diff;                 // 휘석 (도박이면 기대값 기준)
    public double? RangeLo, RangeHi;     // 도박: 최악/최고 이득 (휘석)
    public Verdict Status = Verdict.Unknown;

    public Card Clone() => (Card)MemberwiseClone();
}

public sealed class ItemDef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>1개당 결정 (도박 항목은 쓰지 않음)</summary>
    [JsonPropertyName("price")] public double? Price { get; set; }
    [JsonPropertyName("taxed")] public bool Taxed { get; set; } = true;
    /// <summary>normal / gamble (신비의 휘석처럼 휘석을 랜덤으로 주는 항목)</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "normal";
    [JsonPropertyName("gamble_avg")] public double? GambleAvg { get; set; }
    [JsonPropertyName("gamble_min")] public double? GambleMin { get; set; }
    [JsonPropertyName("gamble_max")] public double? GambleMax { get; set; }
    [JsonIgnore] public bool IsGamble => Kind.Equals("gamble", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsCrystal => Name.Replace(" ", "") == "최초의불꽃결정";
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = new();
}

public sealed class ScanRegion
{
    [JsonPropertyName("top")] public double Top { get; set; } = 0.0;
    [JsonPropertyName("bottom")] public double Bottom { get; set; } = 1.0;
}

public sealed class Config
{
    [JsonPropertyName("_log_설명")] public string LogNote { get; set; } = "auto = 스팀/실행 중인 게임에서 자동으로 찾기. 직접 지정하려면 경로를 넣으세요";
    [JsonPropertyName("log_path")] public string LogPath { get; set; } = "auto";
    [JsonPropertyName("_unit_설명")] public string UnitNote { get; set; } = "price 는 1개당 결정. crystal_rate = 결정 1개가 몇 휘석인지";
    [JsonPropertyName("price_unit")] public string PriceUnit { get; set; } = "crystal";
    [JsonPropertyName("crystal_rate")] public double CrystalRate { get; set; } = 50;
    [JsonPropertyName("_mode_설명")] public string ModeNote { get; set; } = "resell = 결정화 기준(팔아서 결정으로 받을 때: 수수료 떼고 올림한 실수령보다 싸야 이득, 기본), use = 직접 쓸 때(시세보다 싸면 이득)";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "resell";
    [JsonPropertyName("tax_rate")] public double TaxRate { get; set; } = 0.125;
    [JsonPropertyName("refresh_cost")] public double RefreshCost { get; set; } = 50;
    [JsonPropertyName("_scan_설명")] public string ScanNote { get; set; } = "처음 칸 위치를 찾을 때 읽는 세로 범위(0~1)와 확대 배율";
    [JsonPropertyName("scan_region")] public ScanRegion ScanRegion { get; set; } = new();
    [JsonPropertyName("ocr_scale")] public double OcrScale { get; set; } = 2.0;
    [JsonPropertyName("slot_ocr_scale")] public double SlotOcrScale { get; set; } = 2.5;
    [JsonPropertyName("check_updates")] public bool CheckUpdates { get; set; } = true;
    [JsonPropertyName("update_repo")] public string UpdateRepo { get; set; } = "Jaewon234/TLI-ShopHelper";
    [JsonPropertyName("window_class")] public string WindowClass { get; set; } = "UnrealWindow";
    [JsonPropertyName("window_title_contains")] public string WindowTitleContains { get; set; } = "";
    [JsonPropertyName("_items_설명")] public string ItemsNote { get; set; } = "price = 1개당 결정. 모르면 null. kind=gamble 은 gamble_avg(평균 획득 휘석)로 계산";
    [JsonPropertyName("items")] public List<ItemDef> Items { get; set; } = DefaultItems();

    [JsonIgnore] public bool Resell => Mode.Equals("resell", StringComparison.OrdinalIgnoreCase);

    public static List<ItemDef> DefaultItems() => new()
    {
        new() { Name = "최초의 불꽃 결정", Price = 1, Taxed = false },
        new() { Name = "최초의 불꽃 가루", Price = null, Aliases = new() { "결정가루" } },
        new() { Name = "이계의 메아리", Price = 0.11 },
        new() { Name = "승화의 쐐기(매직)", Price = 0.006 },
        new() { Name = "승화의 쐐기(레어)", Price = 0.07 },
        new() { Name = "승화의 쐐기(레전드)", Price = 5.5 },
        new() { Name = "에너지 코어", Price = 0.04 },
        new() { Name = "절대자 엠버", Price = 20.5 },
        Gamble("소", 50, 350),
        Gamble("중", null, null),
        Gamble("대", 100, 900),
    };

    /// <summary>신비의 휘석(소/중/대): 휘석을 랜덤으로 주는 도박. 평균은 따로 안 주면 (최소+최대)/2.</summary>
    public static ItemDef Gamble(string size, double? min, double? max) => new()
    {
        Name = $"신비의 휘석({size})", Kind = "gamble", Taxed = false, GambleMin = min, GambleMax = max,
    };

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Config Load(string path)
    {
        if (!File.Exists(path))
        {
            var c = new Config();
            c.Save(path);
            return c;
        }
        string json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<Config>(json, Opts) ?? new Config();
        // price_unit 이 없으면 예전 파일(1개당 휘석). 기본값이 채워지므로 직접 확인
        using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            if (!doc.RootElement.TryGetProperty("price_unit", out _)) cfg.PriceUnit = "";
        if (cfg.Migrate()) { try { cfg.Save(path); } catch { } }
        return cfg;
    }

    /// <summary>예전 설정(1개당 휘석)을 1개당 결정으로 바꿈. 바꿨으면 true.</summary>
    public bool Migrate()
    {
        bool changed = false;
        if (PriceUnit != "crystal")
        {
            var crystal = Items.FirstOrDefault(i => i.IsCrystal);
            double rate = crystal?.Price is double cp && cp > 1 ? cp : 50;
            CrystalRate = rate;
            foreach (var it in Items)
                if (it.Price is double p) it.Price = Math.Round(p / rate, 6);
            if (crystal != null) { crystal.Price = 1; crystal.Taxed = false; }
            PriceUnit = "crystal";
            changed = true;
        }
        // 신비의 휘석(소/중/대)는 도박 항목
        foreach (var d in DefaultItems().Where(i => i.IsGamble))
        {
            var g = Items.FirstOrDefault(i => i.Name.Replace(" ", "") == d.Name.Replace(" ", ""));
            if (g == null) { Items.Add(d); changed = true; }
            else
            {
                if (!g.IsGamble) { g.Kind = d.Kind; g.Taxed = false; g.Price = null; changed = true; }
                // 새 버전에서 범위를 알게 된 도박은 비어 있을 때만 채움 (직접 넣은 값은 그대로)
                if (g.GambleMin == null && d.GambleMin != null) { g.GambleMin = d.GambleMin; changed = true; }
                if (g.GambleMax == null && d.GambleMax != null) { g.GambleMax = d.GambleMax; changed = true; }
            }
        }
        if (!Items.Any(i => i.IsCrystal)) { Items.Insert(0, new ItemDef { Name = "최초의 불꽃 결정", Price = 1, Taxed = false }); changed = true; }
        // 새 버전에서 기본 항목이 늘어나면 없는 것만 추가 (입력해 둔 시세는 그대로)
        foreach (var d in DefaultItems())
            if (!Items.Any(i => i.Name.Replace(" ", "") == d.Name.Replace(" ", ""))) { Items.Add(d); changed = true; }
        return changed;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));

    // ---------------- 시세 / 설정 분리 저장 ----------------
    // prices.json  : 시세만 (항목, 결정 환율). 업데이트해도 절대 덮어쓰지 않음.
    // settings.json: 그 밖의 설정 (기준, 업데이트 확인 …).

    static readonly string[] PriceKeys = { "_unit_설명", "price_unit", "crystal_rate", "_items_설명", "items" };

    sealed class PricesFile
    {
        [JsonPropertyName("_설명")] public string Note { get; set; } = "시세 (1개당 결정). 업데이트해도 바뀌지 않습니다. crystal_rate = 결정 1개가 몇 휘석인지";
        [JsonPropertyName("price_unit")] public string PriceUnit { get; set; } = "crystal";
        [JsonPropertyName("crystal_rate")] public double CrystalRate { get; set; } = 50;
        [JsonPropertyName("items")] public List<ItemDef> Items { get; set; } = new();
    }

    public void SaveSplit(string settingsPath, string pricesPath)
    {
        SavePrices(pricesPath);
        SaveSettings(settingsPath);
    }

    public void SavePrices(string pricesPath) =>
        WriteAtomic(pricesPath, JsonSerializer.Serialize(new PricesFile { PriceUnit = PriceUnit, CrystalRate = CrystalRate, Items = Items }, Opts));

    public void SaveSettings(string settingsPath)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(this, Opts))!.AsObject();
        foreach (var k in PriceKeys) node.Remove(k);
        WriteAtomic(settingsPath, node.ToJsonString(Opts));
    }

    /// <summary>중간에 꺼져도 파일이 깨지지 않게: 임시 파일에 쓰고 바꿔치기.</summary>
    static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// settings.json + prices.json 을 읽음. 둘 다 없고 예전 config.json 이 있으면 그걸 나눠서 옮김.
    /// </summary>
    public static Config LoadSplit(string settingsPath, string pricesPath, params string[] legacyPaths)
    {
        if (!File.Exists(settingsPath) && !File.Exists(pricesPath))
        {
            var legacy = legacyPaths.FirstOrDefault(File.Exists);
            var c = legacy != null ? Load(legacy) : new Config();
            c.SaveSplit(settingsPath, pricesPath);
            if (legacy != null) try { File.Move(legacy, legacy + ".bak", overwrite: true); } catch { }
            return c;
        }
        var docOpts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var cfg = File.Exists(settingsPath)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(settingsPath), Opts) ?? new Config()
            : new Config();
        bool changed = false;
        if (File.Exists(pricesPath))
        {
            string json = File.ReadAllText(pricesPath);
            var pf = JsonSerializer.Deserialize<PricesFile>(json, Opts) ?? new PricesFile();
            using (var doc = JsonDocument.Parse(json, docOpts))
                if (!doc.RootElement.TryGetProperty("price_unit", out _)) pf.PriceUnit = "";
            cfg.PriceUnit = pf.PriceUnit; cfg.CrystalRate = pf.CrystalRate; cfg.Items = pf.Items;
        }
        else changed = true;
        changed |= cfg.Migrate();
        if (changed) cfg.SavePrices(pricesPath);
        if (!File.Exists(settingsPath)) cfg.SaveSettings(settingsPath);
        return cfg;
    }
}

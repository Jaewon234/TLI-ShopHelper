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

public enum Verdict { Unknown, Good, Bad }

public sealed class Card
{
    public string Raw = "";
    public string Name = "";
    public int Qty;
    public int Price;
    public Box B;          // 이름 + 가격 전체
    public Box PriceBox;   // 가격 줄
    public double H;
    public ItemDef? Item;
    public double? Value;
    public double? Diff;
    public Verdict Status = Verdict.Unknown;

    public Card Clone() => (Card)MemberwiseClone();
}

public sealed class ItemDef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("price")] public double? Price { get; set; }
    [JsonPropertyName("taxed")] public bool Taxed { get; set; } = true;
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
    [JsonPropertyName("_mode_설명")] public string ModeNote { get; set; } = "use = 직접 쓸 때(시세보다 싸면 이득), resell = 되팔 때(세금 떼고 올림한 실수령보다 싸야 이득)";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "use";
    [JsonPropertyName("tax_rate")] public double TaxRate { get; set; } = 0.125;
    [JsonPropertyName("refresh_cost")] public double RefreshCost { get; set; } = 50;
    [JsonPropertyName("_scan_설명")] public string ScanNote { get; set; } = "처음 칸 위치를 찾을 때 읽는 세로 범위(0~1)와 확대 배율";
    [JsonPropertyName("scan_region")] public ScanRegion ScanRegion { get; set; } = new();
    [JsonPropertyName("ocr_scale")] public double OcrScale { get; set; } = 2.0;
    [JsonPropertyName("slot_ocr_scale")] public double SlotOcrScale { get; set; } = 2.5;
    [JsonPropertyName("window_class")] public string WindowClass { get; set; } = "UnrealWindow";
    [JsonPropertyName("window_title_contains")] public string WindowTitleContains { get; set; } = "";
    [JsonPropertyName("_items_설명")] public string ItemsNote { get; set; } = "price = 1개당 휘석 시세. 모르면 null. taxed=false 는 팔 때 세금이 없는 항목";
    [JsonPropertyName("items")] public List<ItemDef> Items { get; set; } = DefaultItems();

    [JsonIgnore] public bool Resell => Mode.Equals("resell", StringComparison.OrdinalIgnoreCase);

    public static List<ItemDef> DefaultItems() => new()
    {
        new() { Name = "최초의 불꽃 결정", Price = 50, Taxed = false },
        new() { Name = "에너지 코어", Price = 2 },
        new() { Name = "이계의 메아리", Price = 5.5 },
        new() { Name = "승화의 쐐기(매직)", Price = 0.3 },
        new() { Name = "승화의 쐐기(레어)", Price = 3.5 },
        new() { Name = "승화의 쐐기(레전드)", Price = 275 },
        new() { Name = "절대자 엠버", Price = 1025 },
        new() { Name = "최초의 불꽃 가루", Price = null },
        new() { Name = "신비의 휘석(소)", Price = null },
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
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Opts) ?? new Config();
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
}

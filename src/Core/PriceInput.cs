using System.Globalization;
using System.Text.RegularExpressions;

namespace ShopHelper;

/// <summary>
/// 시세 입력 해석. 결과는 1개당 휘석.
///   5.5      → 5.5
///   40/20    → 2       (20개에 40휘석)
///   20.5결   → 1025    (결정 단위, 결정 시세를 곱함)
///   0.8결/20 → 2       (20개에 0.8결정)
///   (빈칸)   → null    (시세 없음)
/// </summary>
public static class PriceInput
{
    static readonly Regex Re = new(@"^([0-9]*\.?[0-9]+)\s*(결정?)?\s*(?:/\s*([0-9]*\.?[0-9]+)\s*(개)?)?$", RegexOptions.Compiled);

    public static bool TryParse(string? text, double crystalPrice, out double? value)
    {
        value = null;
        var t = (text ?? "").Trim().Replace(",", "").Replace("휘석", "");
        if (t.Length == 0) return true;
        var m = Re.Match(t);
        if (!m.Success) return false;
        double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (m.Groups[2].Success) v *= crystalPrice;
        if (m.Groups[3].Success)
        {
            double n = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (n <= 0) return false;
            v /= n;
        }
        value = Math.Round(v, 6);
        return true;
    }

    public static double CrystalPrice(Config cfg) =>
        cfg.Items.FirstOrDefault(i => i.Name.Replace(" ", "") == "최초의불꽃결정")?.Price ?? 50;
}

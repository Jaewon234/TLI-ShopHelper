using System.Globalization;
using System.Text.RegularExpressions;

namespace ShopHelper;

/// <summary>
/// 시세 입력 해석 (입력칸은 숫자와 소수점만 받음). 결과는 1개당 결정.
///   0.11              → 0.11
///   0.000, 0., ., 00  → 0
///   .5                → 0.5
///   (빈칸)            → null (시세 없음)
/// </summary>
public static class PriceInput
{
    static readonly Regex NumOnly = new(@"^[0-9]*\.?[0-9]*$", RegexOptions.Compiled);

    public static bool TryParse(string? text, out double? value)
    {
        value = null;
        var t = (text ?? "").Trim().Replace(",", "");
        if (t.Length == 0) return true;
        if (!NumOnly.IsMatch(t)) return false;
        if (!t.Any(char.IsAsciiDigit)) { value = 0; return true; }   // "." 만
        value = Math.Round(double.Parse(t.EndsWith('.') ? t + "0" : t.StartsWith('.') ? "0" + t : t, CultureInfo.InvariantCulture), 8);
        return true;
    }

    /// <summary>결정 1개가 몇 휘석인지.</summary>
    public static double CrystalPrice(Config cfg) => cfg.CrystalRate > 0 ? cfg.CrystalRate : 50;
}

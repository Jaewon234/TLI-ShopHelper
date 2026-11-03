using System.Globalization;
using System.Text.RegularExpressions;

namespace ShopHelper;

/// <summary>
/// 시세 입력 해석. 결과는 1개당 결정. (입력칸은 숫자와 소수점만 받음 · 아래 나머지 형식은 예전 파일 호환용)
///   0.11     → 0.11
///   0.000, 0., . → 0
///   1/20     → 0.05    (20개에 1결정)
///   5.5휘    → 0.11    (휘석으로 입력, 결정 1개 = rate 휘석)
///   40휘/20  → 0.04    (20개에 40휘석)
///   (빈칸)   → null    (시세 없음)
/// </summary>
public static class PriceInput
{
    static readonly Regex NumOnly = new(@"^[0-9]*\.?[0-9]*$", RegexOptions.Compiled);
    static readonly Regex Re = new(@"^([0-9]*\.?[0-9]+)\s*(결정?|휘석?)?\s*(?:/\s*([0-9]*\.?[0-9]+)\s*(개)?)?$", RegexOptions.Compiled);

    public static bool TryParse(string? text, double rate, out double? value)
    {
        value = null;
        var t = (text ?? "").Trim().Replace(",", "");
        if (t.Length == 0) return true;
        // 숫자와 소수점만 (입력칸에서 이것만 받음): "0.000", "0.", ".", "00" → 0, ".5" → 0.5
        if (NumOnly.IsMatch(t))
        {
            value = t.Any(char.IsAsciiDigit)
                ? Math.Round(double.Parse(t.EndsWith('.') ? t + "0" : t.StartsWith('.') ? "0" + t : t, CultureInfo.InvariantCulture), 8)
                : 0;
            return true;
        }
        var m = Re.Match(t);
        if (!m.Success) return false;
        double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (m.Groups[2].Success && m.Groups[2].Value.StartsWith("휘"))
        {
            if (rate <= 0) return false;
            v /= rate;
        }
        if (m.Groups[3].Success)
        {
            double n = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (n <= 0) return false;
            v /= n;
        }
        value = Math.Round(v, 8);
        return true;
    }

    /// <summary>결정 1개가 몇 휘석인지.</summary>
    public static double CrystalPrice(Config cfg) => cfg.CrystalRate > 0 ? cfg.CrystalRate : 50;
}

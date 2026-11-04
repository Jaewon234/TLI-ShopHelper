using System.Text.RegularExpressions;

namespace ShopHelper;

public enum LogEv { None, ShopOpen, ShopClose, ShopHidden, ViewOpen, ViewClose }

/// <summary>
/// UE_game.log 한 줄 해석. 게임 패치로 문구가 바뀌어 두 형식을 다 봄.
///  새 형식: "TipMsgShowMgr@DispatchPageRunChange PageName = GroceryStoreV3 , PageRunState = Run / Hide / Destory"
///  옛 형식: "Page@ OpenView GroceryStoreV3", "Page@ CloseView 123_GroceryStoreV3Ctrl"
/// </summary>
public static class LogLine
{
    public const string Shop = "GroceryStoreV3";

    static readonly Regex RunChange = new(@"DispatchPageRunChange PageName = (\S+?)\s*,\s*PageRunState = (\w+)", RegexOptions.Compiled);
    static readonly Regex OldOpen = new(@"\bPage@ OpenView (\S+)\s*$", RegexOptions.Compiled);
    static readonly Regex OldClose = new(@"\bPage@ CloseView \d+_(\S+?)Ctrl\s*$", RegexOptions.Compiled);

    public static (LogEv Ev, string Name) Parse(string line)
    {
        if (line.IndexOf("Page", StringComparison.Ordinal) < 0) return (LogEv.None, "");
        var m = RunChange.Match(line);
        if (m.Success)
        {
            string name = m.Groups[1].Value, st = m.Groups[2].Value;
            bool run = st.Equals("Run", StringComparison.OrdinalIgnoreCase);
            bool gone = st.StartsWith("Destor", StringComparison.OrdinalIgnoreCase) || st.StartsWith("Close", StringComparison.OrdinalIgnoreCase);
            bool hide = st.Equals("Hide", StringComparison.OrdinalIgnoreCase);
            if (name == Shop)
                return run ? (LogEv.ShopOpen, name) : gone ? (LogEv.ShopClose, name) : hide ? (LogEv.ShopHidden, name) : (LogEv.None, "");
            return run ? (LogEv.ViewOpen, name) : (gone || hide) ? (LogEv.ViewClose, name) : (LogEv.None, "");
        }
        var o = OldOpen.Match(line);
        if (o.Success) return o.Groups[1].Value == Shop ? (LogEv.ShopOpen, Shop) : (LogEv.ViewOpen, o.Groups[1].Value);
        var c = OldClose.Match(line);
        if (c.Success) return c.Groups[1].Value == Shop ? (LogEv.ShopClose, Shop) : (LogEv.ViewClose, c.Groups[1].Value);
        return (LogEv.None, "");
    }
}

/// <summary>거래소 가격 검색 결과 한 번: 아이템 번호와 1개당 가격 목록 (싼 순서, 최대 100개)</summary>
public sealed record XchgResult(int SynId, string? ItemId, List<double> Prices, string? Currency);

/// <summary>
/// UE_game.log 의 거래소 가격 검색 (XchgSearchPrice) 블록을 한 줄씩 받아 해석.
///   ----Socket SendMessage STT----XchgSearchPrice----SynId = 1870   (+filters+1+key [4] / +refer [8129] …)
///   ----Socket RecvMessage STT----XchgSearchPrice----SynId = 1870
///   +itemGoldId [8129]
///   +prices+1+unitPrices+1 [400.0]
///   |      | |          +2 [400.0] …
///   |      | +currency [100300]
///   ----Socket RecvMessage End----
/// 결과 블록이 끝나면 XchgResult 를 돌려줌. 결과에 아이템 번호가 없으면 같은 SynId 요청의 첫 refer 를 씀.
/// </summary>
public sealed class XchgParser
{
    static readonly System.Text.RegularExpressions.Regex Start =
        new(@"----Socket (Send|Recv)Message STT----XchgSearchPrice----SynId = (\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex Index =
        new(@"^[\s|]*\+(\d+) \[([0-9.eE+-]+)\]\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    enum Mode { None, Send, Recv }
    Mode mode;
    int synId;
    string? itemId, currency;
    List<double> prices = new();
    bool inUnitPrices;
    readonly Dictionary<int, string> sentRefer = new();

    public XchgResult? Feed(string line)
    {
        var st = Start.Match(line);
        if (st.Success)
        {
            mode = st.Groups[1].Value == "Send" ? Mode.Send : Mode.Recv;
            synId = int.Parse(st.Groups[2].Value);
            itemId = currency = null; prices = new(); inUnitPrices = false;
            return null;
        }
        if (mode == Mode.None) return null;
        if (line.Contains("Message End----"))
        {
            var m = mode; mode = Mode.None;
            if (m != Mode.Recv) return null;
            var id = itemId ?? (sentRefer.TryGetValue(synId, out var r) ? r : null);
            sentRefer.Remove(synId);
            return new XchgResult(synId, id, prices, currency);
        }
        // 줄 앞의 "[시각]TLNetGame: Display: [Game] " 는 블록 첫 줄에만 붙음
        int g = line.IndexOf("[Game]", StringComparison.Ordinal);
        var body = g >= 0 ? line[(g + 6)..] : line;
        if (mode == Mode.Send)
        {
            var mr = System.Text.RegularExpressions.Regex.Match(body, @"\+refer \[([^\]]+)\]");
            if (mr.Success && !sentRefer.ContainsKey(synId)) sentRefer[synId] = mr.Groups[1].Value;
            return null;
        }
        // 결과
        var ix = Index.Match(body);
        if (ix.Success && inUnitPrices)
        {
            if (double.TryParse(ix.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) prices.Add(v);
            return null;
        }
        if (body.Contains("+itemGoldId", StringComparison.Ordinal))
        {
            var mi = System.Text.RegularExpressions.Regex.Match(body, @"\+itemGoldId \[([^\]]+)\]");
            if (mi.Success) itemId = mi.Groups[1].Value;
            return null;
        }
        var mu = System.Text.RegularExpressions.Regex.Match(body, @"\+unitPrices\+1 \[([0-9.eE+-]+)\]");
        if (mu.Success)
        {
            // 가격 묶음이 여러 개면 첫 묶음만 씀
            if (prices.Count == 0 && double.TryParse(mu.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v0))
            { prices.Add(v0); inUnitPrices = true; }
            else inUnitPrices = false;
            return null;
        }
        var mc = System.Text.RegularExpressions.Regex.Match(body, @"\+currency \[([^\]]+)\]");
        if (mc.Success) { currency ??= mc.Groups[1].Value; inUnitPrices = false; return null; }
        if (body.Contains("+errCode", StringComparison.Ordinal)) inUnitPrices = false;
        return null;
    }
}

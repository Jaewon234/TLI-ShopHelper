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

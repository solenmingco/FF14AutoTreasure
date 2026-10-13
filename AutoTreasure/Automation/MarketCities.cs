using System.Collections.Generic;
using System.Linq;

namespace AutoTreasure.Automation;

/// <summary>
/// The cities a player can go to for a market board.
///
/// <see cref="MarketCity.TeleportArgument"/> is passed to Lifestream's
/// <c>tp</c> command, which accepts an English city name. We keep the list short and
/// to cities that actually have a market board, and the runtime object scan is the
/// real source of truth for the destination position.
/// </summary>
public static class MarketCities
{
    public static readonly IReadOnlyList<MarketCity> All = new List<MarketCity>
    {
        new() { DisplayName = "利姆萨·罗敏萨（海都）", TeleportArgument = "Limsa Lominsa" },
        new() { DisplayName = "格里达尼亚（森都）", TeleportArgument = "Gridania" },
        new() { DisplayName = "乌尔达哈（沙都）", TeleportArgument = "Ul'dah" },
        new() { DisplayName = "伊修加德", TeleportArgument = "Ishgard" },
        new() { DisplayName = "黄金港", TeleportArgument = "Kugane" },
        new() { DisplayName = "旧萨雷安", TeleportArgument = "Old Sharlayan" },
        new() { DisplayName = "图莱尤拉", TeleportArgument = "Tuliyollal" },
        new() { DisplayName = "拉札罕", TeleportArgument = "Radz-at-Han" },
        new() { DisplayName = "九号解决方案", TeleportArgument = "Solution Nine" },
    };

    public static MarketCity? ByName(string? name)
        => name is null ? null : All.FirstOrDefault(c => c.DisplayName == name);
}

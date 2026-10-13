using System.Collections.Generic;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;

namespace AutoTreasure.Automation;

public enum DiscardDecision
{
    Keep,
    Discard,
}

/// <summary>
/// Stage 6: decides which items count as junk. Decision-only — it never touches
/// the game. The actual discard call lives in <see cref="DiscardAction"/>.
/// </summary>
public sealed class DiscardPolicy
{
    private readonly Configuration configuration;

    public DiscardPolicy(Configuration configuration)
    {
        this.configuration = configuration;
    }

    /// <summary>Explain why an item was kept or discarded (for the activity log).</summary>
    public readonly record struct Verdict(DiscardDecision Decision, string Reason);

    public Verdict Evaluate(uint baseItemId)
    {
        var config = this.configuration;

        // Whitelist always wins, including over the blacklist.
        if (config.DiscardWhitelist.Contains(baseItemId))
        {
            return new Verdict(DiscardDecision.Keep, "命中白名单");
        }

        if (config.DiscardBlacklist.Contains(baseItemId))
        {
            return new Verdict(DiscardDecision.Discard, "命中黑名单");
        }

        // Price filter: only discard when we have a usable price and it is low enough.
        if (config.VendorPriceThreshold > 0)
        {
            var price = InventoryScanner.GetVendorPrice(baseItemId);
            if (price > 0 && price <= config.VendorPriceThreshold)
            {
                return new Verdict(DiscardDecision.Discard, $"售价 {price} 低于阈值");
            }
        }

        var rarity = InventoryScanner.GetRarity(baseItemId);

        // Green gear (rarity 2) is opt-in only.
        if (rarity == 2)
        {
            return config.DiscardGreenGear
                ? new Verdict(DiscardDecision.Discard, "绿色品质装备")
                : new Verdict(DiscardDecision.Keep, "绿色品质但未启用丢弃");
        }

        // White gear (rarity 1) is the classic junk case.
        if (rarity == 1 && config.DiscardWhiteGear)
        {
            if (config.ProtectMarketable && InventoryScanner.IsMarketable(baseItemId))
            {
                return new Verdict(DiscardDecision.Keep, "白色品质但可上架交易");
            }

            return new Verdict(DiscardDecision.Discard, "白色品质装备");
        }

        return new Verdict(DiscardDecision.Keep, "未命中任何规则");
    }

    /// <summary>
    /// Build the work list for one cleanup pass over the player's bags.
    /// Returns only the items that passed the policy; the caller performs the discard.
    /// </summary>
    public List<GameInventoryItem> BuildWorkList()
    {
        var work = new List<GameInventoryItem>();
        var mapIds = this.configuration.MapItemIds;

        foreach (var item in ScanPlayerBags())
        {
            // Maps are never junk, regardless of any other rule.
            if (mapIds.Contains(item.BaseItemId))
            {
                continue;
            }

            if (this.Evaluate(item.BaseItemId).Decision == DiscardDecision.Discard)
            {
                work.Add(item);
            }
        }

        return work;
    }

    /// <summary>
    /// Materialised snapshot of the player's four bags. A plain iterator cannot be
    /// used here because <c>GetInventoryItems</c> returns a <c>ReadOnlySpan</c>, which
    /// may not live across a <c>yield</c> boundary.
    /// </summary>
    private static List<GameInventoryItem> ScanPlayerBags()
    {
        var result = new List<GameInventoryItem>();
        foreach (var container in InventoryScanner.PlayerBags)
        {
            foreach (var item in Plugin.GameInventory.GetInventoryItems(container))
            {
                if (!item.IsEmpty)
                {
                    result.Add(item);
                }
            }
        }

        return result;
    }
}

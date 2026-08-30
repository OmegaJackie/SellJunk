using System;
using System.Collections.Generic;

namespace SellJunk.Data;

/// <summary>
/// The optimizer categories a stack can fall into. Bitflags because one stack routinely lands in
/// several - a cheap vendor material is usually also easily gathered and a low-level craft input.
/// </summary>
/// <remarks>
/// Public because it is a key in the serialized configuration; Newtonsoft writes enum keys by
/// name, so reordering the members is safe but renaming them is not.
/// </remarks>
[Flags]
public enum JunkCategory
{
    None = 0,

    // --- Sell-able: any enabled one of these stages the stack for review ---
    GilBuyable = 1 << 0,
    EasilyGathered = 1 << 1,
    SmallStack = 1 << 2,
    SingleRecipe = 1 << 3,
    NoRecipe = 1 << 4,
    SubMaxCraft = 1 << 5,

    // --- Advisory: counted and listed, never sold ---
    Duplicated = 1 << 6,
    FullSpiritbond = 1 << 7,
    LowerableHq = 1 << 8,
}

internal enum CategoryParam
{
    None,
    MaxVendorPrice,
    NodeLevel,
    StackThreshold,
    CraftLevel,
}

internal sealed record CategoryInfo(
    JunkCategory Category,
    string Name,
    string Tooltip,
    bool Sellable,
    CategoryParam Param,
    string ParamLabel,
    bool DefaultEnabled)
{
    /// <summary>Why an advisory category cannot be acted on here.</summary>
    public string? AdvisoryNote { get; init; }
}

internal static class JunkCategories
{
    /// <summary>Everything a sale can be built from.</summary>
    public const JunkCategory SellMask =
        JunkCategory.GilBuyable | JunkCategory.EasilyGathered | JunkCategory.SmallStack |
        JunkCategory.SingleRecipe | JunkCategory.NoRecipe | JunkCategory.SubMaxCraft;

    /// <summary>Displayed in the order below, matching how an optimizer panel reads top to bottom.</summary>
    public static readonly IReadOnlyList<CategoryInfo> All =
    [
        new(JunkCategory.GilBuyable,
            "Items that can be bought with gil",
            "Stocked by an NPC shop for gil, under the price you set. Quest- and achievement-gated\n" +
            "listings do not count, because you cannot necessarily buy those back.",
            Sellable: true, CategoryParam.MaxVendorPrice, "Maximum price", DefaultEnabled: true),

        // Off by default. Under OR these are far wider than they were as the second half of an
        // AND rule: on current data 580 and 2,620 distinct items respectively, against 897 for
        // the vendor category. Enabling them is a deliberate choice, and the row shows its own
        // count so the size of that choice is visible before you make it.
        new(JunkCategory.EasilyGathered,
            "Items that can be gathered easily",
            "Drops from a mining or botany node at or below the level you set. Unspoiled, ephemeral\n" +
            "and legendary node materials never qualify - you cannot re-gather those on demand.\n" +
            "On its own this does NOT require a vendor to stock the item.",
            Sellable: true, CategoryParam.NodeLevel, "Max node level", DefaultEnabled: false),

        new(JunkCategory.SubMaxCraft,
            "Items only used in below-max-level crafts",
            "Follows the craft chain, so an intermediate whose output feeds a max-level recipe does\n" +
            "not count as junk. This is the widest of the crafting rules - most materials in the\n" +
            "game top out below the cap - so check the count before enabling it.",
            Sellable: true, CategoryParam.CraftLevel, "Max craft level", DefaultEnabled: false),

        new(JunkCategory.SmallStack,
            "Items you have in very small stacks",
            "Stackable items you hold fewer of than the threshold - the ones eating a bag slot for\n" +
            "almost nothing. Non-stackable items are never listed here.",
            Sellable: true, CategoryParam.StackThreshold, "Threshold value", DefaultEnabled: false),

        new(JunkCategory.SingleRecipe,
            "Items that are only used for a single recipe",
            "Used as an ingredient by exactly one recipe in the game.",
            Sellable: true, CategoryParam.None, "", DefaultEnabled: false),

        new(JunkCategory.NoRecipe,
            "Items that are not used in any recipe",
            "Not an ingredient anywhere. This is the widest rule in the list - most of the item sheet\n" +
            "is used in no recipe at all - so it is off by default.",
            Sellable: true, CategoryParam.None, "", DefaultEnabled: false),

        new(JunkCategory.Duplicated,
            "Items duplicated across multiple containers",
            "The same item sitting in more than one place - bags, armoury chest, or the retainer you\n" +
            "have open. Worth consolidating so it occupies one slot instead of several.",
            Sellable: false, CategoryParam.None, "", DefaultEnabled: true)
        {
            AdvisoryNote = "Consolidate these by hand - SellJunk does not move items between your own containers.",
        },

        new(JunkCategory.FullSpiritbond,
            "Gear you can extract materia from (100% spiritbond)",
            "Fully spiritbound gear. Extract the materia before doing anything else with it.",
            Sellable: false, CategoryParam.None, "", DefaultEnabled: true)
        {
            AdvisoryNote = "Extract materia yourself first - this gear is never sold.",
        },

        new(JunkCategory.LowerableHq,
            "HQ stacks of items that can have their quality lowered",
            "HQ crafting materials. If you are not saving them for a specific craft, lowering quality\n" +
            "lets them stack with your NQ ones.",
            Sellable: false, CategoryParam.None, "", DefaultEnabled: true)
        {
            AdvisoryNote = "Lower quality from the item's right-click menu - HQ stacks are never sold.",
        },
    ];

    public static CategoryInfo Info(JunkCategory category)
    {
        foreach (var info in All)
        {
            if (info.Category == category)
                return info;
        }

        throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown category.");
    }
}

using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using SellJunk.Data;

namespace SellJunk.Game;

/// <summary>One stack in a specific container slot, with every category it falls into.</summary>
internal readonly record struct JunkStack(
    InventoryType Container,
    short Slot,
    uint ItemId,
    int Quantity,
    bool IsHq,
    string Name,
    uint UnitSellPrice,
    uint UnitBuyPrice,
    JunkCategory Categories,
    string Detail)
{
    public long StackValue => (long)UnitSellPrice * Quantity;

    /// <summary>What it would cost to buy this stack back. 0 when no vendor stocks it.</summary>
    public long StackRebuyCost => (long)UnitBuyPrice * Quantity;

    /// <summary>True when at least one sell-able category caught this stack.</summary>
    public bool IsSellable => (Categories & JunkCategories.SellMask) != 0;
}

internal static unsafe class InventoryScanner
{
    /// <summary>Full spiritbond, in the units the game stores it in.</summary>
    private const ushort MaxSpiritbond = 10000;

    /// <summary>Bags. These are the only containers a shop will sell out of by default.</summary>
    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    /// <summary>
    /// The armoury chest is also reachable from the shop's sell interface. Note the ids are
    /// NOT contiguous - ArmoryMainHand is 3500, above ArmoryRings at 3300 - so this must be an
    /// explicit set, not a range check. ArmorySoulCrystal is deliberately absent: soul crystals
    /// are not sellable.
    /// </summary>
    internal static readonly InventoryType[] Armoury =
    [
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    ];

    internal static readonly InventoryType[] RetainerBags =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    internal static bool IsArmoury(InventoryType container) => System.Array.IndexOf(Armoury, container) >= 0;

    /// <summary>The player's own sellable containers, honouring the armoury setting.</summary>
    public static List<InventoryType> PlayerContainers(Configuration cfg)
    {
        var containers = new List<InventoryType>(PlayerBags);
        if (cfg.IncludeArmoryChest)
            containers.AddRange(Armoury);
        return containers;
    }

    /// <summary>
    /// Which containers hold each item. "Duplicated across containers" is the one category that
    /// cannot be decided from a single slot, and it is most useful spanning bags AND the open
    /// retainer - so this is built once over everything and handed to both scans.
    /// </summary>
    public static Dictionary<uint, HashSet<InventoryType>> BuildContainerMap(IEnumerable<InventoryType> containers)
    {
        var map = new Dictionary<uint, HashSet<InventoryType>>();

        var manager = InventoryManager.Instance();
        if (manager is null)
            return map;

        foreach (var type in containers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container is null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot is null || slot->ItemId == 0)
                    continue;

                var id = slot->GetBaseItemId();
                if (id == 0)
                    continue;

                if (!map.TryGetValue(id, out var seen))
                    map[id] = seen = [];
                seen.Add(type);
            }
        }

        return map;
    }

    public static List<JunkStack> Scan(
        IReadOnlyList<InventoryType> containers,
        Configuration cfg,
        JunkIndex index,
        IReadOnlyDictionary<uint, HashSet<InventoryType>> containersPerItem)
    {
        var results = new List<JunkStack>();

        var manager = InventoryManager.Instance();
        if (manager is null)
            return results;

        var itemSheet = Services.Data.GetExcelSheet<Item>();

        foreach (var type in containers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container is null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot is null || slot->ItemId == 0 || slot->Quantity <= 0)
                    continue;

                // GetBaseItemId strips the HQ/collectable bit that GetItemId folds in,
                // so it is the id that matches the Item sheet and our index.
                var itemId = slot->GetBaseItemId();
                if (itemId == 0)
                    continue;

                var facts = index.DescribeItem(itemId, cfg);
                if (!facts.Known)
                    continue;

                var isHq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var categories = facts.PassesRails ? facts.Categories : JunkCategory.None;

                // Stack-dependent categories. These are added even when the item-level rails
                // failed, because the advisory ones are observations rather than sale candidates.
                if (facts.PassesRails
                    && facts.StackSize > 1
                    && slot->Quantity < cfg.SmallStackThreshold)
                {
                    categories |= JunkCategory.SmallStack;
                }

                if (containersPerItem.TryGetValue(itemId, out var seen) && seen.Count > 1)
                    categories |= JunkCategory.Duplicated;

                if (slot->SpiritbondOrCollectability >= MaxSpiritbond)
                    categories |= JunkCategory.FullSpiritbond;

                if (isHq && index.IsCraftingIngredient(itemId))
                    categories |= JunkCategory.LowerableHq;

                // Per-stack safety rails only gate the SELL-able half; an HQ or melded stack can
                // still legitimately show up as advisory.
                if (!PassesStackRails(slot, cfg))
                    categories &= ~JunkCategories.SellMask;

                if (categories == JunkCategory.None)
                    continue;

                var name = itemSheet.TryGetRow(itemId, out var row) ? row.Name.ExtractText() : $"#{itemId}";

                results.Add(new JunkStack(
                    Container: type,
                    Slot: (short)i,
                    ItemId: itemId,
                    Quantity: slot->Quantity,
                    IsHq: isHq,
                    Name: name,
                    UnitSellPrice: facts.SellPrice,
                    UnitBuyPrice: facts.BuyPrice,
                    Categories: categories,
                    Detail: facts.Detail));
            }
        }

        return results;
    }

    /// <summary>
    /// Per-stack safety rails. These need the live slot, so the static classifier cannot apply
    /// them - and because they describe the stack rather than the item id, they have to be
    /// re-checked immediately before acting, not just at scan time.
    /// </summary>
    private static bool PassesStackRails(InventoryItem* slot, Configuration cfg)
    {
        if (cfg.SkipHq && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))
            return false;
        if (cfg.SkipCollectables && slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable))
            return false;

        if (!cfg.SkipSpiritbondOrMateria)
            return true;

        if (slot->SpiritbondOrCollectability > 0)
            return false;

        for (var i = 0; i < 5; i++)
        {
            if (slot->Materia[i] != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Re-read a slot and confirm it still holds what we queued, and that the stack still passes
    /// the safety rails.
    ///
    /// Checking the item id alone is not enough: the player can move things mid-run, so the slot
    /// could hold the same item as an HQ, collectable or melded stack by the time we act. This is
    /// the last gate before an irreversible sale.
    /// </summary>
    public static bool SlotStillSellable(InventoryType container, short slot, uint itemId, Configuration cfg, out int quantity)
    {
        quantity = 0;
        var manager = InventoryManager.Instance();
        if (manager is null)
            return false;

        var inventory = manager->GetInventoryContainer(container);
        if (inventory is null || !inventory->IsLoaded || slot < 0 || slot >= inventory->Size)
            return false;

        var item = inventory->GetInventorySlot(slot);
        if (item is null || item->GetBaseItemId() != itemId)
            return false;

        if (!PassesStackRails(item, cfg))
            return false;

        quantity = item->Quantity;
        return quantity > 0;
    }
}

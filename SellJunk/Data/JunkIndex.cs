using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Lumina.Excel.Sheets;

namespace SellJunk.Data;

/// <summary>What the gathering sheets say about an item.</summary>
internal readonly record struct GatherFacts(
    int MinUntimedNodeLevel,
    int MinNodeLevel,
    bool HasUntimedNode,
    bool Hidden);

/// <summary>
/// Static, patch-lifetime classification tables built once from Lumina.
/// Pure Excel data, so it is safe to build off the framework thread.
/// </summary>
internal sealed class JunkIndex
{
    // Node-based gathering types. 4/5 are spearfishing, whose GatheringPointBase.Item
    // column points at SpearfishingItem rather than GatheringItem - resolving those
    // against GatheringItem would silently produce garbage, so they are excluded.
    private static readonly HashSet<uint> NodeGatheringTypes = [0, 1, 2, 3];

    // These are published by reference-swap, never mutated in place after they go live.
    // RebuildCraftDemand runs on a background thread while the framework thread is calling
    // DescribeItem, and Dictionary tolerates concurrent readers only if nobody is writing - so each
    // builder fills a local and assigns it in one go. Readers then either see the whole old
    // table or the whole new one.
    private volatile HashSet<uint> _vendorItems = [];
    private volatile Dictionary<uint, GatherFacts> _gather = [];
    private volatile Dictionary<uint, int> _craftDemand = [];
    private volatile HashSet<uint> _isIngredient = [];

    /// <summary>How many recipes use each item as an ingredient. Drives the single-recipe rule.</summary>
    private volatile Dictionary<uint, int> _recipeUseCount = [];

    // Volatile so the write below cannot be reordered ahead of the table writes it guards.
    private volatile bool _ready;

    public bool Ready => _ready;

    /// <summary>Highest ClassJobLevel present in RecipeLevelTable - the current max craft level.</summary>
    public int DetectedMaxCraftLevel { get; private set; } = 100;

    public void Build(bool rollUpCraftChain, CancellationToken token = default)
    {
        var data = Services.Data;

        BuildVendorSet(data);
        token.ThrowIfCancellationRequested();
        BuildGatheringFacts(data);
        token.ThrowIfCancellationRequested();
        BuildCraftDemand(data, rollUpCraftChain);
        token.ThrowIfCancellationRequested();

        _ready = true;
        Services.Log.Information(
            $"JunkIndex built: {_vendorItems.Count} vendor items, {_gather.Count} gatherable items, " +
            $"{_isIngredient.Count} craft ingredients, max craft level {DetectedMaxCraftLevel}.");
    }

    /// <summary>The craft-chain rollup is the only rule affected by config, so it can be rebuilt alone.</summary>
    public void RebuildCraftDemand(bool rollUpCraftChain) => BuildCraftDemand(Services.Data, rollUpCraftChain);

    /// <summary>Whether this item is an ingredient in any recipe.</summary>
    public bool IsCraftingIngredient(uint itemId) => _isIngredient.Contains(itemId);

    /// <summary>Counts of what was indexed, for the settings window. Safe to read from any thread.</summary>
    public (int Vendor, int Gatherable, int Ingredients) Counts =>
        (_vendorItems.Count, _gather.Count, _isIngredient.Count);

    // ------------------------------------------------------------------ vendor

    private void BuildVendorSet(Dalamud.Plugin.Services.IDataManager data)
    {
        var vendorItems = new HashSet<uint>();

        // GilShopItem is a subrow sheet: one parent row per shop, one subrow per stocked item.
        // It carries no price column - the gil cost is Item.PriceMid - so membership here is
        // what proves an item is actually re-buyable. Plenty of items have a nonzero PriceMid
        // while being stocked by no vendor at all.
        var shopItems = data.GetSubrowExcelSheet<GilShopItem>();
        if (shopItems is null)
            return;

        foreach (var parent in shopItems)
        {
            foreach (var sub in parent)
            {
                var id = sub.Item.RowId;
                if (id == 0)
                    continue;

                // A listing gated behind a quest or achievement is not something the player can
                // necessarily re-buy, so it does not make selling reversible. Skip those rather
                // than count them as vendor-stocked.
                if (sub.AchievementRequired.RowId != 0)
                    continue;
                if (sub.QuestRequired.Any(static q => q.RowId != 0))
                    continue;

                vendorItems.Add(id);
            }
        }

        _vendorItems = vendorItems;
    }

    // --------------------------------------------------------------- gathering

    /// <summary>Every ET hour set - GatherBuddy's "always up" sentinel.</summary>
    private const uint AllHoursMask = 0x00FFFFFF;

    /// <summary>
    /// Decode an ephemeral start/end pair (military Eorzea time) into a 24-bit hour mask.
    /// Mirrors GatherBuddy's ConvertFromEphemeralTime: an unset (65535/65535) or degenerate
    /// (start == end) window means the node is up around the clock, not that it is timed.
    /// </summary>
    private static uint EphemeralMask(ushort start, ushort end)
    {
        if (start == end || start > 2400 || end > 2400)
            return AllHoursMask;

        var mask = 0u;
        int from = start / 100, to = end / 100;
        if (to < from)
            to += 24;
        for (var hour = from; hour < to; hour++)
            mask |= 1u << (hour % 24);
        return mask;
    }

    private static uint RarePopMask(GatheringRarePopTimeTable table)
    {
        var mask = 0u;
        for (var i = 0; i < table.StartTime.Count && i < table.Duration.Count; i++)
        {
            var durationBase = table.Duration[i];
            if (durationBase == 0)
                continue;

            // 160 encodes two ET hours, not 1h40m. Without this fixup a subset of
            // unspoiled nodes decode to the wrong uptime.
            var duration = durationBase == 160 ? (ushort)200 : durationBase;
            var start = table.StartTime[i];
            var end = (ushort)((start + duration) % 2400);
            mask |= EphemeralMask(start, end);
        }
        return mask;
    }

    /// <summary>
    /// A node is timed only if its uptime mask is not the full 24 hours. Checking
    /// GatheringRarePopTimeTable alone (the common shortcut) misses every ephemeral node,
    /// and checking EphemeralStartTime alone misclassifies the always-up sentinels.
    /// </summary>
    private static bool IsTimedPoint(GatheringPointTransient transient)
    {
        uint mask;
        if (transient.GatheringRarePopTimeTable.RowId == 0)
            mask = EphemeralMask(transient.EphemeralStartTime, transient.EphemeralEndTime);
        else if (transient.GatheringRarePopTimeTable.TryGetValue(out var table))
            mask = RarePopMask(table);
        else
            mask = AllHoursMask;

        return mask != AllHoursMask;
    }

    private void BuildGatheringFacts(Dalamud.Plugin.Services.IDataManager data)
    {
        var gather = new Dictionary<uint, GatherFacts>();

        var pointSheet = data.GetExcelSheet<GatheringPoint>();
        var transientSheet = data.GetExcelSheet<GatheringPointTransient>();
        var baseSheet = data.GetExcelSheet<GatheringPointBase>();
        var gatheringItemSheet = data.GetExcelSheet<GatheringItem>();
        var itemPointSheet = data.GetSubrowExcelSheet<GatheringItemPoint>();

        // Node level + type live on the base; timing lives per GatheringPoint, keyed by the
        // POINT's row id (not the base's). The same base can back several points with
        // different timing, so timing has to be resolved per point and then folded in.
        var perPoint = new List<(uint PointRow, uint BaseRow, int Level, bool Timed)>();
        foreach (var point in pointSheet)
        {
            var baseRow = point.GatheringPointBase.RowId;
            if (baseRow == 0 || !baseSheet.TryGetRow(baseRow, out var node))
                continue;

            // Types 4/5 are spearfishing, whose Item[] holds SpearfishingItem rows.
            if (!NodeGatheringTypes.Contains(node.GatheringType.RowId))
                continue;

            int level = node.GatheringLevel;
            if (level <= 0)
                continue;

            var timed = transientSheet.TryGetRow(point.RowId, out var transient) && IsTimedPoint(transient);
            perPoint.Add((point.RowId, baseRow, level, timed));
        }

        // GatheringItemPoint attaches extra items to specific points - notably every
        // hidden item (Titanium Ore and friends). Omitting it loses those attachments.
        var extraByPoint = new Dictionary<uint, List<uint>>();
        foreach (var parent in itemPointSheet)
        {
            foreach (var sub in parent)
            {
                var pointRow = sub.GatheringPoint.RowId;
                if (pointRow == 0)
                    continue;
                if (!extraByPoint.TryGetValue(pointRow, out var list))
                    extraByPoint[pointRow] = list = [];
                list.Add(parent.RowId);
            }
        }

        foreach (var (pointRow, baseRow, level, timed) in perPoint)
        {
            if (baseSheet.TryGetRow(baseRow, out var node))
            {
                foreach (var slot in node.Item)
                    Attach(slot.RowId, level, timed);
            }

            if (extraByPoint.TryGetValue(pointRow, out var extras))
            {
                foreach (var gatheringItemRow in extras)
                    Attach(gatheringItemRow, level, timed);
            }
        }

        _gather = gather;
        return;

        void Attach(uint gatheringItemRow, int level, bool timed)
        {
            if (gatheringItemRow == 0 || !gatheringItemSheet.TryGetRow(gatheringItemRow, out var gatheringItem))
                return;

            // GatheringItem.Item is an UNTYPED RowRef targeting [Item, EventItem]; 362 rows
            // point at EventItem (ids >= 2,000,000). Resolving those as Item ids is silent
            // garbage, so gate on the id range and on the ref actually being an Item.
            var itemId = gatheringItem.Item.RowId;
            if (itemId is 0 || itemId >= 1_000_000)
                return;
            if (!gatheringItem.Item.TryGetValue<Item>(out _))
                return;

            var hidden = gatheringItem.IsHidden;

            if (gather.TryGetValue(itemId, out var prev))
            {
                gather[itemId] = new GatherFacts(
                    MinUntimedNodeLevel: !timed
                        ? Math.Min(prev.MinUntimedNodeLevel == 0 ? int.MaxValue : prev.MinUntimedNodeLevel, level)
                        : prev.MinUntimedNodeLevel,
                    MinNodeLevel: Math.Min(prev.MinNodeLevel, level),
                    HasUntimedNode: prev.HasUntimedNode || !timed,
                    // Hidden only sticks if EVERY node slot exposing this item is hidden.
                    Hidden: prev.Hidden && hidden);
            }
            else
            {
                gather[itemId] = new GatherFacts(
                    MinUntimedNodeLevel: timed ? 0 : level,
                    MinNodeLevel: level,
                    HasUntimedNode: !timed,
                    Hidden: hidden);
            }
        }
    }

    // ------------------------------------------------------------------ crafts

    private void BuildCraftDemand(Dalamud.Plugin.Services.IDataManager data, bool rollUpCraftChain)
    {
        var craftDemand = new Dictionary<uint, int>();
        var isIngredient = new HashSet<uint>();
        var recipeUseCount = new Dictionary<uint, int>();

        var recipeSheet = data.GetExcelSheet<Recipe>();
        var levelSheet = data.GetExcelSheet<RecipeLevelTable>();
        if (recipeSheet is null || levelSheet is null)
            return;

        var recipes = new List<(int Level, uint Result, uint[] Ingredients)>();
        var maxLevel = 0;
        foreach (var recipe in recipeSheet)
        {
            var result = recipe.ItemResult.RowId;
            if (result == 0)
                continue;

            // RecipeLevelTable row 0 exists and reports ClassJobLevel 0. That is "unknown",
            // not "a level-0 craft" - letting it through would hand every ingredient of a
            // placeholder recipe a demand of 0 and therefore mark it as junk.
            if (!recipe.RecipeLevelTable.TryGetValue(out var levelRow))
                continue;
            int level = levelRow.ClassJobLevel;
            if (level <= 0)
                continue;

            // Derive the cap from levels a recipe actually reaches, not from every row in
            // RecipeLevelTable. The sheet can carry rows for a level cap the patch has not
            // enabled yet, and over-stating the cap is the unsafe direction: it would make
            // current max-level materials look "below max" and therefore disposable.
            maxLevel = Math.Max(maxLevel, level);

            var ingredients = new List<uint>(8);
            for (var i = 0; i < recipe.Ingredient.Count; i++)
            {
                // Ingredient slots 6 and 7 (the crystal slots) store -1 for "empty", which
                // Lumina surfaces as uint.MaxValue - not 0. Slots 0-5 use 0. Comparing RowId
                // never resolves the row, so this also avoids the InvalidOperationException
                // that .Value throws on those slots.
                var ingredient = recipe.Ingredient[i].RowId;
                if (ingredient is 0 or uint.MaxValue)
                    continue;
                if (recipe.AmountIngredient[i] == 0)
                    continue;
                ingredients.Add(ingredient);
            }

            if (ingredients.Count == 0)
                continue;

            recipes.Add((level, result, ingredients.ToArray()));
            foreach (var ingredient in ingredients)
            {
                isIngredient.Add(ingredient);
                recipeUseCount[ingredient] = recipeUseCount.GetValueOrDefault(ingredient) + 1;
            }
        }

        if (maxLevel > 0)
            DetectedMaxCraftLevel = maxLevel;

        // demand[item] = the highest craft level this item ultimately contributes to.
        // With rollup on, an ingredient inherits the demand of the thing it is crafted into,
        // so a level-60 intermediate feeding a level-100 recipe is not treated as junk.
        // Relaxation to a fixpoint; the pass cap also guards against a cyclic craft graph.
        const int maxPasses = 32;
        var converged = !rollUpCraftChain;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            var changed = false;

            foreach (var (level, result, ingredients) in recipes)
            {
                var effective = level;
                if (rollUpCraftChain && craftDemand.TryGetValue(result, out var downstream) && downstream > effective)
                    effective = downstream;

                foreach (var ingredient in ingredients)
                {
                    if (!craftDemand.TryGetValue(ingredient, out var current) || current < effective)
                    {
                        craftDemand[ingredient] = effective;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                converged = true;
                break;
            }

            if (!rollUpCraftChain)
                break; // one pass is exact when nothing propagates
        }

        if (!converged)
        {
            // Demands are still rising, so every value in the table is an UNDER-estimate - and
            // under-estimating demand is exactly what makes a max-level material look disposable.
            // Fail closed: pin everything to the cap so the craft rule flags nothing this session.
            Services.Log.Warning(
                "Craft-chain rollup did not converge; treating every ingredient as max-level so nothing is wrongly flagged.");
            foreach (var key in craftDemand.Keys.ToArray())
                craftDemand[key] = int.MaxValue;
        }

        // Publish together. DescribeItem reads _craftDemand then _isIngredient then
        // _recipeUseCount, so this order never exposes a demand table without its companions.
        _craftDemand = craftDemand;
        _isIngredient = isIngredient;
        _recipeUseCount = recipeUseCount;
    }

    // -------------------------------------------------------------- classify

    /// <summary>Static per-item facts the scanner needs, resolved once per slot.</summary>
    internal readonly record struct ItemFacts(
        bool Known,
        bool PassesRails,
        uint BuyPrice,
        uint SellPrice,
        uint StackSize,
        JunkCategory Categories,
        string Detail);

    /// <summary>
    /// Work out which categories an item id falls into. Only the item-definition half lives here;
    /// the stack-dependent categories (small stacks, duplicates, HQ, spiritbond) are added by the
    /// scanner, which can see the actual slot.
    ///
    /// The keep list and the item-definition safety rails are applied first and short-circuit
    /// everything, so a rail-failing item is never reported in any category.
    /// </summary>
    internal ItemFacts DescribeItem(uint itemId, Configuration cfg)
    {
        if (!Services.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
            return new ItemFacts(false, false, 0, 0, 1, JunkCategory.None, "unknown item");

        var railsOk = !cfg.Blacklist.Contains(itemId)
                      && item.PriceLow >= cfg.MinSellPrice
                      && !(cfg.SkipUnique && item.IsUnique)
                      && !(cfg.SkipIndisposable && item.IsIndisposable)
                      && !(cfg.SkipCollectables && item.IsCollectable);

        if (!railsOk)
            return new ItemFacts(true, false, item.PriceMid, item.PriceLow, item.StackSize, JunkCategory.None, "protected");

        var craftCap = cfg.AutoDetectMaxCraftLevel ? DetectedMaxCraftLevel : cfg.MaxCraftLevel;
        var categories = JunkCategory.None;
        var details = new List<string>(3);

        var vendorStocked = _vendorItems.Contains(itemId) && item.PriceMid > 0;
        if (vendorStocked && item.PriceMid < cfg.MaxVendorPrice)
        {
            categories |= JunkCategory.GilBuyable;
            details.Add($"re-buy {item.PriceMid}g");
        }

        if (_gather.TryGetValue(itemId, out var facts) && !facts.Hidden)
        {
            if (cfg.ExcludeTimedNodes)
            {
                if (facts.HasUntimedNode && facts.MinUntimedNodeLevel > 0 && facts.MinUntimedNodeLevel <= cfg.MaxNodeLevel)
                {
                    categories |= JunkCategory.EasilyGathered;
                    details.Add($"untimed Lv{facts.MinUntimedNodeLevel} node");
                }
            }
            else if (facts.MinNodeLevel > 0 && facts.MinNodeLevel <= cfg.MaxNodeLevel)
            {
                categories |= JunkCategory.EasilyGathered;
                details.Add($"Lv{facts.MinNodeLevel} node");
            }
        }

        if (_craftDemand.TryGetValue(itemId, out var demand))
        {
            // With rollup on, `demand` already accounts for the item being an intermediate that
            // feeds a higher-level craft, so a low number here really is disposable.
            if (demand < craftCap)
            {
                categories |= JunkCategory.SubMaxCraft;
                details.Add($"tops out at Lv{demand} crafts");
            }
        }
        else if (!_isIngredient.Contains(itemId))
        {
            categories |= JunkCategory.NoRecipe;
            details.Add("not a crafting material");
        }

        if (_recipeUseCount.TryGetValue(itemId, out var uses) && uses == 1)
        {
            categories |= JunkCategory.SingleRecipe;
            details.Add("used by one recipe");
        }

        // The craft chain as a veto: an item that ultimately feeds a max-level craft is spared
        // whatever else matched it.
        if (cfg.CraftChainVeto && _craftDemand.TryGetValue(itemId, out var vetoDemand) && vetoDemand >= craftCap)
            return new ItemFacts(true, true, item.PriceMid, item.PriceLow, item.StackSize,
                JunkCategory.None, $"feeds Lv{vetoDemand} crafts");

        // Optional AND-gate restoring the original conservative behaviour: if a vendor does not
        // stock it cheaply, nothing else it matched can stage it.
        if (cfg.RequireVendorBuyable && !categories.HasFlag(JunkCategory.GilBuyable))
            categories &= ~JunkCategories.SellMask;

        return new ItemFacts(true, true, item.PriceMid, item.PriceLow, item.StackSize,
            categories, string.Join(", ", details));
    }
}

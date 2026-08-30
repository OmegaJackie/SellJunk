using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace SellJunk.Game;

/// <summary>
/// The two game actions this plugin performs, plus the state reads that gate them.
///
/// The two use deliberately different mechanisms, because only one of them is proven:
///
///  * Retrieving from a retainer goes through AgentRetainer's inherited
///    InventoryContextEvent.HandleCallback. ClientStructs documents that agent's callback
///    parameters (Retrieve = 0), so the call is unambiguous.
///
///  * Selling does NOT. There is no sell function anywhere in ClientStructs - ShopEventHandler
///    exposes only ExecuteBuy - and the Shop addon has no sell callback. The game sells purely
///    from the inventory right-click menu, and ShopEventHandler's callback parameter for that
///    entry is undocumented. So selling drives the real context menu: open it for the slot,
///    find the entry whose label matches Addon sheet row 93, and fire that entry. This is the
///    exact path a human click takes, it needs no signature, and it is what SimpleTweaks and
///    AutoRetainer both ship.
/// </summary>
internal static unsafe class GameActions
{
    /// <summary>AgentRetainer context-menu command ids (documented in ClientStructs).</summary>
    private const ulong RetrieveFromRetainer = 0;

    /// <summary>Addon sheet row holding the localised "Sell" context-menu label.</summary>
    private const uint SellLabelAddonRow = 93;

    private static string? _sellLabel;

    /// <summary>Never hardcode "Sell" - the label is localised.</summary>
    public static string SellLabel =>
        _sellLabel ??= Services.Data.GetExcelSheet<Addon>().TryGetRow(SellLabelAddonRow, out var row)
            ? row.Text.ExtractText()
            : "Sell";

    // ------------------------------------------------------------------- shop

    public static ShopEventHandler* GetShopHandler()
    {
        var proxy = ShopEventHandler.AgentProxy.Instance();
        return proxy is null ? null : proxy->Handler;
    }

    public static bool ShopIsOpen() => GetShopHandler() is not null;

    /// <summary>The Shop window itself, which can still be animating in after the handler exists.</summary>
    private static bool ShopAddonReady()
    {
        var addon = (AtkUnitBase*)Services.GameGui.GetAddonByName("Shop", 1).Address;
        return addon is not null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded;
    }

    /// <summary>
    /// True when a normal shop is open and idle. CurrentMode 1 is the normal tab (2 is buyback),
    /// and the three transaction flags mean the client is mid-trade and must not be poked.
    /// </summary>
    public static bool ShopReadyToSell()
    {
        var handler = GetShopHandler();
        if (handler is null)
            return false;

        return handler->CurrentMode == 1
               && !handler->StartingSell
               && !handler->WaitingForSellConfirm
               && !handler->WaitingForTransactionToFinish
               && ShopAddonReady();
    }

    /// <summary>Fire the shop's "Sell" context-menu entry for one inventory slot.</summary>
    public static bool TrySell(InventoryType container, short slot)
    {
        var ctx = AgentInventoryContext.Instance();
        if (ctx is null)
            return false;

        AtkUnitBase* menu = null;
        try
        {
            ctx->OpenForItemSlot(container, slot, 0, OwnerAddonId(container));

            var menuId = ctx->AgentInterface.GetAddonId();
            if (menuId == 0)
                return false;

            menu = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)menuId);
            if (menu is null)
                return false;

            var label = SellLabel;
            for (var i = 0; i < ctx->ContextItemCount; i++)
            {
                // Note the missing 't' in ContexItemStartIndex - that typo is in ClientStructs.
                var param = ctx->EventParams[ctx->ContexItemStartIndex + i];

                // Labels arrive as either String or ManagedString depending on the entry.
                if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
                    continue;
                if (param.GetValueAsString() != label)
                    continue;

                // A greyed-out entry means the game refuses to sell this here. Firing it is a
                // no-op, which would spin the queue forever on the same slot.
                if (ctx->IsContextItemDisabled(i))
                    return false;

                var values = stackalloc AtkValue[5];
                values[0].SetInt(0);
                values[1].SetInt(i);
                values[2].SetUInt(0);
                values[3].SetInt(0);
                values[4].SetInt(0);
                menu->FireCallback(5, values);
                return true;
            }

            return false;
        }
        finally
        {
            // OpenForItemSlot puts a real, visible menu on screen. Leaving it up on any exit
            // path wedges the UI and makes the next open misbehave.
            ctx->AgentInterface.Hide();
            if (menu is not null)
                menu->Close(false);
        }
    }

    /// <summary>
    /// Answer the sell confirmation for <paramref name="expectedItemName"/>, if one is up.
    ///
    /// Two independent gates, because clicking Yes on the wrong dialog could confirm something
    /// destructive:
    ///   1. ShopEventHandler.WaitingForSellConfirm - the game's own "waiting on a sell
    ///      confirmation" flag, so we know a sale is actually pending.
    ///   2. The prompt text must name the item we are selling. Several SelectYesno instances can
    ///      be alive at once and the first visible one is not necessarily ours.
    /// The Yes button is deliberately NOT force-enabled: if the game has it disabled, that is a
    /// reason to wait a tick, not to override it.
    /// </summary>
    public static bool TryConfirmSell(string expectedItemName)
    {
        var handler = GetShopHandler();
        if (handler is null || !handler->WaitingForSellConfirm)
            return false;

        if (string.IsNullOrEmpty(expectedItemName))
            return false;

        for (var i = 1; i <= 4; i++)
        {
            var yesno = (AddonSelectYesno*)Services.GameGui.GetAddonByName("SelectYesno", i).Address;
            if (yesno is null || !yesno->AtkUnitBase.IsVisible)
                continue;

            if (yesno->PromptText is null)
                continue;

            var prompt = yesno->PromptText->NodeText.ToString();
            if (string.IsNullOrEmpty(prompt) || !prompt.Contains(expectedItemName, StringComparison.OrdinalIgnoreCase))
                continue;

            yesno->AtkUnitBase.FireCallbackInt(0);
            return true;
        }

        return false;
    }

    // --------------------------------------------------------------- retainer

    public static AgentRetainer* GetRetainerAgent()
    {
        var module = AgentModule.Instance();
        return module is null ? null : (AgentRetainer*)module->GetAgentByInternalId(AgentId.Retainer);
    }

    /// <summary>
    /// The retainer windows we might dock against, most specific first. Which one is up depends
    /// on how far into the retainer you are: the bell list, then the retainer's menu, then the
    /// inventory grid once you open "entrust or withdraw".
    /// </summary>
    private static readonly string[] RetainerAddons =
    [
        "InventoryRetainerLarge",
        "InventoryRetainer",
        "RetainerGrid0",
        "SelectString",
        "RetainerList",
    ];

    /// <summary>
    /// Screen rect of whichever retainer window is currently up, for docking against.
    /// Framework thread only - it walks live addon memory.
    /// </summary>
    public static bool TryGetRetainerAnchor(out Vector2 position, out Vector2 size)
    {
        position = default;
        size = default;

        // Gate on the summoning bell first. "SelectString" in the list below is the generic NPC
        // menu addon - without this check the panel would dock itself to any shop, quest or
        // aetheryte dialog in the game. AutoRetainer's own overlay gates the same way.
        if (!Services.Condition[ConditionFlag.OccupiedSummoningBell])
            return false;

        foreach (var name in RetainerAddons)
        {
            var addon = (AtkUnitBase*)Services.GameGui.GetAddonByName(name, 1).Address;
            if (addon is null || !addon->IsVisible || addon->RootNode is null)
                continue;
            if (addon->UldManager.LoadedState != AtkLoadState.Loaded)
                continue;

            // X/Y are already screen coordinates; the root node's size is unscaled, so the
            // window's own scale has to be applied to get the on-screen footprint.
            var scale = addon->Scale;
            position = new Vector2(addon->X, addon->Y);
            size = new Vector2(addon->RootNode->Width * scale, addon->RootNode->Height * scale);

            if (size.X > 0 && size.Y > 0)
                return true;
        }

        return false;
    }

    /// <summary>True when a retainer session is live at a summoning bell.</summary>
    public static bool RetainerOpen()
    {
        var agent = GetRetainerAgent();
        return agent is not null && agent->AgentInterface.IsAgentActive();
    }

    /// <summary>
    /// Pull one stack out of the open retainer into the player's bags. There is no
    /// "sell to NPC from a retainer bag" interaction in the game, so this is the necessary
    /// first half of clearing retainer junk.
    /// </summary>
    public static bool TryRetrieve(InventoryType container, short slot)
    {
        var agent = GetRetainerAgent();
        if (agent is null || !agent->AgentInterface.IsAgentActive())
            return false;

        agent->InventoryContextEvent.HandleCallback((uint)slot, container, 0, RetrieveFromRetainer);
        return true;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The context menu needs the id of the addon that "owns" the slot, and the armoury chest
    /// is owned by a different agent than the bags. Passing the wrong one yields a menu with
    /// the wrong entries.
    /// </summary>
    private static uint OwnerAddonId(InventoryType container)
    {
        var module = AgentModule.Instance();
        if (module is null)
            return 0;

        if (InventoryScanner.IsArmoury(container))
        {
            // Only valid while the Armoury Chest window is actually open; fall through to the
            // inventory agent otherwise, or armoury items would silently never sell.
            var armoury = module->GetAgentByInternalId(AgentId.ArmouryBoard);
            if (armoury is not null)
            {
                var armouryAddon = armoury->GetAddonId();
                if (armouryAddon != 0)
                    return armouryAddon;
            }
        }

        var agent = module->GetAgentByInternalId(AgentId.Inventory);
        return agent is null ? 0 : agent->GetAddonId();
    }

    /// <summary>
    /// Whether a retrieved stack of this item has anywhere to land: either a free slot, or an
    /// existing partial stack of the same item it can merge into. Full bags do not necessarily
    /// mean a retrieve is impossible, which is why this is asked per item rather than just
    /// counting empty slots.
    /// </summary>
    public static bool HasRoomFor(uint itemId)
    {
        if (FreeBagSlots() > 0)
            return true;

        var manager = InventoryManager.Instance();
        if (manager is null)
            return false;

        var maxStack = Services.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var row) ? row.StackSize : 1;
        if (maxStack <= 1)
            return false;

        for (var type = InventoryType.Inventory1; type <= InventoryType.Inventory4; type++)
        {
            var container = manager->GetInventoryContainer(type);
            if (container is null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot is not null && slot->GetBaseItemId() == itemId && slot->Quantity < maxStack)
                    return true;
            }
        }

        return false;
    }

    /// <summary>Free slots across the four main bags.</summary>
    public static int FreeBagSlots()
    {
        var manager = InventoryManager.Instance();
        if (manager is null)
            return 0;

        var free = 0;
        for (var type = InventoryType.Inventory1; type <= InventoryType.Inventory4; type++)
        {
            var container = manager->GetInventoryContainer(type);
            if (container is null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot is null || slot->ItemId == 0)
                    free++;
            }
        }

        return free;
    }
}

namespace SellJunk.Game;

/// <summary>
/// Sells a queue of junk stacks to whichever shop is currently open.
///
/// Selling past the buyback list is irreversible, and buyback only holds ten entries, so the
/// loop is deliberately unhurried: one item per cooldown window, each confirmed by re-reading
/// the slot before the next one starts.
/// </summary>
internal sealed class ShopSeller(Configuration cfg) : SlotActionRunner(cfg)
{
    protected override string ActionName => "Sell";

    protected override bool ContextValid(out string reason)
    {
        if (!GameActions.ShopIsOpen())
        {
            reason = "shop closed";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Idle means the shop is on the normal tab with no transaction in flight. While a
    /// confirmation is up this is false, so the loop waits rather than stacking a second sell
    /// on top of the pending one.
    /// </summary>
    protected override bool ContextIdle => GameActions.ShopReadyToSell();

    /// <summary>Ticks to wait after answering a prompt before answering another.</summary>
    private const int ConfirmCooldownTicks = 20;

    private int _confirmCooldown;

    /// <summary>
    /// Confirmations are user-configurable per category, so a prompt may or may not appear for
    /// any given item. This answers one if it is up and does nothing otherwise - the loop must
    /// never block waiting for a dialog that is never coming.
    ///
    /// The cooldown matters: WaitingForSellConfirm stays set for several frames after the click
    /// lands, and without it we would re-fire the callback every frame of that window.
    /// </summary>
    protected override void Poll()
    {
        if (_confirmCooldown > 0)
        {
            _confirmCooldown--;
            return;
        }

        if (Pending is not { } stack)
            return;

        if (GameActions.TryConfirmSell(stack.Name))
            _confirmCooldown = ConfirmCooldownTicks;
    }

    protected override bool TryAct(in JunkStack stack)
    {
        // Defence in depth: the keep list was already applied when the stack was classified,
        // but this is the last point before an irreversible sale, so check it again here.
        if (Cfg.Blacklist.Contains(stack.ItemId))
        {
            Services.Log.Warning($"Refusing to sell {stack.Name} - it is on the keep list.");
            return false;
        }

        return GameActions.TrySell(stack.Container, stack.Slot);
    }
}

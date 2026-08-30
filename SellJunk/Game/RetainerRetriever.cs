namespace SellJunk.Game;

/// <summary>
/// Pulls junk out of the retainer that is currently open at a summoning bell.
///
/// This is the only thing a plugin can do about retainer junk: the game has no
/// "sell to NPC from retainer" interaction, so clearing it is always retrieve-then-sell.
/// </summary>
internal sealed class RetainerRetriever(Configuration cfg) : SlotActionRunner(cfg)
{
    protected override string ActionName => "Retrieve";

    protected override bool ContextValid(out string reason)
    {
        if (!GameActions.RetainerOpen())
        {
            reason = "retainer closed";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Retrieving with nowhere to put the stack silently does nothing, which would otherwise look
    /// like a hang while each item timed out in turn. Full bags are not automatically fatal
    /// though - a stackable item can still merge into a partial stack - so this is decided per
    /// item rather than aborting the whole run on a zero free-slot count.
    /// </summary>
    protected override bool CanAct(in JunkStack stack, out string reason)
    {
        if (GameActions.HasRoomFor(stack.ItemId))
        {
            reason = string.Empty;
            return true;
        }

        reason = "no room in your bags";
        return false;
    }

    protected override bool ContextIdle => true;

    /// <summary>A retrieve into nearly-full bags can move only part of a stack.</summary>
    protected override bool RetryPartial => true;

    protected override bool TryAct(in JunkStack stack) => GameActions.TryRetrieve(stack.Container, stack.Slot);
}

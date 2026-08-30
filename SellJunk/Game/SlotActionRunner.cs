using System.Collections.Generic;

namespace SellJunk.Game;

/// <summary>
/// Shared drive loop for "walk a queue of inventory slots, do one thing to each, one at a time".
/// Selling to a shop and retrieving from a retainer are the same loop with different
/// preconditions and a different action.
///
/// The discipline that matters: one action per cooldown window, and never fire the next one
/// until the game has visibly consumed the last. Spamming these callbacks desyncs the client
/// from the server, which is how you lose items rather than sell them.
/// </summary>
internal abstract class SlotActionRunner(Configuration cfg)
{
    /// <summary>How long to wait for the game to acknowledge one action before abandoning it.</summary>
    private const int ActionTimeoutTicks = 300;

    /// <summary>
    /// How long the context may stay un-idle before we give up entirely. Without this, something
    /// that never clears - the buyback tab left open, a dialog we do not answer - would leave the
    /// queue spinning silently forever.
    /// </summary>
    private const int StallTimeoutTicks = 900;

    /// <summary>Cap on re-queues of a partially-moved stack, so a stuck stack cannot loop.</summary>
    private const int MaxAttempts = 3;

    private readonly Queue<(JunkStack Stack, int Attempts)> _queue = new();
    private JunkStack _pending;
    private int _pendingAttempts;
    private bool _hasPending;
    private int _waitTicks;
    private int _cooldown;
    private int _stalledTicks;

    protected Configuration Cfg { get; } = cfg;

    public bool Running { get; private set; }
    public int Completed { get; private set; }
    public int Skipped { get; private set; }
    public long GilTotal { get; private set; }
    public int Remaining => _queue.Count + (_hasPending ? 1 : 0);
    public string Status { get; protected set; } = string.Empty;

    /// <summary>The stack currently being acted on, for prompt matching. Null when idle.</summary>
    protected JunkStack? Pending => _hasPending ? _pending : null;

    protected abstract string ActionName { get; }

    /// <summary>Hard precondition. When this goes false the run is over (shop closed, retainer dismissed).</summary>
    protected abstract bool ContextValid(out string reason);

    /// <summary>Soft precondition - false just means "busy right now, try again next tick".</summary>
    protected abstract bool ContextIdle { get; }

    protected abstract bool TryAct(in JunkStack stack);

    /// <summary>
    /// Per-item precondition, checked at dequeue. False skips this one stack rather than ending
    /// the run, so one unplaceable item does not abandon the rest of the queue.
    /// </summary>
    protected virtual bool CanAct(in JunkStack stack, out string reason)
    {
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Runs every tick while the queue is live, before the state machine. Used for side
    /// effects that must happen even mid-action, such as answering a confirmation dialog.
    /// </summary>
    protected virtual void Poll() { }

    /// <summary>
    /// True when a stack that shrank but did not vanish should be re-queued for the remainder.
    /// Selling always takes the whole stack, so only retrieval sets this.
    /// </summary>
    protected virtual bool RetryPartial => false;

    public void Start(IEnumerable<JunkStack> stacks)
    {
        _queue.Clear();
        foreach (var stack in stacks)
            _queue.Enqueue((stack, 0));

        _hasPending = false;
        _cooldown = 0;
        _stalledTicks = 0;
        Completed = 0;
        Skipped = 0;
        GilTotal = 0;
        Running = _queue.Count > 0;
        Status = Running ? $"{ActionName}: {_queue.Count} stacks queued" : "nothing to do";
    }

    public void Stop(string reason)
    {
        if (!Running)
            return;

        _queue.Clear();
        _hasPending = false;
        Running = false;
        Status = reason;
        Services.Log.Information($"{ActionName} stopped: {reason} ({Completed} done, {Skipped} skipped).");
    }

    public void Tick()
    {
        if (!Running)
            return;

        if (!ContextValid(out var invalidReason))
        {
            Stop(invalidReason);
            return;
        }

        Poll();

        if (_hasPending)
        {
            AwaitPending();
            return;
        }

        if (_cooldown > 0)
        {
            _cooldown--;
            return;
        }

        if (!ContextIdle)
        {
            if (++_stalledTicks >= StallTimeoutTicks)
                Stop("gave up waiting for the game to be ready");
            return;
        }

        _stalledTicks = 0;
        StartNext();
    }

    private void AwaitPending()
    {
        // Success is observed, not assumed. Waiting on the game's own state is what makes this
        // safe to repeat.
        var stillThere = InventoryScanner.SlotStillSellable(
            _pending.Container, _pending.Slot, _pending.ItemId, Cfg, out var quantity);

        if (!stillThere)
        {
            Finish();
            return;
        }

        if (quantity < _pending.Quantity)
        {
            // Only part of the stack moved - a retrieve into nearly-full bags does this. Count
            // the progress, then come back for the rest rather than silently abandoning it.
            var moved = _pending.Quantity - quantity;
            Completed++;
            GilTotal += (long)_pending.UnitSellPrice * moved;

            if (RetryPartial && _pendingAttempts + 1 < MaxAttempts)
                _queue.Enqueue((_pending with { Quantity = quantity }, _pendingAttempts + 1));

            _hasPending = false;
            _cooldown = Cfg.TicksBetweenActions;
            return;
        }

        if (--_waitTicks > 0)
            return;

        Skipped++;
        _hasPending = false;
        _cooldown = Cfg.TicksBetweenActions;
        Services.Log.Warning(
            $"{ActionName} timed out on {_pending.Name} ({_pending.Container} slot {_pending.Slot}); moving on.");
    }

    private void Finish()
    {
        Completed++;
        GilTotal += _pending.StackValue;
        _hasPending = false;
        _cooldown = Cfg.TicksBetweenActions;
    }

    private void StartNext()
    {
        while (_queue.Count > 0)
        {
            var (next, attempts) = _queue.Dequeue();

            // Slots shift as stacks leave, and the player can move things mid-run, so the queued
            // snapshot is only a hint. SlotStillSellable re-checks both the item id and the
            // safety rails before we touch anything.
            if (!InventoryScanner.SlotStillSellable(next.Container, next.Slot, next.ItemId, Cfg, out var quantity))
            {
                Skipped++;
                continue;
            }

            var stack = next with { Quantity = quantity };

            if (!CanAct(stack, out var blocked))
            {
                Skipped++;
                Status = $"skipped {stack.Name} - {blocked}";
                continue;
            }

            if (!TryAct(stack))
            {
                // TryAct opens and closes a real context menu, so a failure is expensive.
                // Take the cooldown rather than burning through the rest of the queue this frame.
                Skipped++;
                _cooldown = Cfg.TicksBetweenActions;
                return;
            }

            _pending = stack;
            _pendingAttempts = attempts;
            _hasPending = true;
            _waitTicks = ActionTimeoutTicks;
            Status = $"{ActionName} {stack.Name} x{stack.Quantity} ({Remaining} left)";
            return;
        }

        Stop($"finished - {Completed} stacks, {GilTotal:N0} gil");
    }
}

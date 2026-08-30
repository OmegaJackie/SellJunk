using System;
using System.Collections.Generic;

namespace SellJunk.Game;

/// <summary>
/// Politely pauses YesAlready for the duration of a run.
///
/// YesAlready races us on the sell confirmation: it may answer a prompt before we see it, or
/// answer one we did not intend. It exposes a Dalamud data share of plugin names that want it
/// held; adding ours pauses it without touching the user's saved settings.
///
/// This deliberately does NOT use YesAlready's SetPluginEnabled / PausePlugin IPC - those
/// mutate the user's persisted config, so a crash mid-run would leave YesAlready switched off
/// permanently. A shared-set entry is recoverable; a rewritten config is not.
/// </summary>
internal sealed class YesAlreadySuppressor : IDisposable
{
    private const string DataShare = "YesAlready.StopRequests";

    private bool _suppressed;

    public void Suppress()
    {
        if (_suppressed)
            return;

        try
        {
            if (!Services.PluginInterface.TryGetData<HashSet<string>>(DataShare, out var stops))
                return; // YesAlready not installed - nothing to hold back.

            lock (stops)
                stops.Add(Services.PluginInterface.InternalName);

            _suppressed = true;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Could not pause YesAlready; continuing without it.");
        }
    }

    public void Restore()
    {
        if (!_suppressed)
            return;

        // Always clear this. The shared set outlives our plugin, so a missed removal leaves
        // the user's YesAlready silently paused forever.
        try
        {
            if (Services.PluginInterface.TryGetData<HashSet<string>>(DataShare, out var stops))
            {
                lock (stops)
                    stops.Remove(Services.PluginInterface.InternalName);
            }
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Could not resume YesAlready.");
        }
        finally
        {
            _suppressed = false;
        }
    }

    public void Dispose() => Restore();
}

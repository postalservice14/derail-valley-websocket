using System;
using System.Collections.Concurrent;
using UnityModManagerNet;

namespace DerailValleyWebSocket;

/// <summary>
/// WebSocket messages arrive on websocket-sharp's thread pool, but Unity objects (cars, controls, sim ports)
/// may only be touched from the main thread. Anything that reads or writes game state is posted here and
/// executed by UpdateDriver.Update.
/// </summary>
public static class MainThread
{
    private static UnityModManager.ModEntry.ModLogger Logger => Main.ModEntry.Logger;
    private static readonly ConcurrentQueue<Action> _queue = new();

    public static void Post(Action action)
    {
        _queue.Enqueue(action);
    }

    public static void Drain(int maxActions = 256)
    {
        // cap per frame so a flooding client can't stall the game
        while (maxActions-- > 0 && _queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Log($"Main thread action failed: {ex}");
            }
        }
    }
}

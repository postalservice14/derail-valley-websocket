using UnityEngine;
using System;
using UnityModManagerNet;

namespace DerailValleyWebSocket;

public class UpdateDriver : MonoBehaviour
{
    private static UnityModManager.ModEntry.ModLogger Logger => Main.ModEntry.Logger;
    private float _elapsed;

    void Start()
    {
        Logger.Log($"UpdateDriver started");
    }

    void Update()
    {
        // incoming messages and commands, every frame so controls feel immediate
        MainThread.Drain();

        // unscaled so emission keeps going while the game is paused
        _elapsed += Time.unscaledDeltaTime;
        if (_elapsed < Main.settings.Rate)
            return;

        _elapsed = 0f;  // reset timer

        try
        {
            Main.server.PublishState();
        }
        catch (Exception ex)
        {
            Logger.Log($"PublishState failed: {ex}");
        }

        if (!Main.server.HasLegacyClients || VarSystem.ActiveVars.Count == 0)
            return;

        try
        {
            var values = VarSystem.FetchAll();

            // TODO: allow to configure per var?
            if (Main.settings.EmitEachVar)
            {
                foreach (var kv in values)
                {
                    var (VarName, Unit) = kv.Key;

                    // Logger.Log($"Fetch varName={VarName} unit={Unit} value={kv.Value}");

                    Main.server.BroadcastVar(VarName, Unit, kv.Value);
                }
            }
            else
            {
                Main.server.BroadcastVars(values);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"UpdateDriver failed: {ex}");
        }
    }

    private void OnDisable()
    {
        Logger.Log($"UpdateDriver disabled");
    }

    private void OnDestroy()
    {
        Logger.Log($"UpdateDriver destroyed");
    }
}

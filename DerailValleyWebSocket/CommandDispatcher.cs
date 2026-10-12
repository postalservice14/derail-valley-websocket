using System;
using System.Collections.Generic;
using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using UnityEngine;
using UnityModManagerNet;

namespace DerailValleyWebSocket;

/// <summary>
/// Applies v2 commands to the player's loco through BaseControlsOverrider, the same path the game's own
/// remote control uses, so cab levers follow. Main thread only. Never throws: every failure becomes a
/// CommandResult with Ok = false.
/// </summary>
public static class CommandDispatcher
{
    private static UnityModManager.ModEntry.ModLogger Logger => Main.ModEntry.Logger;

    // horn presses per client, so a client that disconnects mid-press can't leave the horn stuck on
    private static readonly Dictionary<string, TrainCar> _hornHeldBy = new();

    // last non-off position of each headlight lever, restored on toggle
    private static readonly Dictionary<OverridableBaseControl, float> _lastHeadlightOn = new();

    public static CommandResultPayload Execute(string clientId, CommandPayload command)
    {
        try
        {
            var value = Apply(clientId, command);
            return new CommandResultPayload { Id = command.Id, Ok = true, Value = value };
        }
        catch (CommandException ex)
        {
            return new CommandResultPayload { Id = command.Id, Ok = false, Error = ex.Message };
        }
        catch (Exception ex)
        {
            Logger.Log($"Command failed unexpectedly: {ex}");
            return new CommandResultPayload { Id = command.Id, Ok = false, Error = $"Internal error: {ex.Message}" };
        }
    }

    public static void OnClientDisconnected(string clientId)
    {
        if (_hornHeldBy.TryGetValue(clientId, out var car))
        {
            _hornHeldBy.Remove(clientId);
            ReleaseHorn(car);
        }
    }

    private static float? Apply(string clientId, CommandPayload command)
    {
        if (command.Value != null && (float.IsNaN(command.Value.Value) || float.IsInfinity(command.Value.Value)))
            throw new CommandException("Value must be a finite number");

        var loco = LocoHelper.GetControlledLoco();
        if (loco == null)
            throw new CommandException("Not in a locomotive");

        var overrider = LocoHelper.GetOverrider(loco)!;
        var controlName = Normalize(command.Control);
        var action = Normalize(command.Action);

        if (controlName == "horn")
            return ApplyHorn(clientId, loco, overrider.Horn, action, command.Value);

        var control = ResolveControl(loco, overrider, controlName, command.Control);

        if (control.IsControlBlocked)
            throw new CommandException($"'{command.Control}' is blocked on this loco");

        switch (action)
        {
            case "set":
                if (command.Value == null)
                    throw new CommandException("'set' needs a value");
                control.Set(Snap(control, command.Value.Value));
                break;

            case "nudge":
                if (command.Value == null)
                    throw new CommandException("'nudge' needs a value (signed delta)");
                control.Set(Nudged(control, command.Value.Value));
                break;

            case "toggle":
                Toggle(control);
                break;

            case "press":
                control.Set(command.Value ?? 1f);
                break;

            case "release":
                control.Set(0f);
                break;

            default:
                throw new CommandException($"Unknown action '{command.Action}' (expected set, nudge, toggle, press, release)");
        }

        return control.Value;
    }

    private static OverridableBaseControl ResolveControl(TrainCar loco, BaseControlsOverrider overrider, string controlName, string rawName)
    {
        var isSteam = LocoHelper.IsSteam(loco);

        OverridableBaseControl? control = controlName switch
        {
            "throttle" => overrider.Throttle,
            "regulator" => isSteam ? overrider.Throttle : throw new CommandException("'regulator' is steam-only; use 'throttle'"),
            "trainbrake" => overrider.Brake,
            "independentbrake" => overrider.IndependentBrake,
            "dynamicbrake" => overrider.DynamicBrake,
            "reverser" => overrider.Reverser,
            "cutoff" => isSteam ? overrider.Reverser : throw new CommandException("'cutoff' is steam-only; use 'reverser'"),
            "sander" => overrider.Sander,
            "headlights" or "headlightsfront" => overrider.HeadlightsFront,
            "headlightsrear" => overrider.HeadlightsRear,
            "cablight" => overrider.CabLight,
            _ => throw new CommandException($"Unknown control '{rawName}'"),
        };

        if (control == null)
            throw new CommandException($"This loco has no '{rawName}' control");

        return control;
    }

    private static float? ApplyHorn(string clientId, TrainCar loco, HornControl? horn, string action, float? value)
    {
        if (horn == null)
            throw new CommandException("This loco has no horn");

        // HornControl.Set maps 1 to full horn and 0 to neutral for both lever styles
        switch (action)
        {
            case "press":
                // a press on a different loco than the one already held releases the old one first
                if (_hornHeldBy.TryGetValue(clientId, out var previous) && previous != loco)
                    ReleaseHorn(previous);
                _hornHeldBy[clientId] = loco;
                horn.Set(Mathf.Clamp01(value ?? 1f));
                break;

            case "release":
                _hornHeldBy.Remove(clientId);
                horn.Set(0f);
                break;

            case "set":
                if (value == null)
                    throw new CommandException("'set' needs a value");
                horn.Set(Mathf.Clamp01(value.Value));
                break;

            default:
                throw new CommandException($"Horn supports press, release, set (got '{action}')");
        }

        return horn.Value;
    }

    private static void ReleaseHorn(TrainCar car)
    {
        try
        {
            if (car == null)
                return;

            var horn = LocoHelper.GetOverrider(car)?.Horn;
            if (horn != null)
                horn.Set(0f);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to release horn: {ex}");
        }
    }

    private static void Toggle(OverridableBaseControl control)
    {
        if (control is HeadlightsControlBase)
        {
            var isOff = Mathf.Abs(control.Value - HeadlightsControlBase.NEUTRAL_VALUE) < 0.01f;
            if (isOff)
            {
                control.Set(_lastHeadlightOn.TryGetValue(control, out var on) ? on : 1f);
            }
            else
            {
                _lastHeadlightOn[control] = control.Value;
                control.Set(HeadlightsControlBase.NEUTRAL_VALUE);
            }
            return;
        }

        control.Set(control.Value > 0.5f ? 0f : 1f);
    }

    /// <summary>
    /// NotchCount is the number of positions, so notched levers rest on k / (NotchCount - 1), matching the
    /// game's own Mathf.RoundToInt(value * (notches - 1)). Snap so we never leave one between notches.
    /// </summary>
    private static float Snap(OverridableBaseControl control, float value)
    {
        value = Mathf.Clamp01(value);

        var intervals = NotchIntervals(control);
        if (intervals == 0f)
            return value;

        return Mathf.Round(value * intervals) / intervals;
    }

    private static float NotchIntervals(OverridableBaseControl control)
    {
        return control.IsNotched && control.NotchCount >= 2f ? control.NotchCount - 1f : 0f;
    }

    private static float Nudged(OverridableBaseControl control, float delta)
    {
        // Mathf.Sign(0) is 1, which would move a notched lever
        if (delta == 0f)
            return control.Value;

        var intervals = NotchIntervals(control);
        if (intervals == 0f)
            return Mathf.Clamp01(control.Value + delta);

        // a dial tick worth less than one notch must still move the lever one notch, or it would snap back
        var notches = Mathf.Max(1f, Mathf.Round(Mathf.Abs(delta) * intervals)) * Mathf.Sign(delta);
        return Snap(control, Snap(control, control.Value) + notches / intervals);
    }

    private static string Normalize(string? name)
    {
        return (name ?? "").Replace("_", "").Replace("-", "").ToLowerInvariant();
    }

    private class CommandException : Exception
    {
        public CommandException(string message) : base(message) { }
    }
}

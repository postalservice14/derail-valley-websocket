using System.Collections.Generic;

namespace DerailValleyWebSocket;

// Protocol v2 payloads. Serialized camelCase; see PROTOCOL.md.
// Every nullable field means "not available on this loco / right now", never an error.

public class HelloPayload : Payload
{
    public string? Client;
    public int Protocol;
}

public class StatePayload : Payload
{
    public int Protocol = StateReader.PROTOCOL_VERSION;
    public string? CarName;
    public LocoInfo? Loco;
    public List<JobInfo>? Jobs;
}

public class LocoInfo
{
    public string Id = "";
    public string LiveryId = "";
    /// <summary>"steam" or "diesel" (anything with cab controls that isn't steam, including electric).</summary>
    public string Kind = "";
    public float SpeedKph;
    public float? SpeedometerKph;
    public float? Wheelslip;
    public bool IsWheelslipping;
    public ControlsInfo Controls = new();
    public BrakesInfo? Brakes;
    public SteamInfo? Steam;
    public ResourcesInfo? Resources;
    public EngineInfo? Engine;
    public LampsInfo? Lamps;
}

public class ControlInfo
{
    /// <summary>Lever position 0..1, exactly what Set() takes (reverser/cutoff 0.5 = neutral).</summary>
    public float Value;
    /// <summary>Number of notches when the lever is notched, else null.</summary>
    public int? Notches;
    public bool Blocked;
}

public class ControlsInfo
{
    public ControlInfo? Throttle;
    public ControlInfo? TrainBrake;
    public ControlInfo? IndependentBrake;
    public ControlInfo? DynamicBrake;
    public ControlInfo? Reverser;
    public ControlInfo? Horn;
    public ControlInfo? Sander;
    public ControlInfo? HeadlightsFront;
    public ControlInfo? HeadlightsRear;
    public ControlInfo? CabLight;
}

/// <summary>Gauge pressure in bar, as shown on the cab gauges (absolute - 1).</summary>
public class BrakesInfo
{
    public float BrakePipe;
    public float MainReservoir;
    public float BrakeCylinder;
}

public class SteamInfo
{
    /// <summary>Gauge pressure in bar.</summary>
    public float? BoilerPressure;
    /// <summary>0..1 normalized water level in the glass.</summary>
    public float? BoilerWaterLevel;
    public float? SafetyValve;
    /// <summary>Regulator opening 0..1 (same lever as controls.throttle).</summary>
    public float? Regulator;
    /// <summary>Signed cutoff -1..1, negative = reverse (derived from controls.reverser).</summary>
    public float? Cutoff;
}

public class ResourcesInfo
{
    public float? Fuel;
    public float? FuelMax;
    public float? Sand;
    public float? SandMax;
    public float? Oil;
    public float? OilMax;
}

public class EngineInfo
{
    public bool? On;
    public float? Rpm;
    public float? RpmMax;
    public float? TractionMotorAmps;
    public float? TractionMotorAmpsMax;
    public float? Temperature;
}

/// <summary>Cab lamp states from the standard sim ports (0 = off, higher = on/blinking per loco).</summary>
public class LampsInfo
{
    public float? Fuel;
    public float? Oil;
    public float? Sand;
    public float? Sander;
    public float? HeadlightFront;
    public float? HeadlightRear;
    public float? CabLight;
}

public class JobInfo
{
    public string Id = "";
    public string Type = "";
    public string State = "";
    public string? Origin;
    public string? Destination;
    public float Wage;
    /// <summary>Seconds; 0 = untimed.</summary>
    public float TimeLimit;
    public float TimeOnJob;
}

public class CommandPayload : Payload
{
    /// <summary>Echoed back in the CommandResult so the client can correlate.</summary>
    public string? Id;
    public string Control = "";
    /// <summary>set | nudge | toggle | press | release</summary>
    public string Action = "";
    public float? Value;
}

public class CommandResultPayload : Payload
{
    public string? Id;
    public bool Ok;
    public string? Error;
    /// <summary>Lever position after the command, when there is one.</summary>
    public float? Value;
}

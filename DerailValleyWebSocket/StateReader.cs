using System;
using System.Collections.Generic;
using System.Reflection;
using DV.Customization;
using DV.HUD;
using DV.Logic.Job;
using DV.Simulation.Controllers;
using DV.Utils;
using UnityEngine;
using UnityModManagerNet;

namespace DerailValleyWebSocket;

/// <summary>
/// Builds the v2 state snapshot. Main thread only. Each section is read independently and falls back to
/// null on failure, so one bad reading (e.g. a loco without some port) can't blank the whole snapshot.
/// </summary>
public static class StateReader
{
    public const int PROTOCOL_VERSION = 2;

    private static UnityModManager.ModEntry.ModLogger Logger => Main.ModEntry.Logger;
    private static readonly HashSet<string> _loggedFailures = new();

    // jobs change rarely, and reading them goes through reflection, so refresh at 1 Hz
    private const float JOBS_REFRESH_SECONDS = 1f;
    private static float _jobsReadAt = float.NegativeInfinity;
    private static List<JobInfo>? _jobs;
    private static readonly FieldInfo? _jobsManagerInstanceField =
        typeof(SingletonBehaviour<JobsManager>).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);

    public static StatePayload Read()
    {
        var loco = LocoHelper.GetControlledLoco();

        return new StatePayload
        {
            CarName = Safe("carName", CarHelper.GetCurrentCarName),
            Loco = loco == null ? null : Safe("loco", () => ReadLoco(loco)),
            Jobs = Safe("jobs", ReadJobsThrottled),
        };
    }

    private static LocoInfo ReadLoco(TrainCar car)
    {
        var overrider = LocoHelper.GetOverrider(car)!;
        var isSteam = LocoHelper.IsSteam(car);
        var wheelslip = car.SimController?.wheelslipController;

        var info = new LocoInfo
        {
            Id = car.ID,
            LiveryId = car.carLivery.id,
            Kind = isSteam ? "steam" : "diesel",
            SpeedKph = Round(car.GetAbsSpeed() * 3.6f),
            SpeedometerKph = Safe("speedometer", () => Round(CarHelper.GetCarSpeedometerValueKph())),
            Wheelslip = wheelslip == null ? null : Round(wheelslip.wheelslip),
            IsWheelslipping = wheelslip != null && wheelslip.IsWheelslipping,
            Controls = new ControlsInfo
            {
                Throttle = ReadControl(overrider.Throttle),
                TrainBrake = ReadControl(overrider.Brake),
                IndependentBrake = ReadControl(overrider.IndependentBrake),
                DynamicBrake = ReadControl(overrider.DynamicBrake),
                Reverser = ReadControl(overrider.Reverser),
                Horn = ReadControl(overrider.Horn),
                Sander = ReadControl(overrider.Sander),
                HeadlightsFront = ReadControl(overrider.HeadlightsFront),
                HeadlightsRear = ReadControl(overrider.HeadlightsRear),
                CabLight = ReadControl(overrider.CabLight),
            },
            Brakes = Safe("brakes", () => ReadBrakes(car)),
            Steam = isSteam ? Safe("steam", () => ReadSteam(car, overrider)) : null,
            Resources = Safe("resources", () => ReadResources(car)),
            Engine = Safe("engine", () => ReadEngine(car)),
            Lamps = Safe("lamps", () => ReadLamps(car)),
        };

        return info;
    }

    private static ControlInfo? ReadControl(OverridableBaseControl? control)
    {
        if (control == null)
            return null;

        return new ControlInfo
        {
            Value = Round(control.Value),
            Notches = control.IsNotched ? (int)control.NotchCount : null,
            Blocked = control.IsControlBlocked,
        };
    }

    private static BrakesInfo? ReadBrakes(TrainCar car)
    {
        var brakes = car.brakeSystem;
        if (brakes == null)
            return null;

        return new BrakesInfo
        {
            BrakePipe = Round(brakes.brakePipePressure - 1f),
            MainReservoir = Round(brakes.mainReservoirPressure - 1f),
            BrakeCylinder = Round(brakes.brakeCylinderPressure - 1f),
        };
    }

    private static SteamInfo ReadSteam(TrainCar car, DV.Simulation.Cars.BaseControlsOverrider overrider)
    {
        var boiler = LocoHelper.GetBoiler(car);
        var reverser = overrider.Reverser;
        var throttle = overrider.Throttle;

        return new SteamInfo
        {
            BoilerPressure = boiler == null ? null : Round(boiler.pressureReadOut.Value - 1f),
            BoilerWaterLevel = boiler == null ? null : Round(boiler.waterLevelReadOut.Value),
            SafetyValve = boiler == null ? null : Round(boiler.safetyValveReadOut.Value),
            Regulator = throttle == null ? null : Round(throttle.Value),
            Cutoff = reverser == null ? null : Round((reverser.Value - 0.5f) * 2f),
        };
    }

    private static ResourcesInfo ReadResources(TrainCar car)
    {
        return new ResourcesInfo
        {
            Fuel = Port(car, STDSimPort.Fuel),
            FuelMax = Port(car, STDSimPort.FuelMax),
            Sand = Port(car, STDSimPort.Sand),
            SandMax = Port(car, STDSimPort.SandMax),
            Oil = Port(car, STDSimPort.Oil),
            OilMax = Port(car, STDSimPort.OilMax),
        };
    }

    private static EngineInfo ReadEngine(TrainCar car)
    {
        var on = Port(car, STDSimPort.EngineOn);

        return new EngineInfo
        {
            On = on == null ? null : on > 0.5f,
            Rpm = Port(car, STDSimPort.EngineRPM),
            RpmMax = Port(car, STDSimPort.EngineRPMMax),
            TractionMotorAmps = Port(car, STDSimPort.TractionMotorAmps),
            TractionMotorAmpsMax = Port(car, STDSimPort.TractionMotorAmpsMax),
            Temperature = Port(car, STDSimPort.Temperature),
        };
    }

    private static LampsInfo ReadLamps(TrainCar car)
    {
        return new LampsInfo
        {
            Fuel = Port(car, STDSimPort.FuelLampState),
            Oil = Port(car, STDSimPort.OilLampState),
            Sand = Port(car, STDSimPort.SandLampState),
            Sander = Port(car, STDSimPort.SanderLampState),
            HeadlightFront = Port(car, STDSimPort.HeadlightFLampState),
            HeadlightRear = Port(car, STDSimPort.HeadlightRLampState),
            CabLight = Port(car, STDSimPort.CabLightLampState),
        };
    }

    private static List<JobInfo>? ReadJobsThrottled()
    {
        if (Time.unscaledTime - _jobsReadAt < JOBS_REFRESH_SECONDS)
            return _jobs;

        _jobsReadAt = Time.unscaledTime;
        _jobs = ReadJobs();
        return _jobs;
    }

    private static List<JobInfo>? ReadJobs()
    {
        // JobsManager.Instance auto-creates the singleton when missing (e.g. main menu), so peek at the
        // backing field instead
        var manager = _jobsManagerInstanceField?.GetValue(null) as JobsManager;
        if (manager == null)
            return null;

        var jobs = new List<JobInfo>();

        foreach (var job in manager.currentJobs)
        {
            if (job == null)
                continue;

            jobs.Add(new JobInfo
            {
                Id = job.ID,
                Type = job.jobType.ToString(),
                State = job.State.ToString(),
                Origin = job.chainData?.chainOriginYardId,
                Destination = job.chainData?.chainDestinationYardId,
                Wage = Round(job.GetWageForTheJob()),
                TimeLimit = Round(job.TimeLimit),
                TimeOnJob = Round(job.GetTimeOnJob()),
            });
        }

        return jobs;
    }

    private static float? Port(TrainCar car, STDSimPort port)
    {
        var value = LocoHelper.ReadStandardPort(car, port);
        return value == null ? null : Round(value.Value);
    }

    private static float Round(float value)
    {
        return (float)Math.Round(value, 3);
    }

    private static float? Round(float? value)
    {
        return value == null ? null : Round(value.Value);
    }

    private static T? Safe<T>(string section, Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            LogOnce(section, ex);
            return null;
        }
    }

    private static float? Safe(string section, Func<float?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            LogOnce(section, ex);
            return null;
        }
    }

    // state is read at 20 Hz; log each failing section once instead of flooding the UMM log
    private static void LogOnce(string section, Exception ex)
    {
        if (_loggedFailures.Add(section))
            Logger.Log($"Failed to read state section '{section}' (further failures not logged): {ex}");
    }
}

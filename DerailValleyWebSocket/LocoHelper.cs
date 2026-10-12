using System.Linq;
using DV.Customization;
using DV.Simulation.Cars;
using DV.ThingTypes;
using LocoSim.Implementations;

namespace DerailValleyWebSocket;

/// <summary>
/// Resolves the loco the player is driving. All methods return null instead of throwing when the player
/// is on foot, in a wagon, in the main menu, or mid-load. Main thread only.
/// </summary>
public static class LocoHelper
{
    private static TrainCar? _boilerCar;
    private static Boiler? _boiler;
    private static TrainCar? _customizationCar;
    private static TrainCarCustomization? _customization;

    /// <summary>The car the player is in if it has cab controls; for a tender, the steam loco it's coupled to.</summary>
    public static TrainCar? GetControlledLoco()
    {
        var car = PlayerManager.Car;

        // Unity's overloaded == catches destroyed cars, which ?. would not
        if (car == null || car.carLivery == null)
            return null;

        if (GetOverrider(car) != null)
            return car;

        if (CarTypes.IsTender(car.carLivery) && car.couplers != null)
        {
            foreach (var coupler in car.couplers)
            {
                if (coupler == null || coupler.coupledTo == null)
                    continue;

                var other = coupler.coupledTo.train;
                if (other != null && other.carLivery != null && CarTypes.IsSteamLocomotive(other.carLivery) && GetOverrider(other) != null)
                    return other;
            }
        }

        return null;
    }

    public static BaseControlsOverrider? GetOverrider(TrainCar car)
    {
        var sim = car.SimController;
        if (sim == null)
            return null;

        var overrider = sim.controlsOverrider;
        return overrider == null ? null : overrider;
    }

    public static bool IsSteam(TrainCar car)
    {
        return car.carLivery != null && CarTypes.IsSteamLocomotive(car.carLivery);
    }

    public static Boiler? GetBoiler(TrainCar car)
    {
        if (_boilerCar == car)
            return _boiler;

        _boilerCar = car;
        _boiler = car.SimController?.SimulationFlow?.OrderedSimComps.OfType<Boiler>().FirstOrDefault();
        return _boiler;
    }

    public static float? ReadStandardPort(TrainCar car, STDSimPort port)
    {
        // ~20 ports at 20 Hz; don't GetComponent for each one
        if (_customizationCar != car)
        {
            _customizationCar = car;
            _customization = car.GetComponent<TrainCarCustomization>();
        }

        var customComp = _customization;

        if (customComp == null || !customComp.HasPort(port))
            return null;

        return customComp.ReadPort(port);
    }
}

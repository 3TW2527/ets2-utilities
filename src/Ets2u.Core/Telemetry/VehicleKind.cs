using Ets2u.Core.Extract;

namespace Ets2u.Core.Telemetry;

/// <summary>
/// Tells a coach from a truck by its id: coaches are defined under def/vehicle/bus/&lt;brand.model&gt;/
/// (vehicle.man.lions_24 is def/vehicle/bus/man.lions_24/data.sii), so any coach the game adds
/// later is found too.
/// </summary>
public sealed class VehicleKind(Func<IGameFiles?> game)
{
    readonly Dictionary<string, bool> _known = new();

    public bool IsCoach(string truckId)
    {
        if (!truckId.StartsWith("vehicle.", StringComparison.Ordinal)) return false;
        if (_known.TryGetValue(truckId, out var coach)) return coach;
        try
        {
            coach = game()?.TryRead($"def/vehicle/bus/{truckId["vehicle.".Length..]}/data.sii") is not null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            return false; // asked again next time
        }
        _known[truckId] = coach;
        return coach;
    }
}

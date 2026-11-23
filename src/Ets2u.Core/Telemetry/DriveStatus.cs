namespace Ets2u.Core.Telemetry;

/// <summary>
/// The traffic light's steady colour: how the driving has gone over the last <see cref="WindowMinutes"/>
/// of the game's clock, counted back from now. Red while a red event happened in that time, yellow while
/// a yellow one did, green otherwise. It rolls on with the clock, not with events: an event lights its
/// colour from the moment it happens until it has been over for the whole window, then drops off on its
/// own, so the light eases back to green as the clock runs on, moving or standing.
///
/// The clock is the game's (the plugin's game.time, in minutes), so the window is in game time: 60 game
/// minutes are about 3 real minutes on roads. A paused or closed game doesn't age it; sleeping does, and a
/// clock that goes back (another profile or save loaded) starts the window afresh.
/// Fed by the telemetry reader thread only.
/// </summary>
public sealed class DriveStatus
{
    /// <summary>Events in the window, and the game minute each was last seen going on.</summary>
    readonly List<(DrivingEvent Event, uint Last)> _events = new();
    uint? _clock;

    /// <summary>The window, in game minutes (30, 60 or 120 in the app).</summary>
    public int WindowMinutes { get; set; } = 60;

    /// <summary>The game minute of the latest step, or null before the game has sent one.</summary>
    public uint? Clock => _clock;

    /// <summary>The steady colour: the worst event within the window; null is green.</summary>
    public AlertLevel? Level => AlertLight.For(_events.Select(x => x.Event.Level));

    /// <summary>One reader step: the game's clock (null while it isn't known) and the events that started or turned red.</summary>
    public void Step(uint? gameMinute, IEnumerable<DrivingEvent> news)
    {
        if (gameMinute is { } now)
        {
            if (_clock is { } before && now < before) _events.Clear();
            _clock = now;
        }
        foreach (var e in news)
            if (!_events.Exists(x => ReferenceEquals(x.Event, e)) && (_clock ?? e.GameStarted) is { } at)
                _events.Add((e, at));
        if (_clock is not { } clock) return;
        for (var i = _events.Count - 1; i >= 0; i--)
        {
            var (e, last) = _events[i];
            // Still going on: it counts from now.
            if (e.Ended is null) _events[i] = (e, last = clock);
            if (clock - last >= WindowMinutes) _events.RemoveAt(i);
        }
    }

    public void Clear()
    {
        _events.Clear();
        _clock = null;
    }
}

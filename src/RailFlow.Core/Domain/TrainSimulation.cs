using System.Collections.Immutable;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

/// <summary>
/// The train's progress along a planned route, shown on the Timetable tab. Uses the same running times and
/// station stops as <see cref="RecursiveTimetable"/>, so the simulated arrival matches the timetable.
/// </summary>
public static class TrainSimulation
{
    /// <summary>
    /// Follows the train along the route in fixed time steps with IterateStepsUntil (an indefinite loop over
    /// immutable state records). Each step asks the pure function <see cref="PositionAt"/> where the train is,
    /// so the simulation uses the same running times and station stops as the timetable and arrives at the same time.
    /// </summary>
    public static ImmutableList<TransitSimulationState> RunTransitSimulation(PlannedRoute route, int stepMinutes = 20)
    {
        var journeyMinutes = RecursiveTimetable.JourneyMinutes(route);
        var initial = new TransitSimulationState(
            StepIndex: 0,
            CurrentStation: route.Origin,
            ProgressKm: 0.0,
            TotalDistanceKm: route.TotalDistanceKm,
            AccumulatedMinutes: 0,
            LastEventDescription: $"Departed {StationNames.Display(route.Origin)} on {route.Segments.FirstOrDefault()?.LineName ?? "-"}",
            ReachedDestination: route.Segments.Count == 0);

        return initial.IterateStepsUntil(
            updateState: state =>
            {
                var minutes = Math.Min(journeyMinutes, state.AccumulatedMinutes + stepMinutes);
                var (km, lastStation, description) = PositionAt(route, minutes);
                return state with
                {
                    StepIndex = state.StepIndex + 1,
                    CurrentStation = lastStation,
                    ProgressKm = km,
                    AccumulatedMinutes = minutes,
                    LastEventDescription = description,
                    ReachedDestination = minutes >= journeyMinutes
                };
            },
            // The step limit only guards against a route with zero running time.
            endCondition: s => s.ReachedDestination || s.StepIndex >= 500
        ).ToImmutableList();
    }

    /// <summary>
    /// Where the train is a given number of minutes after departure: distance covered, the last station passed
    /// and what it is doing. Sections are run at their own average speed; every intermediate station has a
    /// stop of <see cref="RecursiveTimetable.DwellMinutes"/>.
    /// </summary>
    public static (double Km, string LastStation, string Description) PositionAt(PlannedRoute route, int minutesAfterDeparture)
    {
        var elapsed = 0;
        var km = 0.0;
        var lastStation = route.Origin;

        for (var i = 0; i < route.Segments.Count; i++)
        {
            var segment = route.Segments[i];
            if (minutesAfterDeparture < elapsed + segment.BaseMinutes)
            {
                var share = (double)(minutesAfterDeparture - elapsed) / Math.Max(1, segment.BaseMinutes);
                var position = Math.Round(km + segment.DistanceKm * share, 1);
                return (position, lastStation, $"On {segment.LineName} towards {StationNames.Display(segment.ToStation)}");
            }

            elapsed += segment.BaseMinutes;
            km += segment.DistanceKm;
            lastStation = segment.ToStation;

            var isLast = i == route.Segments.Count - 1;
            if (isLast)
                break;

            if (minutesAfterDeparture < elapsed + RecursiveTimetable.DwellMinutes)
                return (Math.Round(km, 1), lastStation, $"Stopped at {StationNames.Display(lastStation)}");

            elapsed += RecursiveTimetable.DwellMinutes;
        }

        return (Math.Round(km, 1), route.Destination, $"Arrived at {StationNames.Display(route.Destination)}");
    }
}

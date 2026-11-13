using System.Collections.Immutable;
using System.Diagnostics;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

public sealed record BenchmarkEntry(
    string ApproachName,
    string Category,
    ComputedResult ComputedValue,
    double ElapsedMicroseconds,
    double RelativeRatio,
    string ArchitecturalNote);

public readonly record struct ComputedResult(int Value);

/// <summary>
/// Supporting code for the Timetable tab (train simulation) and the Lab tab (CSV check, benchmark):
/// 1. A CSV parser built from curried, partially applied functions.
/// 2. Loggers with the service name and level fixed by partial application.
/// 3. A step-by-step train simulation run with an indefinite loop (IterateStepsUntil).
/// 4. A small Stopwatch benchmark comparing loop styles on the same input.
/// </summary>
public static class BenchmarkAndLoopLab
{
    // --- 1. Curried & Partially Applied CSV Parser ---
    public static readonly Func<string, string, bool, string, ImmutableList<ImmutableList<string>>> RawCsvParser =
        (lineSplitChar, fieldSplitChar, trimWhitespace, inputCsv) =>
            inputCsv
                .Split(lineSplitChar, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line
                    .Split(fieldSplitChar)
                    .Select(cell => trimWhitespace ? cell.Trim() : cell)
                    .ToImmutableList())
                .ToImmutableList();

    /// <summary>
    /// Pre-configured CSV parser created via Partial Application.
    /// Fixes line separator ("\n"), field separator (","), and whitespace trimming (true).
    /// </summary>
    public static Func<string, ImmutableList<ImmutableList<string>>> StandardCsvParser { get; } =
        RawCsvParser.Partial("\n", ",", true);

    /// <summary>
    /// Curried CSV parser: string -> string -> bool -> string -> ImmutableList
    /// </summary>
    public static Func<string, Func<string, Func<bool, Func<string, ImmutableList<ImmutableList<string>>>>>> CurriedCsvParser { get; } =
        RawCsvParser.Curry();

    // --- 2. Partially Applied Level-Specific Loggers ---
    public static Func<string, string, string, string> FormatLogEntry =>
        (serviceName, severityLevel, message) =>
            $"[{DateTime.UtcNow:HH:mm:ss}] [{serviceName}] [{severityLevel}] {message}";

    public static Func<string, string> CreateServiceLogger(string serviceName, string level) =>
        FormatLogEntry.Partial(serviceName, level);

    // --- 3. Indefinite Loops & Trampolining ---

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

    // --- 4. Live Performance Benchmark Suite ---
    // Compares several loop styles (imperative, LINQ, Fork combinator, trampoline loop, custom iterator) on the same elevation puzzle:
    // '^' = +1 elevation grade, 'v' = -1 elevation grade; every approach returns the final elevation.
    // Timings come from a plain Stopwatch (see Measure), so they are indicative, not a rigorous benchmark.
    public static ImmutableList<BenchmarkEntry> RunElevationTelemetryBenchmark(int signalLength = 25_000)
    {
        // Generate deterministic signal string of '^' and 'v'
        var chars = new char[signalLength];
        for (int i = 0; i < signalLength; i++)
        {
            chars[i] = (i % 3 == 0) ? 'v' : '^';
        }
        var input = new string(chars);

        var results = new List<(string Name, string Category, int Value, double Microseconds, string Note)>();

        // 1. Imperative foreach loop
        var (val1, us1) = Measure(() =>
        {
            int floor = 0;
            foreach (var c in input)
            {
                if (c == '^') floor++;
                else if (c == 'v') floor--;
            }
            return floor;
        });
        results.Add(("1. Imperative foreach", "Aggregation", val1, us1,
            "Plain loop with a mutable counter; no delegates."));

        // 2. LINQ Sum with inline selector
        var (val2, us2) = Measure(() =>
            input.Sum(x => x == '^' ? 1 : x == 'v' ? -1 : 0));
        results.Add(("2. LINQ .Sum(selector)", "Aggregation", val2, us2,
            "One pass; the lambda is called once per character."));

        // 3. LINQ .Select().Sum()
        var (val3, us3) = Measure(() =>
            input.Select(x => x == '^' ? 1 : x == 'v' ? -1 : 0).Sum());
        results.Add(("3. LINQ .Select().Sum()", "Aggregation", val3, us3,
            "Two chained iterators, so one extra call per character."));

        // 4. Higher-Order Fork Combinator
        var (val4, us4) = Measure(() =>
            input.Fork(
                s => s.Count(c => c == '^'),
                s => s.Count(c => c == 'v'),
                (up, down) => up - down));
        results.Add(("4. Fork Combinator (Up - Down)", "Combinator", val4, us4,
            "Counts ups and downs separately, so it reads the input twice."));

        // 5. IterateUntil over an immutable (Floor, Index) state
        var (val5, us5) = Measure(() =>
        {
            var finalState = (Floor: 0, Index: 0).IterateUntil(
                s => (
                    Floor: s.Floor + (input[s.Index] == '^' ? 1 : -1),
                    Index: s.Index + 1
                ),
                s => s.Index >= input.Length);
            return finalState.Floor;
        });
        results.Add(("5. IterateUntil (immutable state)", "Indefinite loop", val5, us5,
            "Each step returns a new (Floor, Index) value; the loop itself never grows the stack."));

        // 6. Custom IEnumerable<T> / IEnumerator<T>
        var (val6, us6) = Measure(() =>
        {
            var enumerable = new TransitStepEnumerable<(int Floor, int Index)>(
                (0, 0),
                s => (s.Floor + (input[s.Index] == '^' ? 1 : -1), s.Index + 1),
                s => s.Index >= input.Length);
            return enumerable.Last().Floor;
        });
        results.Add(("6. Custom IEnumerable<T> / IEnumerator<T>", "Indefinite loop", val6, us6,
            "Lazy, pull-based: each state is produced only when LINQ asks for it."));

        var minUs = Math.Max(0.1, results.Min(r => r.Microseconds));

        return results.Select(r => new BenchmarkEntry(
            r.Name,
            r.Category,
            new ComputedResult(r.Value),
            Math.Round(r.Microseconds, 1),
            Math.Round(r.Microseconds / minUs, 2),
            r.Note
        )).ToImmutableList();
    }

    private static (int Result, double ElapsedMicroseconds) Measure(Func<int> action, int iterations = 15)
    {
        // Warmup JIT
        int last = action();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            last = action();
        }
        sw.Stop();
        var avgMicroseconds = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;
        return (last, avgMicroseconds);
    }
}

using System.Collections.Immutable;
using System.Diagnostics;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

public sealed record BenchmarkEntry(
    string ApproachName,
    string Category,
    intComputedResult ComputedValue,
    double ElapsedMicroseconds,
    double RelativeRatio,
    string ArchitecturalNote);

public readonly record struct intComputedResult(int Value);

/// <summary>
/// Implements the Lecture L7_1 & L9_1 Practical Functional C# Labs:
/// 1. Curried & Partially Applied CSV Tariff / Schedule Parser (L7_1).
/// 2. Level-Specific Loggers via Partial Application (L7_1).
/// 3. Indefinite Loop Simulation via Record Trampoline, ValueTuple Trampoline, and Custom IEnumerable (L7_1 & L9_1).
/// 4. Live Micro-Benchmark Engine comparing Imperative vs LINQ vs Transduce vs Trampoline (L9_1).
/// </summary>
public static class BenchmarkAndLoopLab
{
    // --- 1. Curried & Partially Applied CSV Parser (Lecture L7_1) ---
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
    /// Pre-configured CSV parser created via Partial Application (Lecture L7_1).
    /// Fixes line separator ("\n"), field separator (","), and whitespace trimming (true).
    /// </summary>
    public static Func<string, ImmutableList<ImmutableList<string>>> StandardCsvParser { get; } =
        RawCsvParser.Partial("\n", ",", true);

    /// <summary>
    /// Curried CSV parser (Lecture L7_1): string -> string -> bool -> string -> ImmutableList
    /// </summary>
    public static Func<string, Func<string, Func<bool, Func<string, ImmutableList<ImmutableList<string>>>>>> CurriedCsvParser { get; } =
        RawCsvParser.Curry();

    // --- 2. Partially Applied Level-Specific Loggers (Lecture L7_1) ---
    public static Func<string, string, string, string> FormatLogEntry =>
        (serviceName, severityLevel, message) =>
            $"[{DateTime.UtcNow:HH:mm:ss}] [{serviceName}] [{severityLevel}] {message}";

    public static Func<string, string> CreateServiceLogger(string serviceName, string level) =>
        FormatLogEntry.Partial(serviceName, level);

    // --- 3. Indefinite Loops & Trampolining (Lectures L7_1 & L9_1) ---

    /// <summary>
    /// Runs a deterministic Snakes & Ladders / Transit Signal Simulation using IterateStepsUntil (Record Trampoline).
    /// Models signal boosters (ladders / express green waves) and track delays (snakes / caution zones).
    /// </summary>
    public static ImmutableList<TransitSimulationState> RunTransitSimulation(
        PlannedRoute route,
        double cruiseSpeedKmPerStep = 45.0)
    {
        var initial = new TransitSimulationState(
            StepIndex: 0,
            CurrentStation: route.Origin,
            ProgressKm: 0.0,
            TotalDistanceKm: route.TotalDistanceKm,
            AccumulatedMinutes: 0,
            LastEventDescription: $"Departed {route.Origin} on {route.Segments.FirstOrDefault()?.LineName ?? "Mainline"}",
            ReachedDestination: route.TotalDistanceKm <= 0);

        return initial.IterateStepsUntil(
            updateState: state =>
            {
                var step = state.StepIndex + 1;
                // Deterministic rail telemetry anomaly (inspired by Snakes & Ladders in L7_1)
                var (deltaKm, deltaMin, eventNote) = (step % 5, step % 3) switch
                {
                    (0, _) => (cruiseSpeedKmPerStep * 0.55, 22, "Caution Signal (Track Maintenance Speed Restriction)"),
                    (_, 0) => (cruiseSpeedKmPerStep * 1.25, 14, "High-Speed Green Wave Boost (Express Corridor)"),
                    _ => (cruiseSpeedKmPerStep, 18, "Nominal Cruise Telemetry")
                };

                var nextKm = Math.Min(state.TotalDistanceKm, Math.Round(state.ProgressKm + deltaKm, 1));
                var reached = nextKm >= state.TotalDistanceKm;

                // Resolve nearest station along segments
                double cumulative = 0;
                string currentStation = route.Origin;
                foreach (var seg in route.Segments)
                {
                    cumulative += seg.DistanceKm;
                    if (nextKm >= cumulative - 1e-3)
                        currentStation = seg.ToStation;
                }

                return state with
                {
                    StepIndex = step,
                    CurrentStation = reached ? route.Destination : currentStation,
                    ProgressKm = nextKm,
                    AccumulatedMinutes = state.AccumulatedMinutes + deltaMin,
                    LastEventDescription = reached
                        ? $"Arrived at {route.Destination} ({eventNote})"
                        : $"{eventNote} (+{deltaKm:F1} km)",
                    ReachedDestination = reached
                };
            },
            endCondition: s => s.ReachedDestination || s.StepIndex >= 50
        ).ToImmutableList();
    }

    // --- 4. Lecture L9_1 Live Performance Benchmark Suite ---
    // Compares the exact paradigms from Lecture L9_1 (Advent of Code Elevator / Elevation Telemetry Puzzle):
    // '^' = +1 elevation grade, 'v' = -1 elevation grade.
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

        // 1. Imperative foreach loop (L9_1 Baseline)
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
        results.Add(("1. Imperative foreach", "Aggregation (Part 1)", val1, us1,
            "Zero heap allocations; direct char iteration (L9_1 fastest baseline)."));

        // 2. LINQ Sum with inline selector (L9_1 Functional Winner)
        var (val2, us2) = Measure(() =>
            input.Sum(x => x == '^' ? 1 : x == 'v' ? -1 : 0));
        results.Add(("2. LINQ .Sum(selector)", "Aggregation (Part 1)", val2, us2,
            "Single-pass declarative projection + sum; concise & near-imperative speed (L9_1 recommended)."));

        // 3. LINQ .Select().Sum() (L9_1 Two-stage pipeline)
        var (val3, us3) = Measure(() =>
            input.Select(x => x == '^' ? 1 : x == 'v' ? -1 : 0).Sum());
        results.Add(("3. LINQ .Select().Sum()", "Aggregation (Part 1)", val3, us3,
            "Deferred iterator chain; slight delegate invocation overhead per item."));

        // 4. Higher-Order Fork Combinator (L4_1 / L9_1)
        var (val4, us4) = Measure(() =>
            input.Fork(
                s => s.Count(c => c == '^'),
                s => s.Count(c => c == 'v'),
                (up, down) => up - down));
        results.Add(("4. Fork Combinator (Up - Down)", "Combinator (L4_1)", val4, us4,
            "Clean declarative fan-out using Fork combinator; traverses sequence twice."));

        // 5. ValueTuple Trampoline IterateUntil (L7_1 & L9_1 Part 2 Indefinite Loop)
        var targetAltitude = val1 / 2;
        var (val5, us5) = Measure(() =>
        {
            var finalState = (Floor: 0, Index: 0).IterateUntil(
                s => (
                    Floor: s.Floor + (input[s.Index] == '^' ? 1 : -1),
                    Index: s.Index + 1
                ),
                s => s.Floor == targetAltitude || s.Index >= input.Length);
            return finalState.Index;
        });
        results.Add(("5. ValueTuple Trampoline (IterateUntil)", "Indefinite Loop (Part 2)", val5, us5,
            "Stack-safe functional loop with stack-allocated ValueTuple state (0 GC pressure)."));

        // 6. Custom IEnumerable<T> / IEnumerator<T> (L7_1 & L9_1)
        var (val6, us6) = Measure(() =>
        {
            var enumerable = new TransitStepEnumerable<(int Floor, int Index)>(
                (0, 0),
                s => (s.Floor + (input[s.Index] == '^' ? 1 : -1), s.Index + 1),
                s => s.Floor == targetAltitude || s.Index >= input.Length);
            return enumerable.Last().Index;
        });
        results.Add(("6. Custom IEnumerable<T> / IEnumerator<T>", "Indefinite Loop (Part 2)", val6, us6,
            "Lazy pull-based custom state enumerator supporting full LINQ composition."));

        var minUs = Math.Max(0.1, results.Min(r => r.Microseconds));

        return results.Select(r => new BenchmarkEntry(
            r.Name,
            r.Category,
            new intComputedResult(r.Value),
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

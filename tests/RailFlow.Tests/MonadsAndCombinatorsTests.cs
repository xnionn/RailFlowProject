using RailFlow.Core.Domain;
using RailFlow.Core.Functional;

namespace RailFlow.Tests;

/// <summary>
/// Reader and State monads, and the smaller combinators (Tap, Alt, AltMaybe) plus the partially applied logger.
/// </summary>
public sealed class MonadsAndCombinatorsTests
{
    private sealed record FareSettings(decimal RatePerKm, decimal Fee);

    [Fact]
    public void Reader_DefersReadingTheEnvironment_UntilRun()
    {
        // The fare formula is written once; the settings are supplied only when it runs.
        var rate = new Reader<FareSettings, decimal>(s => s.RatePerKm);
        var fareFor100Km = rate
            .Map(r => r * 100m)
            .Bind(baseFare => new Reader<FareSettings, decimal>(s => baseFare + s.Fee));

        Assert.Equal(24.50m, fareFor100Km.Run(new FareSettings(0.22m, 2.50m)));
        Assert.Equal(32.00m, fareFor100Km.Run(new FareSettings(0.30m, 2.00m)));
    }

    [Fact]
    public void State_ThreadsStateThroughBind_WithoutMutation()
    {
        // Issues sequential seat numbers; the counter is passed along, never stored in a variable.
        var nextSeat = new State<int, string>(n => ($"Seat {n}", n + 1));
        var twoSeats = nextSeat.Bind(first => nextSeat.Map(second => $"{first}, {second}"));

        var (value, finalState) = twoSeats.Run(12);

        Assert.Equal("Seat 12, Seat 13", value);
        Assert.Equal(14, finalState);

        // Running again from the same start gives the same answer: nothing was mutated.
        Assert.Equal(twoSeats.Run(12), (value, finalState));
    }

    [Fact]
    public void Tap_RunsTheSideEffect_AndReturnsTheSameValue()
    {
        var seen = new List<int>();

        var result = 42.Tap(seen.Add);

        Assert.Equal(42, result);
        Assert.Equal([42], seen);
    }

    [Fact]
    public void Alt_ReturnsFirstNonDefaultResult()
    {
        string? FromCache(string code) => null;
        string? FromAlias(string code) => code == "LDN" ? "LONDON" : null;
        string FromInput(string code) => code;

        Assert.Equal("LONDON", "LDN".Alt(FromCache, FromAlias, FromInput));
        Assert.Equal("PARIS", "PARIS".Alt(FromCache, FromAlias, FromInput));
    }

    [Fact]
    public void AltMaybe_StopsAtFirstSomething_OrReturnsLastResult()
    {
        var calls = 0;
        Maybe<int> None(string _) { calls++; return new UnhandledNothing<int>(); }
        Maybe<int> Length(string s) { calls++; return new Something<int>(s.Length); }

        var found = "ZURICH".AltMaybe(None, Length, Length);
        Assert.Equal(6, Assert.IsType<Something<int>>(found).InnerValue);
        Assert.Equal(2, calls);

        Assert.IsType<UnhandledNothing<int>>("X".AltMaybe<string, int>(None, None));
    }

    [Fact]
    public void ServiceLogger_IsFormatLogEntryWithServiceAndLevelFixed()
    {
        var warn = BenchmarkAndLoopLab.CreateServiceLogger("Pricing", "WARN");

        var line = warn("engineering works at LYON");

        Assert.EndsWith("[Pricing] [WARN] engineering works at LYON", line);
    }
}

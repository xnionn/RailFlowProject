using RailFlow.Core.Functional;

namespace RailFlow.Tests;

/// <summary>
/// Week 5 (pipelining) and Week 7 (Result / Either instead of exceptions).
/// </summary>
public class ResultAndPipelineTests
{
    private static Result<decimal> ParseAmount(string text) =>
        decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Result.Ok(v)
            : Result.Fail<decimal>($"'{text}' is not a number.");

    [Fact]
    public void Result_BindAndMap_StayOnSuccessTrack()
    {
        var outcome = ParseAmount("250")
            .Ensure(a => a > 0, "Amount must be positive.")
            .Map(a => a * 2);

        var success = Assert.IsType<Success<decimal>>(outcome);
        Assert.Equal(500m, success.Data);
    }

    [Theory]
    [InlineData("abc", "'abc' is not a number.")]
    [InlineData("-5", "Amount must be positive.")]
    public void Result_FirstFailure_ShortCircuitsTheRestOfTheChain(string input, string expectedReason)
    {
        var mapCalls = 0;

        var outcome = ParseAmount(input)
            .Ensure(a => a > 0, "Amount must be positive.")
            .Map(a => { mapCalls++; return a; });

        var failure = Assert.IsType<Failure<decimal>>(outcome);
        Assert.Equal(expectedReason, failure.Reason);
        Assert.Equal(0, mapCalls);
    }

    [Fact]
    public void ToResult_ConvertsNothingAndErrorIntoFailures()
    {
        Maybe<int> nothing = new UnhandledNothing<int>();
        Maybe<int> error = new UnhandledError<int>(new InvalidOperationException("sensor offline"));

        Assert.Equal("not found", Assert.IsType<Failure<int>>(nothing.ToResult("not found")).Reason);
        Assert.Equal("sensor offline", Assert.IsType<Failure<int>>(error.ToResult("not found")).Reason);
        Assert.Equal(7, Assert.IsType<Success<int>>(7.ToMaybe().ToResult("not found")).Data);
    }

    [Fact]
    public void Partition_SeparatesAcceptedValuesFromReasons()
    {
        var (accepted, rejected) = new[] { "1", "x", "3" }.Select(ParseAmount).Partition();

        Assert.Equal(new[] { 1m, 3m }, accepted);
        Assert.Single(rejected);
    }

    [Fact]
    public void Either_MapAndBind_OnlyTransformTheRightSide()
    {
        Either<string, int> right = new Right<string, int>(20);
        Either<string, int> left = new Left<string, int>("missing");

        Either<string, int> Halve(int x) =>
            x % 2 == 0 ? new Right<string, int>(x / 2) : new Left<string, int>("odd");

        Assert.Equal(new Right<string, int>(10), right.Bind(Halve));
        Assert.Equal(new Left<string, int>("missing"), left.Bind(Halve).Map(x => x + 1));
        Assert.Equal("value 21", right.Map(x => x + 1).Match(l => l, r => $"value {r}"));
    }

    // --- Week 5: Curry + Compose + Pipe + Pipeline(Aggregate) ---

    private static decimal ApplyDiscount(decimal rate, decimal amount) => amount - amount * rate;
    private static decimal AddFee(decimal fee, decimal amount) => amount + fee;
    private static decimal RoundToCents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    [Fact]
    public void Pipe_And_Compose_ReadLeftToRight_AndMatchNestedCalls()
    {
        var discount10 = CurryingAndLoops.Curry<decimal, decimal, decimal>(ApplyDiscount)(0.10m);
        var fee5 = CurryingAndLoops.Curry<decimal, decimal, decimal>(AddFee)(5m);
        Func<decimal, decimal> round = RoundToCents;

        var nested = RoundToCents(AddFee(5m, ApplyDiscount(0.10m, 1234.56m)));
        var piped = 1234.56m.Pipe(discount10).Pipe(fee5).Pipe(round);
        var checkout = discount10.Compose(fee5).Compose(round);

        Assert.Equal(1116.10m, nested);
        Assert.Equal(nested, piped);
        Assert.Equal(nested, checkout(1234.56m));
    }

    [Fact]
    public void Pipeline_OrderOfStepsMatters_AndEmptyPipelineIsIdentity()
    {
        var discount10 = CurryingAndLoops.Curry<decimal, decimal, decimal>(ApplyDiscount)(0.10m);
        var fee5 = CurryingAndLoops.Curry<decimal, decimal, decimal>(AddFee)(5m);

        var standard = Pipelining.Pipeline(discount10, fee5, RoundToCents)(1234.56m);
        var feeFirst = Pipelining.Pipeline(fee5, discount10, RoundToCents)(1234.56m);
        var identity = Pipelining.Pipeline<decimal>()(1234.56m);

        Assert.Equal(1116.10m, standard);
        Assert.Equal(1115.60m, feeFirst);
        Assert.Equal(1234.56m, identity);
    }
}

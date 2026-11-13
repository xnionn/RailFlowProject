using RailFlow.Core.Functional;

namespace RailFlow.Infrastructure.Services;

/// <summary>
/// Stand-in for a real card processor. It is the only side effect of the booking workflow and is passed
/// into <c>BookingWorkflow.Charge</c> as a <c>Func&lt;decimal, Result&lt;string&gt;&gt;</c> (Week 6).
/// The Dispatcher can switch on a payment outage from the Dispatcher tab to see the failure path.
/// </summary>
public sealed class SimulatedPaymentGateway(OperationsControls chaos)
{
    public const decimal CardLimit = 10_000m;

    private int _successfulCharges;

    public int SuccessfulCharges => _successfulCharges;

    public Result<string> Charge(decimal amount)
    {
        if (chaos.SimulatePaymentOutage)
            return Result.Fail<string>("Payment provider is unavailable (simulated outage). You were not charged.");

        if (amount <= 0m)
            return Result.Fail<string>("Amount to charge must be positive.");

        if (amount > CardLimit)
            return Result.Fail<string>($"Card declined: amount exceeds the €{CardLimit:N0} limit.");

        Interlocked.Increment(ref _successfulCharges);
        return Result.Ok("TX-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant());
    }
}

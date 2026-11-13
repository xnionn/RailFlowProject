using System.Collections.Immutable;
using RailFlow.Core.Domain;

namespace RailFlow.Infrastructure.Services;

public enum UserRole
{
    Passenger,
    Dispatcher
}

public sealed record PlannerFormSnapshot(
    string Origin,
    string Destination,
    int Passengers,
    string OfferingKey,
    DateTime TimestampUtc,
    DateOnly? TravelDate = null,
    TimeOnly? Departure = null);

/// <summary>
/// Per-session state of the planner (one instance per browser tab): the search history as two immutable
/// stacks for Undo/Redo, the signed-in role and the activity feed. Every change replaces a value instead of
/// mutating it, and raises OnStateChanged so the page re-renders.
/// </summary>
public sealed class ImmutableAppStateContainer
{
    private ImmutableStack<PlannerFormSnapshot> _undoStack = ImmutableStack<PlannerFormSnapshot>.Empty;
    private ImmutableStack<PlannerFormSnapshot> _redoStack = ImmutableStack<PlannerFormSnapshot>.Empty;

    public PlannerFormSnapshot CurrentSnapshot { get; private set; } = new(
        Origin: "LONDON",
        Destination: "ZURICH",
        Passengers: 2,
        OfferingKey: "HolidayWithMeals",
        TimestampUtc: DateTime.UtcNow,
        TravelDate: DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
        Departure: new TimeOnly(8, 0));

    public UserRole CurrentRole { get; private set; } = UserRole.Passenger;

    public ImmutableList<string> ActivityFeed { get; private set; }

    /// <param name="operations">The shared dispatcher settings; the feed starts with the engineering works in force now.</param>
    public ImmutableAppStateContainer(OperationsControls? operations = null)
    {
        var now = $"[{DateTime.UtcNow:HH:mm:ss}] ";
        var works = operations?.StationsWithEngineeringWorks.Select(StationNames.Display).Order().ToList() ?? [];
        ActivityFeed = ImmutableList.Create(
            now + "Session started.",
            now + (works.Count == 0
                ? "No engineering works at the moment."
                : $"Engineering works at {string.Join(", ", works)}: +{(operations!.EngineeringWorksMultiplier - 1) * 100:0}% on connections to those stations."));
    }

    public event Action? OnStateChanged;

    public bool CanUndo => !_undoStack.IsEmpty;
    public bool CanRedo => !_redoStack.IsEmpty;

    public void CommitSnapshot(PlannerFormSnapshot nextSnapshot)
    {
        if (nextSnapshot.Origin == CurrentSnapshot.Origin &&
            nextSnapshot.Destination == CurrentSnapshot.Destination &&
            nextSnapshot.Passengers == CurrentSnapshot.Passengers &&
            nextSnapshot.OfferingKey == CurrentSnapshot.OfferingKey &&
            nextSnapshot.TravelDate == CurrentSnapshot.TravelDate &&
            nextSnapshot.Departure == CurrentSnapshot.Departure)
        {
            return;
        }

        _undoStack = _undoStack.Push(CurrentSnapshot);
        _redoStack = ImmutableStack<PlannerFormSnapshot>.Empty;
        CurrentSnapshot = nextSnapshot with { TimestampUtc = DateTime.UtcNow };
        NotifyStateChanged();
    }

    public void Undo()
    {
        if (_undoStack.IsEmpty) return;
        _undoStack = _undoStack.Pop(out var previous);
        _redoStack = _redoStack.Push(CurrentSnapshot);
        CurrentSnapshot = previous;
        NotifyStateChanged();
    }

    public void Redo()
    {
        if (_redoStack.IsEmpty) return;
        _redoStack = _redoStack.Pop(out var restored);
        _undoStack = _undoStack.Push(CurrentSnapshot);
        CurrentSnapshot = restored;
        NotifyStateChanged();
    }

    public void SetRole(UserRole role)
    {
        if (CurrentRole == role) return;
        CurrentRole = role;
        AppendFeedMessage(role == UserRole.Dispatcher ? "Signed in as dispatcher." : "Signed in as passenger.");
    }

    public void AppendFeedMessage(string message)
    {
        var stamped = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        ActivityFeed = ActivityFeed.Insert(0, stamped);
        if (ActivityFeed.Count > 15)
        {
            ActivityFeed = ActivityFeed.Take(15).ToImmutableList();
        }
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}

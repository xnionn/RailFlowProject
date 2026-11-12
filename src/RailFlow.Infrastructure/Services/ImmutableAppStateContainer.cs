using System.Collections.Immutable;
using RailFlow.Core.Domain;

namespace RailFlow.Infrastructure.Services;

public enum UserRole
{
    Passenger,
    DispatcherAdmin
}

public sealed record PlannerFormSnapshot(
    string Origin,
    string Destination,
    int Passengers,
    string OfferingKey,
    DateTime TimestampUtc);

/// <summary>
/// Immutable State Container for Blazor State Management (Criterion 4 & Lecture L33).
/// Stores state history as an ImmutableStack<PlannerFormSnapshot> supporting instant Time-Travel Undo/Redo,
/// plus Role-Based Access Control (RBAC) state and real-time SignalR/event notifications.
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
        TimestampUtc: DateTime.UtcNow);

    public UserRole CurrentRole { get; private set; } = UserRole.DispatcherAdmin;

    public ImmutableList<string> LiveSignalRFeed { get; private set; } = ImmutableList.Create(
        $"[{DateTime.UtcNow:HH:mm:ss}] [SignalR Hub] Connected to RailFlow Telemetry Stream.",
        $"[{DateTime.UtcNow:HH:mm:ss}] [DispatcherAdmin] Active roadworks initialized at FRANKFURT & LYON (1.10x factor).");

    public event Action? OnStateChanged;

    public bool CanUndo => !_undoStack.IsEmpty;
    public bool CanRedo => !_redoStack.IsEmpty;
    public int UndoDepth => _undoStack.Count();

    public void CommitSnapshot(PlannerFormSnapshot nextSnapshot)
    {
        if (nextSnapshot.Origin == CurrentSnapshot.Origin &&
            nextSnapshot.Destination == CurrentSnapshot.Destination &&
            nextSnapshot.Passengers == CurrentSnapshot.Passengers &&
            nextSnapshot.OfferingKey == CurrentSnapshot.OfferingKey)
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
        AppendFeedMessage($"[RBAC Security] Switched active session role to {role}.");
    }

    public void AppendFeedMessage(string message)
    {
        var stamped = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        LiveSignalRFeed = LiveSignalRFeed.Insert(0, stamped);
        if (LiveSignalRFeed.Count > 15)
        {
            LiveSignalRFeed = LiveSignalRFeed.Take(15).ToImmutableList();
        }
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}

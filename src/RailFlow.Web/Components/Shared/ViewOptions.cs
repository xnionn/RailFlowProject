namespace RailFlow.Web.Components.Shared;

/// <summary>
/// Display choices of the current visitor, cascaded by Home. With <see cref="ShowCourseNotes"/> switched on the
/// page also shows the course material: "How it works" notes, the edge-case shortcuts, the search trace and the
/// Lab tab. It is an immutable record, so changing it means cascading a new value and every user re-renders.
/// </summary>
public sealed record ViewOptions(bool ShowCourseNotes)
{
    public static ViewOptions Default { get; } = new(ShowCourseNotes: false);
}

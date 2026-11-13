namespace RailFlow.Web.Components.Shared;

public enum StageStatus
{
    Done,
    Failed,
    Skipped
}

/// <summary>One row of a <see cref="StageList"/>: the stage name, its status and a line of detail.</summary>
public sealed record StageView(string Name, StageStatus Status, string Detail);

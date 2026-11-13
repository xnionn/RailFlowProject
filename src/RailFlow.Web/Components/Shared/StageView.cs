namespace RailFlow.Web.Components.Shared;

/// <summary>
/// One row of a <see cref="StageList"/>: the stage name, its status ("Done", "Failed" or "Skipped") and a line of detail.
/// </summary>
public sealed record StageView(string Name, string Status, string Detail);

using System.Globalization;

namespace RailFlow.Core.Domain;

/// <summary>
/// Station codes are stored in capitals (LONDON) because they are keys; people read them as names (London).
/// </summary>
public static class StationNames
{
    private static readonly TextInfo Text = CultureInfo.InvariantCulture.TextInfo;

    public static string Display(string code) =>
        string.IsNullOrWhiteSpace(code) ? "" : Text.ToTitleCase(code.Trim().ToLowerInvariant());
}

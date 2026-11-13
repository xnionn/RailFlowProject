using System.Collections.Immutable;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;

namespace RailFlow.Web.Components.Tabs;

/// <summary>
/// Inputs and results of the Lab tab (course experiments). Home keeps one instance per session, so the results
/// are still there when the visitor comes back to the tab. Every method calls pure functions from RailFlow.Core;
/// only the stopwatch readings are side effects.
/// </summary>
public sealed class LabTabState
{
    public const string SampleStationCsv =
        "code,latitude,longitude\nLONDON,51.53,-0.12\nPARIS,48.88,2.36\nATLANTIS,abc,0\nZURICH,47.38,8.54,EXTRA";

    // Station CSV check
    public string CustomCsvInput { get; set; } = SampleStationCsv;
    public ImmutableList<CsvCheckRow> CsvRows { get; private set; } = ImmutableList<CsvCheckRow>.Empty;
    public CsvImportReport<GeoCoord> StationValidation { get; private set; } = CsvImportReport<GeoCoord>.Empty;

    // Recursion test
    public string? StackSafetyMessage { get; private set; }

    // Route cache and benchmark
    public string? CacheMessage { get; set; }
    public ImmutableList<BenchmarkEntry> BenchmarkEntries { get; set; } = ImmutableList<BenchmarkEntry>.Empty;

    /// <summary>
    /// Validates the pasted lines with the same parser that loads stations.csv, and splits every line into cells
    /// with the partially applied CSV parser, so each row can be shown with its verdict.
    /// </summary>
    public void CheckCsv()
    {
        var lines = CustomCsvInput.Split('\n');
        StationValidation = NetworkCsv.ParseAll(lines, NetworkCsv.StationsHeader, "input", NetworkCsv.ParseStation);
        var rejected = StationValidation.Rejected.ToImmutableDictionary(r => r.LineNumber, r => r.Reason);

        CsvRows = lines
            .Select((text, index) => (Text: text.Trim(), Number: index + 1))
            .Where(l => l.Text.Length > 0)
            .Select(l => new CsvCheckRow(
                l.Number,
                BenchmarkAndLoopLab.StandardCsvParser(l.Text).FirstOrDefault() ?? ImmutableList<string>.Empty,
                IsHeader: string.Equals(l.Text.Replace(" ", ""), NetworkCsv.StationsHeader, StringComparison.OrdinalIgnoreCase),
                Problem: rejected.GetValueOrDefault(l.Number)))
            .ToImmutableList();
    }

    public void RunStackSafetyCheck()
    {
        const int segmentCount = 200_000;
        var segments = RecursiveTimetable.SyntheticSegments(segmentCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = RecursiveTimetable.TotalDistanceTrampolined(segments).Run();
        sw.Stop();
        StackSafetyMessage =
            $"Added up {segmentCount:N0} sections recursively in {sw.Elapsed.TotalMilliseconds:N0} ms (total {total:N0} km). " +
            "Plain recursion would need 200,000 nested calls and crash the server with a StackOverflowException.";
    }
}

/// <summary>One pasted CSV line: its number, its cells, and whether it is the header, valid, or rejected (with the reason).</summary>
public sealed record CsvCheckRow(int LineNumber, ImmutableList<string> Cells, bool IsHeader, string? Problem);

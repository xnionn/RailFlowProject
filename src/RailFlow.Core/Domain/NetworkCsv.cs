using System.Collections.Immutable;
using System.Globalization;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

public sealed record CsvRejection(string Source, int LineNumber, string Line, string Reason);

public sealed record CsvImportReport<T>(ImmutableList<T> Accepted, ImmutableList<CsvRejection> Rejected)
{
    public static CsvImportReport<T> Empty { get; } =
        new(ImmutableList<T>.Empty, ImmutableList<CsvRejection>.Empty);
}

/// <summary>
/// The rail network the services work with, plus where it came from and which input lines were rejected.
/// </summary>
public sealed record NetworkDataset(
    ImmutableList<GeoCoord> Stations,
    ImmutableList<GraphConnection> Connections,
    ImmutableList<CsvRejection> Rejected,
    string Source)
{
    /// <summary>
    /// Combines parsed stations and connections. Connections that point to an unknown station are rejected.
    /// </summary>
    public static NetworkDataset Build(
        CsvImportReport<GeoCoord> stations,
        CsvImportReport<GraphConnection> connections,
        string source)
    {
        var knownCodes = stations.Accepted
            .Select(s => s.StationCode)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        var (valid, unknown) = connections.Accepted.Aggregate(
            (Valid: ImmutableList<GraphConnection>.Empty, Unknown: ImmutableList<CsvRejection>.Empty),
            (acc, c) => knownCodes.Contains(c.FromStation) && knownCodes.Contains(c.ToStation)
                ? (acc.Valid.Add(c), acc.Unknown)
                : (acc.Valid, acc.Unknown.Add(new CsvRejection(
                    "connections", 0, $"{c.FromStation},{c.ToStation}",
                    $"Connection refers to an unknown station ({c.FromStation} or {c.ToStation})."))));

        return new NetworkDataset(
            stations.Accepted,
            valid,
            stations.Rejected.AddRange(connections.Rejected).AddRange(unknown),
            source);
    }
}

/// <summary>
/// Pure CSV parsing for the rail network (Week 6 File Handling, Week 10 Functional core / imperative shell).
/// This class never touches the file system: it receives lines and returns explicit outcomes,
/// so it can be tested with strings in memory. File reading lives in the Infrastructure layer.
/// </summary>
public static class NetworkCsv
{
    public const string StationsHeader = "code,latitude,longitude";
    public const string ConnectionsHeader = "from,to,distance_km,minutes,line";

    public static Result<GeoCoord> ParseStation(string line) =>
        SplitFields(line, expectedCount: 3)
            .Bind(f => ParseCode(f[0])
                .Bind(code => ParseNumber(f[1], "latitude", -90, 90)
                    .Bind(lat => ParseNumber(f[2], "longitude", -180, 180)
                        .Map(lon => new GeoCoord(lat, lon, code)))));

    public static Result<GraphConnection> ParseConnection(string line) =>
        SplitFields(line, expectedCount: 5)
            .Bind(f => ParseCode(f[0])
                .Bind(from => ParseCode(f[1])
                    .Ensure(to => !string.Equals(from, to, StringComparison.OrdinalIgnoreCase),
                        "A connection must link two different stations.")
                    .Bind(to => ParseNumber(f[2], "distance_km", 1, 5000)
                        .Bind(km => ParseWholeNumber(f[3], "minutes", 1, 3000)
                            .Bind(min => ParseText(f[4], "line")
                                .Map(lineName => new GraphConnection(from, to, km, min, lineName)))))));

    /// <summary>
    /// Parses every data line; comment lines (#), blank lines and the header row are skipped.
    /// Bad lines never stop the import: they are collected with their line number and reason.
    /// </summary>
    public static CsvImportReport<T> ParseAll<T>(
        IEnumerable<string> lines,
        string expectedHeader,
        string sourceName,
        Func<string, Result<T>> parseLine) =>
        lines
            .Select((text, index) => (Text: text.Trim(), Number: index + 1))
            .Where(l => l.Text.Length > 0 && !l.Text.StartsWith('#'))
            .Where(l => !string.Equals(l.Text.Replace(" ", ""), expectedHeader, StringComparison.OrdinalIgnoreCase))
            .Aggregate(CsvImportReport<T>.Empty, (report, l) => parseLine(l.Text) switch
            {
                Success<T> s => report with { Accepted = report.Accepted.Add(s.Data) },
                Failure<T> f => report with
                {
                    Rejected = report.Rejected.Add(new CsvRejection(sourceName, l.Number, l.Text, f.Reason))
                },
                _ => report
            });

    private static Result<ImmutableList<string>> SplitFields(string line, int expectedCount)
    {
        var fields = line.Split(',').Select(f => f.Trim()).ToImmutableList();
        return fields.Count == expectedCount
            ? Result.Ok(fields)
            : Result.Fail<ImmutableList<string>>($"Expected {expectedCount} fields but found {fields.Count}.");
    }

    private static Result<string> ParseCode(string raw) =>
        raw.Length is >= 2 and <= 20 && raw.All(c => char.IsLetter(c) || c == '-')
            ? Result.Ok(raw.ToUpperInvariant())
            : Result.Fail<string>($"'{raw}' is not a valid station code (2-20 letters).");

    private static Result<double> ParseNumber(string raw, string field, double min, double max) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value >= min && value <= max
                ? Result.Ok(value)
                : Result.Fail<double>($"Column {field}: {value} is outside the range {min}..{max}.")
            : Result.Fail<double>($"Column {field}: '{raw}' is not a number.");

    private static Result<int> ParseWholeNumber(string raw, string field, int min, int max) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value >= min && value <= max
                ? Result.Ok(value)
                : Result.Fail<int>($"Column {field}: {value} is outside the range {min}..{max}.")
            : Result.Fail<int>($"Column {field}: '{raw}' is not a whole number.");

    private static Result<string> ParseText(string raw, string field) =>
        string.IsNullOrWhiteSpace(raw)
            ? Result.Fail<string>($"Column {field} must not be empty.")
            : Result.Ok(raw);
}

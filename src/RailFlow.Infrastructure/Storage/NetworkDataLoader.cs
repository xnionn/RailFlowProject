using RailFlow.Core.Domain;
using RailFlow.Core.Functional;

namespace RailFlow.Infrastructure.Storage;

/// <summary>
/// Loads the rail network from CSV files (Week 6 File Handling, Week 10 Imperative shell).
/// Reading files happens only here; parsing and validation are done by the pure <see cref="NetworkCsv"/>.
/// If the files are missing or unreadable, the app keeps working with the built-in network.
/// </summary>
public static class NetworkDataLoader
{
    public const string StationsFileName = "stations.csv";
    public const string ConnectionsFileName = "connections.csv";

    public static Result<NetworkDataset> LoadFromDirectory(string directory)
    {
        var stationsPath = Path.Combine(directory, StationsFileName);
        var connectionsPath = Path.Combine(directory, ConnectionsFileName);

        return ReadLines(stationsPath)
            .Bind(stationLines => ReadLines(connectionsPath)
                .Map(connectionLines => NetworkDataset.Build(
                    NetworkCsv.ParseAll(stationLines, NetworkCsv.StationsHeader, StationsFileName, NetworkCsv.ParseStation),
                    NetworkCsv.ParseAll(connectionLines, NetworkCsv.ConnectionsHeader, ConnectionsFileName, NetworkCsv.ParseConnection),
                    source: $"{StationsFileName} + {ConnectionsFileName}")))
            .Ensure(d => d.Stations.Count >= 2 && d.Connections.Count >= 1,
                "CSV files contain fewer than 2 valid stations or no valid connections.");
    }

    public static NetworkDataset LoadOrEmbedded(string directory) =>
        LoadFromDirectory(directory).Match(
            onSuccess: dataset => dataset,
            onFailure: reason => EmbeddedNetwork.Dataset with
            {
                Source = "built-in network (fallback)",
                Rejected = EmbeddedNetwork.Dataset.Rejected.Add(new CsvRejection("loader", 0, directory, reason))
            });

    private static Result<IReadOnlyList<string>> ReadLines(string path)
    {
        if (!File.Exists(path))
            return Result.Fail<IReadOnlyList<string>>($"File '{Path.GetFileName(path)}' was not found.");

        try
        {
            // Materialized on purpose: the file is read once, here, inside the try block.
            return Result.Ok<IReadOnlyList<string>>(File.ReadAllLines(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Failure<IReadOnlyList<string>>($"Could not read '{Path.GetFileName(path)}': {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Built-in copy of the network, parsed with the same pure CSV functions as the files.
/// Used by unit tests and as a fallback when the data files are unavailable.
/// </summary>
public static class EmbeddedNetwork
{
    private const string StationsCsv = """
        code,latitude,longitude
        LONDON,51.5308,-0.1238
        PARIS,48.8809,2.3553
        BRUSSELS,50.8357,4.3365
        AMSTERDAM,52.3791,4.9003
        COLOGNE,50.9432,6.9586
        FRANKFURT,50.1071,8.6638
        ZURICH,47.3779,8.5403
        MILAN,45.4859,9.2043
        LYON,45.7606,4.8594
        MUNICH,48.1402,11.5599
        BERLIN,52.5251,13.3694
        VIENNA,48.1851,16.3766
        """;

    private const string ConnectionsCsv = """
        from,to,distance_km,minutes,line
        LONDON,PARIS,460,135,Eurostar High-Speed
        LONDON,BRUSSELS,370,120,Eurostar Channel Line
        PARIS,BRUSSELS,310,82,Thalys Red Corridor
        BRUSSELS,AMSTERDAM,210,110,Benelux Intercity
        BRUSSELS,COLOGNE,220,105,ICE International
        COLOGNE,FRANKFURT,190,62,ICE Rhine-Main Sprinter
        FRANKFURT,BERLIN,545,235,ICE Sprinter North-East
        FRANKFURT,MUNICH,390,195,ICE Bavarian Express
        FRANKFURT,ZURICH,400,230,EC Helvetia
        PARIS,LYON,465,118,TGV Sud-Est Duplex
        LYON,ZURICH,415,215,TGV Lyria Alpine
        LYON,MILAN,445,265,Frecciarossa Transalpine
        ZURICH,MILAN,285,195,Gotthard Base Tunnel EC
        MUNICH,VIENNA,405,240,Railjet Danube Express
        BERLIN,VIENNA,680,460,ICE Vindobona Corridor
        """;

    public static NetworkDataset Dataset { get; } = NetworkDataset.Build(
        NetworkCsv.ParseAll(StationsCsv.Split('\n'), NetworkCsv.StationsHeader, "embedded stations", NetworkCsv.ParseStation),
        NetworkCsv.ParseAll(ConnectionsCsv.Split('\n'), NetworkCsv.ConnectionsHeader, "embedded connections", NetworkCsv.ParseConnection),
        source: "built-in network");
}

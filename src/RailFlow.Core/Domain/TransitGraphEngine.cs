using System.Collections.Immutable;
using RailFlow.Core.Functional;

namespace RailFlow.Core.Domain;

public sealed record GraphConnection(string FromStation, string ToStation, double DistanceKm, int Minutes, string LineName);

public sealed record BfsPathResult(
    string StartStation,
    string TargetStation,
    int DegreesOfSeparation,
    ImmutableList<string> StationChain,
    ImmutableList<RouteSegment> Segments,
    double TotalDistanceKm,
    int TotalMinutes);

/// <summary>
/// Implements the Breadth-First Search (BFS) shortest-hop/degrees-of-separation algorithm
/// from Lecture L8_1 (Kevin Bacon Graph Search) paired with Thread-Safe Memoization.
/// </summary>
public sealed class TransitGraphEngine
{
    private readonly ImmutableList<GraphConnection> _edges;
    private readonly MemoizedFunc<(string Start, string End), Maybe<BfsPathResult>> _memoizedBfs;

    public TransitGraphEngine(IEnumerable<GraphConnection> edges)
    {
        _edges = edges.ToImmutableList();
        Func<(string Start, string End), Maybe<BfsPathResult>> rawSearch = pair =>
            ComputeBfsShortestPath(pair.Start, pair.End);
        _memoizedBfs = rawSearch.MemoizeThreadSafeWithStats();
    }

    public ImmutableList<GraphConnection> Edges => _edges;

    public Maybe<BfsPathResult> FindShortestPathMemoized(string startStation, string endStation) =>
        _memoizedBfs.Invoke((startStation.Trim().ToUpperInvariant(), endStation.Trim().ToUpperInvariant()));

    public MemoizationStats GetCacheStats() => _memoizedBfs.GetStats();

    public void ClearCache() => _memoizedBfs.Clear();

    /// <summary>
    /// Pure Breadth-First Search (BFS) over the station adjacency graph (Lecture L8_1).
    /// Explores layer-by-layer to find the minimum degrees of separation (station hops).
    /// </summary>
    private Maybe<BfsPathResult> ComputeBfsShortestPath(string start, string target)
    {
        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(target))
            return new UnhandledNothing<BfsPathResult>();

        if (string.Equals(start, target, StringComparison.OrdinalIgnoreCase))
        {
            return new Something<BfsPathResult>(new BfsPathResult(
                start, target, 0, ImmutableList.Create(start), ImmutableList<RouteSegment>.Empty, 0, 0));
        }

        var adjacency = _edges
            .SelectMany(e => new[]
            {
                e,
                new GraphConnection(e.ToStation, e.FromStation, e.DistanceKm, e.Minutes, e.LineName)
            })
            .GroupBy(e => e.FromStation, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToImmutableList(), StringComparer.OrdinalIgnoreCase);

        if (!adjacency.ContainsKey(start) || !adjacency.ContainsKey(target))
            return new UnhandledNothing<BfsPathResult>();

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var queue = new Queue<(string Current, ImmutableList<string> Chain, ImmutableList<RouteSegment> Segments)>();
        queue.Enqueue((start, ImmutableList.Create(start), ImmutableList<RouteSegment>.Empty));

        while (queue.Count > 0)
        {
            var (current, chain, segments) = queue.Dequeue();

            if (!adjacency.TryGetValue(current, out var neighbors))
                continue;

            foreach (var edge in neighbors)
            {
                if (!visited.Add(edge.ToStation))
                    continue;

                var nextChain = chain.Add(edge.ToStation);
                var nextSegments = segments.Add(new RouteSegment(
                    edge.FromStation, edge.ToStation, edge.DistanceKm, edge.Minutes, edge.LineName));

                if (string.Equals(edge.ToStation, target, StringComparison.OrdinalIgnoreCase))
                {
                    var totalKm = nextSegments.Sum(s => s.DistanceKm);
                    var totalMin = nextSegments.Sum(s => s.BaseMinutes);
                    return new Something<BfsPathResult>(new BfsPathResult(
                        start,
                        target,
                        nextSegments.Count,
                        nextChain,
                        nextSegments,
                        Math.Round(totalKm, 1),
                        totalMin));
                }

                queue.Enqueue((edge.ToStation, nextChain, nextSegments));
            }
        }

        return new UnhandledNothing<BfsPathResult>();
    }
}

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using RailFlow.Core.Domain;
using RailFlow.Core.Functional;

namespace RailFlow.Infrastructure.Storage;

public enum JournalFormat
{
    TextLog,
    Json
}

public sealed record JournalContents(ImmutableList<BookingReceipt> Receipts, ImmutableList<string> SkippedLines)
{
    public static JournalContents Empty { get; } =
        new(ImmutableList<BookingReceipt>.Empty, ImmutableList<string>.Empty);
}

/// <summary>
/// Week 4: one interface for both storage formats, so calling code is written once and works with either.
/// Appending never replaces existing data, and a booking that is already stored is not written twice.
/// A missing file is not an error: it simply means there are no bookings yet.
/// </summary>
public interface IBookingJournal
{
    JournalFormat Format { get; }

    string FilePath { get; }

    Result<JournalContents> LoadAll();

    /// <summary>
    /// Returns how many receipts were actually added (duplicates are skipped).
    /// </summary>
    Result<int> Append(IEnumerable<BookingReceipt> receipts);
}

/// <summary>
/// Imperative shell around the file system. All I/O exceptions are caught here, at the boundary,
/// and turned into Failure values; the functional core never sees them.
/// </summary>
internal static class FileBoundary
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static object LockFor(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    public static Result<T> Guard<T>(string path, Func<Result<T>> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The user gets a plain sentence; the exception stays attached to the Failure for the log.
            var file = Path.GetFileName(path);
            var reason = ex switch
            {
                JsonException => $"The booking file '{file}' is damaged and could not be read.",
                UnauthorizedAccessException => $"There is no permission to use the booking file '{file}'.",
                _ => $"The booking file '{file}' could not be read or written."
            };
            return new Failure<T>(reason, ex);
        }
    }

    public static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }
}

/// <summary>
/// Human-readable log: one receipt per line, in the format defined by <see cref="ReceiptLineCodec"/>.
/// </summary>
public sealed class TextBookingJournal(string filePath) : IBookingJournal
{
    public JournalFormat Format => JournalFormat.TextLog;

    public string FilePath { get; } = filePath;

    public Result<JournalContents> LoadAll() =>
        FileBoundary.Guard(FilePath, () =>
        {
            lock (FileBoundary.LockFor(FilePath))
            {
                return Result.Ok(ReadUnlocked());
            }
        });

    public Result<int> Append(IEnumerable<BookingReceipt> receipts) =>
        FileBoundary.Guard(FilePath, () =>
        {
            lock (FileBoundary.LockFor(FilePath))
            {
                var existing = ReadUnlocked().Receipts.ToHashSet();
                var toAdd = receipts.Distinct().Where(r => !existing.Contains(r)).ToList();
                if (toAdd.Count > 0)
                {
                    FileBoundary.EnsureDirectory(FilePath);
                    File.AppendAllLines(FilePath, toAdd.Select(ReceiptLineCodec.ToLine));
                }

                return Result.Ok(toAdd.Count);
            }
        });

    private JournalContents ReadUnlocked()
    {
        if (!File.Exists(FilePath))
            return JournalContents.Empty;

        var (receipts, skipped) = File.ReadLines(FilePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ReceiptLineCodec.FromLine)
            .Partition();

        return new JournalContents(receipts.Distinct().ToImmutableList(), skipped);
    }
}

/// <summary>
/// Machine-readable store: the whole list as a JSON array. Writes go to a temporary file first and
/// replace the original only after they succeed, so a crash mid-write cannot corrupt existing data.
/// </summary>
public sealed class JsonBookingJournal(string filePath) : IBookingJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JournalFormat Format => JournalFormat.Json;

    public string FilePath { get; } = filePath;

    public Result<JournalContents> LoadAll() =>
        FileBoundary.Guard(FilePath, () =>
        {
            lock (FileBoundary.LockFor(FilePath))
            {
                return Result.Ok(new JournalContents(ReadUnlocked(), ImmutableList<string>.Empty));
            }
        });

    public Result<int> Append(IEnumerable<BookingReceipt> receipts) =>
        FileBoundary.Guard(FilePath, () =>
        {
            lock (FileBoundary.LockFor(FilePath))
            {
                var existing = ReadUnlocked();
                var known = existing.ToHashSet();
                var toAdd = receipts.Distinct().Where(r => !known.Contains(r)).ToList();
                if (toAdd.Count == 0)
                    return Result.Ok(0);

                FileBoundary.EnsureDirectory(FilePath);
                var tempPath = FilePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(existing.AddRange(toAdd), JsonOptions));
                File.Move(tempPath, FilePath, overwrite: true);
                return Result.Ok(toAdd.Count);
            }
        });

    private ImmutableList<BookingReceipt> ReadUnlocked()
    {
        if (!File.Exists(FilePath))
            return ImmutableList<BookingReceipt>.Empty;

        var json = File.ReadAllText(FilePath);
        return string.IsNullOrWhiteSpace(json)
            ? ImmutableList<BookingReceipt>.Empty
            : (JsonSerializer.Deserialize<List<BookingReceipt>>(json, JsonOptions) ?? []).Distinct().ToImmutableList();
    }
}

/// <summary>
/// Week 6: factory that centralizes which journal implementation is created for a format.
/// <see cref="ForDirectory"/> returns a factory function with the storage directory already captured,
/// which is what the Blazor app receives through dependency injection.
/// </summary>
public static class BookingJournalFactory
{
    public const string TextFileName = "bookings.log";
    public const string JsonFileName = "bookings.json";

    public static IBookingJournal Create(JournalFormat format, string directory) => format switch
    {
        JournalFormat.TextLog => new TextBookingJournal(Path.Combine(directory, TextFileName)),
        JournalFormat.Json => new JsonBookingJournal(Path.Combine(directory, JsonFileName)),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported journal format.")
    };

    public static Func<JournalFormat, IBookingJournal> ForDirectory(string directory) =>
        format => Create(format, directory);
}

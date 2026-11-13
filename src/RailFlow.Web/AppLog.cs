using RailFlow.Core.Domain;
using RailFlow.Core.Functional;

namespace RailFlow.Web;

/// <summary>
/// Log messages of the web app, in one place. The functional core stays free of logging: the shell logs the
/// values it gets back (an Error outcome, a Failure from storage), usually through the Tap combinator.
/// An exception caught inside Map/Bind is turned into an Error value for the user, but its stack trace
/// is written here, so programming errors are not hidden.
/// </summary>
internal static partial class AppLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Route search {Origin} → {Destination} failed")]
    public static partial void SearchFailed(this ILogger logger, Exception exception, string origin, string destination);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "No route found for {Origin} → {Destination}")]
    public static partial void NoRoute(this ILogger logger, string origin, string destination);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Booking {BookingId} stored, {Amount} paid")]
    public static partial void BookingStored(this ILogger logger, string bookingId, decimal amount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Booking not completed: {Reason}")]
    public static partial void BookingNotCompleted(this ILogger logger, string reason);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Booking file could not be read: {Reason}")]
    public static partial void HistoryUnavailable(this ILogger logger, string reason);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Network data from {Source}: {Count} input line(s) rejected")]
    public static partial void NetworkLinesRejected(this ILogger logger, string source, int count);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "{Actor} switched engineering works at {Station} {State}")]
    public static partial void EngineeringWorksChanged(this ILogger logger, string actor, string station, string state);

    /// <summary>Writes what went wrong in a search; a priced trip is not logged.</summary>
    public static void SearchOutcome(this ILogger logger, PipelineExecutionReport report, string origin, string destination)
    {
        switch (report.Outcome)
        {
            case Error<PricedItinerary> { ErrorDetails: var exception }:
                logger.SearchFailed(exception, origin, destination);
                break;
            case Nothing<PricedItinerary>:
                logger.NoRoute(origin, destination);
                break;
        }
    }

    /// <summary>Writes the result of a booking attempt: the booking id, or the reason it stopped.</summary>
    public static void BookingOutcome(this ILogger logger, Result<BookingReceipt> stored)
    {
        switch (stored)
        {
            case Success<BookingReceipt> { Data: var receipt }:
                logger.BookingStored(receipt.BookingId, receipt.AmountPaid);
                break;
            case Failure<BookingReceipt> { Reason: var reason }:
                logger.BookingNotCompleted(reason);
                break;
        }
    }

    public static void NetworkData(this ILogger logger, NetworkDataset dataset)
    {
        if (dataset.Rejected.Count > 0)
            logger.NetworkLinesRejected(dataset.Source, dataset.Rejected.Count);
    }
}

using System.Collections.Immutable;
using RailFlow.Core.Domain;
using RailFlow.Infrastructure.Storage;
using RailFlow.Web.Components.Shared;

namespace RailFlow.Web.Components.Tabs;

/// <summary>
/// What the Bookings tab remembers for the whole browser session. Home keeps one instance and passes it to
/// <see cref="BookingsTab"/>, because the tab component itself is created again every time the tab is opened.
/// Above all this keeps <see cref="OwnBookingIds"/>: the bookings made in this session, which are the only
/// ones a passenger may see unmasked.
/// </summary>
public sealed class BookingsTabState
{
    // Form fields
    public string PassengerName { get; set; } = "";
    public string PassengerEmail { get; set; } = "";
    public string PromoCode { get; set; } = "";
    public JournalFormat JournalFormat { get; set; } = JournalFormat.Json;
    public TicketFormat TicketFormat { get; set; } = TicketFormat.PlainText;

    /// <summary>Progress of the last booking attempt. A new search clears it (see <see cref="ClearStages"/>).</summary>
    public ImmutableList<StageView> Stages { get; set; } = ImmutableList<StageView>.Empty;

    /// <summary>
    /// The search whose card was already charged (whether saving to disk succeeded or failed),
    /// so a second click or a re-price cannot charge the card again.
    /// </summary>
    public int? ChargedSearchId { get; set; }

    /// <summary>
    /// The search that was both paid and saved to the booking journal.
    /// </summary>
    public int? BookedSearchId { get; set; }

    /// <summary>
    /// Immutable snapshot of the receipt confirmed and saved for <see cref="BookedSearchId"/>.
    /// Kept separate from <see cref="SelectedReceipt"/> so browsing the history table only changes
    /// the e-ticket preview and never alters the confirmation banner.
    /// </summary>
    public BookingReceipt? BookedReceipt { get; set; }

    /// <summary>
    /// Receipt from a charge that succeeded when writing to the booking journal subsequently failed.
    /// Kept so the user sees the transaction reference and recovery instructions instead of a fake confirmation.
    /// </summary>
    public BookingReceipt? UnsavedPaidReceipt { get; set; }

    public string? UnsavedStorageReason { get; set; }

    // History as last loaded from the selected journal file
    public ImmutableList<BookingReceipt> History { get; set; } = ImmutableList<BookingReceipt>.Empty;
    public ImmutableList<string> SkippedLines { get; set; } = ImmutableList<string>.Empty;
    public string? HistoryError { get; set; }
    public string? StorageMessage { get; set; }
    public BookingReceipt? SelectedReceipt { get; set; }

    /// <summary>
    /// Bookings made in this browser tab. A guest has no account, so these are the only bookings a guest sees.
    /// </summary>
    public HashSet<string> OwnBookingIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Passenger names, e-mails and booking ids are personal data: dispatchers see every booking, a signed-in
    /// passenger the bookings of their account, and a guest the bookings made in this browser tab.
    /// </summary>
    public bool CanSeeDetails(BookingReceipt receipt, bool isDispatcher, string owner = "") =>
        isDispatcher
        || OwnBookingIds.Contains(receipt.BookingId)
        || (owner.Length > 0 && string.Equals(receipt.Owner, owner, StringComparison.OrdinalIgnoreCase));

    public void ClearStages()
    {
        Stages = ImmutableList<StageView>.Empty;
        ChargedSearchId = null;
        BookedSearchId = null;
        BookedReceipt = null;
        UnsavedPaidReceipt = null;
        UnsavedStorageReason = null;
    }
}

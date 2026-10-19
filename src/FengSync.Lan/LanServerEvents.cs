namespace FengSync.Lan;

/// <summary>
/// What the listener saw happen, in the form a window can put on screen.
/// </summary>
/// <remarks>
/// These are separate records rather than one event with a kind field because each one answers a different
/// question in the UI — who paired, what landed, what a person refused, how far a transfer has got — and a
/// reader of the server code should be able to see which of them a route raises. They are plain records, not
/// <see cref="EventArgs"/> subclasses, because a record cannot derive from anything and the payload already
/// names the peer that acted; there is no <c>sender</c> left to carry.
/// </remarks>
public sealed record LanPairAttemptEventArgs(LanContact Contact, LanRefusal Refusal)
{
    public bool Accepted => Refusal == LanRefusal.None;
}

/// <summary>
/// A device that already had a row corrected its own name. The name it was listed under before travels with it,
/// because the thing worth showing is 改名 — and the room's older rows keep the name they were said under, so a
/// window holding only the new one could not say what changed.
/// </summary>
public sealed record LanPeerRenamedEventArgs(LanContact Contact, string PreviousName);

/// <summary>A file the sink published.</summary>
public sealed record LanInboxEventArgs(LanContact Contact, LanReceived Received, string? Session);

/// <summary>A directory a peer asked for, which is how an empty folder arrives at all.</summary>
public sealed record LanDirectoryEventArgs(LanContact Contact, string RelativePath, string? Session);

/// <summary>
/// A write this side decided about before any byte moved. Raised for both answers, so the window can show that
/// a device it never agreed to is still trying.
/// </summary>
public sealed record LanAdmissionEventArgs(LanInboundOffer Offer, LanRefusal Decision)
{
    public bool Accepted => Decision == LanRefusal.None;
}

/// <summary>
/// One progress tick. <paramref name="TotalBytes"/> is 0 when the sender declined to declare a length, so a
/// window cannot draw a fraction out of a number it was never given.
/// </summary>
/// <param name="TransferId">
/// The key the board filed this transfer under. A window keys its rows on this rather than on the path, because
/// the path a peer named is not the name the sink settled on — a file stored as <c>a (1).txt</c> would otherwise
/// never close the row its arrival opened.
/// </param>
/// <param name="SpeedBytesPerSecond">
/// Measured once, by <see cref="LanTransferBoard"/>, so the page that polls the same board shows the same number.
/// </param>
/// <param name="IsDone">The last tick of this transfer, either way. The row is finished and should stop showing a bar.</param>
/// <param name="Error">
/// Why the transfer ended badly, when it did. A finished row that says only 「已结束」 costs the user the one thing
/// worth knowing about a failure — whether the bytes are somewhere else or nowhere at all.
/// </param>
public sealed record LanProgressEventArgs(
    LanContact Contact,
    string Path,
    long Bytes,
    long TotalBytes,
    bool IsInbound,
    string TransferId = "",
    double SpeedBytesPerSecond = 0,
    bool IsDone = false,
    string? Error = null);

/// <summary>
/// Bytes this machine handed to a peer. It is the item's own length rather than the range the peer took,
/// because a resumed download is one delivery of one file and a history row that counted the restart would
/// say the file went twice.
/// </summary>
public sealed record LanOutboundEventArgs(LanContact Contact, LanSendItem Item, bool IsArchive);

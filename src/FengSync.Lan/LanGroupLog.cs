namespace FengSync.Lan;

/// <summary>
/// What this machine knows the room said — held in memory and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// The log is a replica, not a store. Every member keeps its own copy of what it heard, that copy lives exactly
/// as long as the window that opened it, and nothing in here is ever written to disk: leaving the room is closing
/// the window, and when the last member closes theirs the history is gone rather than archived. That is the whole
/// reason an entry can name a file without its bytes being here — a record of what somebody offered is cheap, and
/// a copy of it is not something this feature is allowed to make on its own.
/// </para>
/// <para>
/// Identity is the message's own id, minted by whoever spoke. A device that leaves and comes back may restart its
/// own numbering and mint nothing that collides, because a GUID it made before and a GUID it makes now are
/// different objects even when both are its first sentence of a session — which is why merging keys on the id and
/// never on an author's counter. Keying on a counter would silently drop a re-joining member's new sentence as a
/// duplicate of an old one, and no test on a single window could ever see it happen.
/// </para>
/// </remarks>
public sealed class LanGroupLog(int maximumEntries = LanGroupLog.MaximumEntries)
{
    /// <summary>How much of the room's past one member keeps. Long enough to scroll back, bounded because memory is the only place it lives.</summary>
    public const int MaximumEntries = 1000;

    /// <summary>The longest sentence a member may say. A page that lets a person type more is told to stop before a request is spent.</summary>
    public const int MaximumTextLength = 2000;

    private readonly object _gate = new();
    private readonly List<LanGroupMessage> _entries = [];
    private readonly Dictionary<string, LanGroupMessage> _byId = new(StringComparer.OrdinalIgnoreCase);
    private long _localSeq;

    /// <summary>A new entry landed, from this machine or from a peer. Raised off the lock, on whichever thread spoke.</summary>
    public event Action<LanGroupMessage>? Appended;

    /// <summary>
    /// An entry aged out of this copy. The shell uses it to forget where it kept the bytes, because an entry nobody
    /// can scroll back to must not keep pointing at a file in the user's directory. It never deletes that file: the
    /// published copy is the user's, and this log has no claim on it.
    /// </summary>
    public event Action<LanGroupMessage>? Trimmed;

    /// <summary>This machine's arrival watermark, which is what a peer's <c>since</c> keys on for this copy.</summary>
    public long Head
    {
        get { lock (_gate) return _localSeq; }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>The copy this machine holds, in the order it arrived.</summary>
    public IReadOnlyList<LanGroupMessage> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    /// <summary>A sentence from a member. Empty after trimming is a request that said nothing, and a too-long one is refused before it lands.</summary>
    /// <param name="mintedMessageId">
    /// The id the speaker gave its own sentence, so the same words arriving from two paths — a fan-out that raced a
    /// poll — become one row rather than two. An id that is not a message id is ignored rather than trusted: a
    /// peer naming an id this copy already holds under another author would otherwise be able to make a sentence
    /// vanish by re-delivering it.
    /// </param>
    public LanRefusal TryAppendText(LanContact author, string? text, DateTimeOffset createdUtc, out LanGroupMessage? entry,
        string? mintedMessageId = null)
    {
        entry = null;
        var said = (text ?? "").Trim();
        if (said.Length == 0) return LanRefusal.BadRequest;
        if (said.Length > MaximumTextLength) return LanRefusal.MessageTooLong;

        var id = LanProtocol.IsMessageId(mintedMessageId) ? mintedMessageId! : Guid.NewGuid().ToString("N");
        // The duplicate is the normal answer to being told the same thing twice, and the entry that is already
        // here is the one to hand back, so a relaying caller can report the id it just repeated.
        if (TryFind(id, out var known))
        {
            entry = known;
            return LanRefusal.None;
        }

        entry = Append(new LanGroupMessage
        {
            MessageId = id,
            AuthorDeviceId = author.DeviceId,
            AuthorName = author.DeviceName,
            Kind = LanGroupKind.Text,
            Text = said,
            CreatedUtc = createdUtc,
        });
        return LanRefusal.None;
    }

    /// <summary>
    /// The announcement of a file or folder. Nothing is copied here: an entry is a promise that somebody can keep
    /// if it can be reached, and <paramref name="localPath"/> is how the announcer says it can be reached now.
    /// </summary>
    public LanGroupMessage AppendFile(LanContact author, string fileName, string? relativePath, long bytes,
        bool isDirectory, DateTimeOffset createdUtc, string? localPath = null)
    {
        var name = fileName.Trim();
        return Append(new LanGroupMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            AuthorDeviceId = author.DeviceId,
            AuthorName = author.DeviceName,
            Kind = isDirectory ? LanGroupKind.Directory : LanGroupKind.File,
            FileName = name.Length == 0 ? null : name,
            RelativePath = string.IsNullOrWhiteSpace(relativePath) ? null : relativePath.Trim(),
            Bytes = bytes < 0 ? 0 : bytes,
            IsDirectory = isDirectory,
            CreatedUtc = createdUtc,
            HeldHere = localPath is not null,
            LocalPath = localPath,
        });
    }

    /// <summary>
    /// A line this machine writes about the room rather than about a member: who joined, who renamed themselves,
    /// whose firewall question was answered. It is authored by nobody, so no peer can ever be told it said this.
    /// </summary>
    public LanGroupMessage AppendSystem(string text, DateTimeOffset createdUtc) => Append(new LanGroupMessage
    {
        MessageId = Guid.NewGuid().ToString("N"),
        AuthorDeviceId = "",
        AuthorName = "",
        Kind = LanGroupKind.Text,
        Text = text,
        CreatedUtc = createdUtc,
    });

    /// <summary>
    /// Adopts an entry a peer already has.
    /// </summary>
    /// <returns><see langword="false"/> when this machine already holds that id, which is the normal answer to a
    /// poll that overlaps and not a fault — a replica converges by being told the same thing twice.</returns>
    public bool TryMerge(LanMessageWire wire)
    {
        if (string.IsNullOrWhiteSpace(wire.MessageId) || !LanGroupKind.IsKnown(wire.Kind)) return false;
        var message = new LanGroupMessage
        {
            MessageId = wire.MessageId,
            AuthorDeviceId = wire.AuthorDeviceId ?? "",
            AuthorName = LanProtocol.SanitizePeerName(wire.AuthorName),
            Kind = wire.Kind,
            Text = wire.Text,
            FileName = wire.FileName,
            RelativePath = wire.RelativePath,
            Bytes = wire.Bytes,
            IsDirectory = wire.IsDirectory,
            CreatedUtc = wire.CreatedUtc,
            // This side cannot hand over bytes it was only told about, and a copy that believed a peer's claim
            // would offer a 接收 button for a file it cannot open.
            HeldHere = false,
        };

        List<LanGroupMessage> dropped;
        lock (_gate)
        {
            if (_byId.ContainsKey(message.MessageId)) return false;
            dropped = Index(message);
        }

        Appended?.Invoke(message);
        // A copy that shrank only by what this machine said would grow forever by what everybody else said, and the
        // cap is the only bound on the one place this history lives — memory.
        foreach (var gone in dropped) Trimmed?.Invoke(gone);
        return true;
    }

    /// <summary>The entries this copy has and that caller has not been shown yet, in this machine's arrival order.</summary>
    public IReadOnlyList<LanGroupMessage> Since(long seq)
    {
        lock (_gate) return [.. _entries.Where(entry => entry.LocalSeq > seq)];
    }

    public bool TryFind(string messageId, out LanGroupMessage entry)
    {
        lock (_gate) return _byId.TryGetValue(messageId ?? "", out entry!);
    }

    /// <summary>
    /// Says these bytes are on this machine, at a path the caller already owns.
    /// </summary>
    /// <remarks>
    /// Only a reference is kept, and it is a reference to the file the sink published — the receive directory is
    /// where the bytes are, not a second copy of them. An id this copy has never heard of is dropped silently: a
    /// relay that announced its own bytes and was not answered has nothing to mark, and inventing an entry to mark
    /// would put a file in the room that nobody offered.
    /// </remarks>
    public void MarkHeld(string messageId, string localPath)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(messageId ?? "", out var message)) return;
            message.HeldHere = true;
            message.LocalPath = localPath;
        }
    }

    /// <summary>
    /// Says this machine can no longer hand over the bytes behind an entry, leaving what the room said intact.
    /// </summary>
    /// <remarks>
    /// The entry is not removed: a replicated copy has no authority to un-say another member's sentence, and a
    /// member that cannot serve is a normal state of this log rather than a fault — most entries in it were never
    /// this machine's to hold. Only the promise goes, because a path named once and now gone would otherwise keep
    /// offering a 接收 this copy cannot honour.
    /// </remarks>
    public void DropHeld(string messageId)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(messageId ?? "", out var message)) return;
            message.HeldHere = false;
            message.LocalPath = null;
        }
    }

    /// <summary>
    /// Files the bytes a peer just pushed under the entry it named — or announces that entry here, when this copy
    /// has never heard of it.
    /// </summary>
    /// <returns>
    /// The entry the arrival became or updated, and <see langword="null"/> when the id belongs to another member.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A push is the only way a device nobody can dial gets a file into the room, so an arrival has to be able to
    /// speak for itself. <paramref name="relativePath"/> and <paramref name="localPath"/> are both things this
    /// machine observed rather than believed: the path the sink was handed, and the path it published. The time
    /// is this machine's too, because a write carries no clock the room could key a display order on.
    /// </para>
    /// <para>
    /// The id arrives in a header while the author arrives in a credential, so the two are compared rather than
    /// merged. An id this copy already knows under <b>another</b> member's authorship is left exactly as it was and
    /// nothing is returned: a device cannot retarget somebody else's announcement by tagging its own bytes with
    /// that id, because every later 接收 would then read this machine's copy as pointing at a path the pusher
    /// chose while still naming the other member's file. The write itself is not refused for that reason — the
    /// bytes are already on disk by the time a tag is read, and a file in the receive directory is the thing the
    /// user agreed to.
    /// </para>
    /// <para>
    /// A same-author arrival that is already known only moves the path, which is what a retried or renamed send
    /// needs: the newest copy this machine holds is the one it can hand over. Neither marking case raises
    /// <see cref="Appended"/>, because nothing new entered the room; the shell learns of the arrival from the
    /// receive event, which is the one that carries the peer.
    /// </para>
    /// </remarks>
    public LanGroupMessage? Hold(LanContact author, string messageId, string relativePath, long bytes,
        bool isDirectory, string localPath, DateTimeOffset createdUtc)
    {
        if (!LanProtocol.IsMessageId(messageId)) return null;

        LanGroupMessage? message = null;
        List<LanGroupMessage> dropped = [];
        var announced = false;
        lock (_gate)
        {
            if (_byId.TryGetValue(messageId, out var known))
            {
                if (!string.Equals(known.AuthorDeviceId, author.DeviceId, StringComparison.OrdinalIgnoreCase)) return null;
                known.HeldHere = true;
                known.LocalPath = localPath;
                message = known;
            }
            else
            {
                message = new LanGroupMessage
                {
                    MessageId = messageId,
                    AuthorDeviceId = author.DeviceId,
                    AuthorName = author.DeviceName,
                    Kind = isDirectory ? LanGroupKind.Directory : LanGroupKind.File,
                    FileName = LanRelativePath.NameOf(relativePath),
                    RelativePath = relativePath.Length == 0 ? null : relativePath,
                    Bytes = bytes < 0 ? 0 : bytes,
                    IsDirectory = isDirectory,
                    CreatedUtc = createdUtc,
                    HeldHere = true,
                    LocalPath = localPath,
                };
                dropped = Index(message);
                announced = true;
            }
        }

        if (announced) Appended?.Invoke(message!);
        foreach (var gone in dropped) Trimmed?.Invoke(gone);
        return message;
    }

    private LanGroupMessage Append(LanGroupMessage message)
    {
        List<LanGroupMessage> dropped;
        lock (_gate) dropped = Index(message);

        Appended?.Invoke(message);
        foreach (var gone in dropped) Trimmed?.Invoke(gone);
        return message;
    }

    /// <summary>Caller holds the gate. Numbers the entry, files it, and returns whatever the cap pushed out.</summary>
    private List<LanGroupMessage> Index(LanGroupMessage message)
    {
        _localSeq++;
        message.LocalSeq = _localSeq;
        _entries.Add(message);
        _byId[message.MessageId] = message;
        return TrimLocked();
    }

    /// <summary>Caller holds the gate. Oldest first, because the newest thing a person said is the last one to throw away.</summary>
    private List<LanGroupMessage> TrimLocked()
    {
        List<LanGroupMessage>? dropped = null;
        while (_entries.Count > Math.Max(1, maximumEntries))
        {
            var oldest = _entries[0];
            _entries.RemoveAt(0);
            _byId.Remove(oldest.MessageId);
            (dropped ??= []).Add(oldest);
        }

        return dropped ?? [];
    }
}

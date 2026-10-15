namespace FengSync.Lan;

/// <summary>
/// One thing said in the room: a sentence, or the announcement of a file somebody offered.
/// </summary>
/// <remarks>
/// <para>
/// A file entry is metadata and nothing else. The bytes stay where they were until a member asks for them,
/// because this room stores nothing: there is no attachment table, no cache, and no directory on this machine
/// that quietly holds copies of what other people sent. An entry says what the file is and who named it, and a
/// member that wants the bytes goes and fetches them from whoever will answer.
/// </para>
/// <para>
/// <see cref="LocalSeq"/> is this machine's own arrival number and means nothing to anybody else, which is why
/// a poller remembers one watermark <i>per peer</i> rather than one for the room. A shared number would have
/// needed somebody to hand it out, and this design has no member that owns the room.
/// </para>
/// </remarks>
public sealed class LanGroupMessage
{
    /// <summary>Minted by whoever spoke, and the only thing two members can agree a message is identical by.</summary>
    public required string MessageId { get; init; }

    /// <summary>Who said it. A display name is never trusted, so this is the key the log is indexed and filtered on.</summary>
    public required string AuthorDeviceId { get; init; }

    /// <summary>The name its own device claimed when it spoke, kept so a row reads correctly without a member list.</summary>
    public required string AuthorName { get; init; }

    /// <summary>Either <see cref="LanGroupKind.Text"/>, <see cref="LanGroupKind.File"/> or <see cref="LanGroupKind.Directory"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>This machine's arrival order, assigned here. It is what a peer's <c>since</c> keys on, on that peer's copy.</summary>
    public long LocalSeq { get; set; }

    public string? Text { get; init; }
    public string? FileName { get; init; }

    /// <summary>The path inside what was sent, so a folder arrives as a tree rather than as a pile of names.</summary>
    public string? RelativePath { get; init; }

    public long Bytes { get; init; }
    public bool IsDirectory { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Whether this machine can hand over the bytes behind this entry — the only honest answer to 「接收」 here.</summary>
    public bool HeldHere { get; set; }

    /// <summary>The absolute path this machine holds them at. Never goes on the wire: a peer has no business naming a local path.</summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// The entry as the wire shows it. <see cref="HeldHere"/> travels as a statement about <i>this</i> machine, so a
    /// member that pulls a copy from here never inherits a claim that it holds the bytes itself.
    /// </summary>
    public LanMessageWire ToWire() => new(
        MessageId, AuthorDeviceId, AuthorName, LocalSeq, Kind, Text, FileName, RelativePath,
        Bytes, IsDirectory, CreatedUtc, HeldHere);
}

/// <summary>The kinds a message may be. Anything else on the wire is refused, not guessed at.</summary>
public static class LanGroupKind
{
    public const string Text = "text";
    public const string File = "file";
    public const string Directory = "directory";

    public static bool IsKnown(string? kind) => kind is Text or File or Directory;
}

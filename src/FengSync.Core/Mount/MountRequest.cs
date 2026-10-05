namespace FengSync.Core.Mount;

/// <summary>
/// A mount request: where to mount, plus the options this particular mount should use.
/// </summary>
/// <remarks>
/// Deliberately a wrapper around <see cref="MountTarget"/> rather than extra members on it. <c>MountTarget</c>
/// is constructed by the picker and asserted by four existing test files; widening it would touch all of them
/// for no benefit, and an absent <see cref="Options"/> has to mean "the historical defaults" anyway, which is
/// exactly what a separate optional record says.
/// </remarks>
public sealed record MountRequest(MountTarget Target, MountOptionsSettings? Options = null);

/// <summary>
/// What a successful mount actually produced.
/// </summary>
/// <param name="MountPoint">
/// The mount point that now exists, which is <b>not</b> always the requested one: a UNC target comes back as the
/// drive letter Windows allocated, and Windows can substitute a letter for a directory target too.
/// </param>
/// <param name="DiskSpaceTotalSize">The size the endpoint reported, or <see langword="null"/> when it reported none.</param>
public sealed record MountResult(string MountPoint, long? DiskSpaceTotalSize);

/// <summary>
/// The one thing a restore needs from a mount service: the ability to start one mount.
/// </summary>
/// <remarks>
/// Narrower than <see cref="RcloneMountService"/> on purpose. A restore is a loop over remembered records, and
/// the only behaviour worth pinning in a test is what that loop does when one of them fails — which does not
/// need rclone, a mount point, or the WinFsp driver that the real service insists on before it will mount
/// anything. <see cref="RcloneMountService"/> implements this implicitly; the interface exists so the loop can
/// be driven by a stand-in.
/// </remarks>
public interface IMountStarter
{
    Task<MountResult> MountAsync(MountRequest request, CancellationToken ct = default);
}

using System.Collections.Frozen;

namespace FengSync.Lan;

/// <summary>One file of the built page, addressed by the path it is served under.</summary>
/// <param name="Path">The address, relative to the site root — <c>index.html</c>, <c>assets/index-abc.js</c>.</param>
public sealed record LanWebAsset(string Path, byte[] Bytes, string ContentType);

/// <summary>
/// The phone page, as the build embedded it.
/// </summary>
/// <remarks>
/// <para>
/// These bytes are resources inside this assembly, not files beside it. That is the one shape this repository can
/// ship without teaching the release manifest, the updater and the package test about a second kind of payload: the
/// publish directory gains nothing, so every hash it already computes stays exactly as meaningful as it was.
/// </para>
/// <para>
/// Loaded once, on first use, into a frozen dictionary keyed by the address the route will ask for. The alternative
/// — scanning the resource list on every request — would be a string comparison per name per asset per page load,
/// and a phone reloading a page on a bad Wi-Fi link does that more often than anybody would like.
/// </para>
/// <para>
/// A build that set <c>SkipWebBuild=true</c> embeds nothing. That is not an error here: <see cref="All"/> is empty,
/// the page route answers 503 with a sentence, and every other route keeps working — which is what a harness that
/// only wants the protocol needs.
/// </para>
/// <para>
/// The name a build produced carries whatever separator that build's machine uses, because MSBuild names a resource
/// from a file path; <see cref="Load"/> settles it to <c>/</c> so the key is the URL and not the build machine.
/// </para>
/// </remarks>
public static class LanWebAssets
{
    /// <summary>What every embedded name starts with. The rest is the URL path.</summary>
    private const string Prefix = "web/";

    /// <summary>
    /// The budget the whole page is held to.
    /// </summary>
    /// <remarks>
    /// The old page was one document under twenty kilobytes, and the number existed to keep it a page rather than an
    /// application. That promise is no longer kept by a byte count on one file — a framework is a framework — so it
    /// is kept by what the number is really for: everything the page needs is inside this assembly, it asks no other
    /// origin for anything, and it stays small enough to arrive over a phone's Wi-Fi in one breath. Measured, not
    /// invented: React and Tailwind together land around 271 KB, and this leaves room for a version bump without
    /// becoming a blank cheque.
    /// </remarks>
    public const int MaximumTotalBytes = 384 * 1024;

    /// <summary>The shell's own budget. It is a doctype, four metas and two references.</summary>
    public const int MaximumHtmlBytes = 8 * 1024;

    private static readonly Lazy<FrozenDictionary<string, LanWebAsset>> Loaded =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Everything the build embedded, keyed by the path it is served under.</summary>
    public static IReadOnlyDictionary<string, LanWebAsset> All => Loaded.Value;

    public static bool TryGet(string path, out LanWebAsset asset)
    {
        if (Loaded.Value.TryGetValue(path, out var found))
        {
            asset = found;
            return true;
        }

        asset = null!;
        return false;
    }

    /// <summary>The asset a route cannot do without, or a sentence saying which build produced this assembly.</summary>
    public static LanWebAsset Require(string path) => TryGet(path, out var asset)
        ? asset
        : throw new InvalidOperationException($"嵌入资源 web/{path} 不存在。不要用 -p:SkipWebBuild=true 构建发布的程序集。");

    private static FrozenDictionary<string, LanWebAsset> Load()
    {
        var assembly = typeof(LanWebAssets).Assembly;
        var files = new Dictionary<string, LanWebAsset>(StringComparer.Ordinal);

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            var path = name[Prefix.Length..].Replace('\\', '/');
            files[path] = new LanWebAsset(path, buffer.ToArray(), ContentTypeOf(path));
        }

        return files.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// The media type for one path.
    /// </summary>
    /// <remarks>
    /// The charset is part of the answer rather than left to the framework: a browser that guesses at a Chinese
    /// sentence in a script is a browser that guesses wrong. Fonts and images get no charset, because there are no
    /// characters in them to name one for.
    /// </remarks>
    public static string ContentTypeOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
}

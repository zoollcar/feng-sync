using System.IO.Compression;
using System.Text.Json;
using FengSync.Core.Scanning;

namespace FengSync.Core;

/// <summary>Versioned payload codec used inside the paired SQLite archives.</summary>
internal static class BaselinePayloadCodec
{
    public const int CurrentVersion = 3;
    public const int MinimumVersion = 2;
    private const int MaxEntries = 2_000_000;
    private const int MaxPathLength = 32_768;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;

    public static byte[] Encode(IReadOnlyList<BaselineEntry> entries)
    {
        Validate(entries);
        using var raw = new MemoryStream();
        using (var writer = new Utf8JsonWriter(raw))
        {
            writer.WriteStartArray();
            foreach (var entry in entries.OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(entry.Path);
                WriteSnapshot(writer, entry.Left);
                WriteSnapshot(writer, entry.Right);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
            raw.WriteTo(gzip);
        return compressed.ToArray();
    }

    public static IReadOnlyList<BaselineEntry> Decode(int version, byte[] payload)
    {
        if (version is < MinimumVersion or > CurrentVersion)
            throw new BaselineUnsupportedVersionException(version);
        var raw = Expand(payload);
        IReadOnlyList<BaselineEntry> entries;
        try
        {
            entries = version switch
            {
                2 => JsonSerializer.Deserialize<List<BaselineEntry>>(raw, new JsonSerializerOptions { MaxDepth = 8 }) ?? throw new InvalidDataException("同步状态负载为空。"),
                3 => DecodeV3(raw),
                _ => throw new BaselineUnsupportedVersionException(version)
            };
        }
        catch (JsonException ex) { throw new InvalidDataException($"sync.fengdb v{version} JSON 无效。", ex); }
        Validate(entries);
        return entries;
    }

    private static byte[] Expand(byte[] payload)
    {
        try
        {
            using var input = new MemoryStream(payload);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length + read > MaxExpandedBytes) throw new InvalidDataException("sync.fengdb 解压后超过安全限制。");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            throw new InvalidDataException("sync.fengdb 压缩负载无效。", ex);
        }
    }

    private static IReadOnlyList<BaselineEntry> DecodeV3(byte[] raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("v3 同步状态根节点无效。");
            var result = new List<BaselineEntry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (result.Count >= MaxEntries) throw new InvalidDataException("sync.fengdb 条目数超过安全限制。");
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3) throw new InvalidDataException("v3 同步状态条目无效。");
                var values = item.EnumerateArray().ToArray();
                var path = values[0].GetString() ?? throw new InvalidDataException("v3 同步状态路径为空。");
                result.Add(new(path, ReadSnapshot(path, values[1]), ReadSnapshot(path, values[2])));
            }
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException("v3 同步状态 JSON 无效。", ex); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        { throw new InvalidDataException("v3 同步状态字段类型无效。", ex); }
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, EntrySnapshot? snapshot)
    {
        if (snapshot is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        writer.WriteNumberValue((int)snapshot.Kind);
        WriteNullable(writer, snapshot.Fingerprint?.Size);
        WriteNullable(writer, snapshot.Fingerprint?.ModifiedUtc.UtcTicks);
        WriteNullable(writer, snapshot.Fingerprint?.Hash);
        WriteNullable(writer, snapshot.Identity?.StableObjectId);
        if (snapshot.Identity?.StrongDigest is { } digest) writer.WriteNumberValue((int)digest.Algorithm); else writer.WriteNullValue();
        WriteNullable(writer, snapshot.Identity?.StrongDigest?.Hex);
        WriteNullable(writer, snapshot.Identity?.ProviderToken);
        writer.WriteEndArray();
    }

    private static EntrySnapshot? ReadSnapshot(string path, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 8) throw new InvalidDataException("v3 快照结构无效。");
        var values = value.EnumerateArray().ToArray();
        var kindValue = values[0].GetInt32();
        if (!Enum.IsDefined(typeof(EntryKind), kindValue)) throw new InvalidDataException("v3 快照类型无效。");
        var kind = (EntryKind)kindValue;
        Fingerprint? fingerprint = null;
        var size = NullableInt64(values[1]);
        var ticks = NullableInt64(values[2]);
        var hash = NullableString(values[3]);
        if (kind == EntryKind.File)
        {
            if (size is null || ticks is null || size < 0) throw new InvalidDataException("v3 文件指纹无效。");
            DateTimeOffset modified;
            try { modified = new DateTimeOffset(ticks.Value, TimeSpan.Zero); }
            catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("v3 文件时间无效。", ex); }
            fingerprint = new(size.Value, modified, hash);
        }
        else if (size is not null || ticks is not null || hash is not null)
            throw new InvalidDataException("v3 目录不能包含文件指纹。");

        var stableId = NullableString(values[4]);
        var digestAlgorithm = NullableInt32(values[5]);
        var digestHex = NullableString(values[6]);
        var providerToken = NullableString(values[7]);
        ContentDigest? digest = null;
        if (digestAlgorithm is not null || digestHex is not null)
        {
            if (digestAlgorithm is null || digestHex is null || !Enum.IsDefined(typeof(HashAlgorithmId), digestAlgorithm.Value))
                throw new InvalidDataException("v3 强摘要无效。");
            digest = new((HashAlgorithmId)digestAlgorithm.Value, digestHex);
        }
        var identity = stableId is null && digest is null && providerToken is null ? null : new EntryIdentity(stableId, digest, providerToken);
        return new(path, kind, fingerprint, identity);
    }

    private static void Validate(IReadOnlyList<BaselineEntry> entries)
    {
        if (entries.Count > MaxEntries) throw new InvalidDataException("sync.fengdb 条目数超过安全限制。");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || entry.Path.Length > MaxPathLength || !paths.Add(entry.Path))
                throw new InvalidDataException("sync.fengdb 包含空、超长或重复路径。");
            ValidateSnapshot(entry.Path, entry.Left);
            ValidateSnapshot(entry.Path, entry.Right);
        }
    }

    private static void ValidateSnapshot(string path, EntrySnapshot? snapshot)
    {
        if (snapshot is null) return;
        if (!string.Equals(path, snapshot.Path, StringComparison.Ordinal)) throw new InvalidDataException("sync.fengdb 条目路径不一致。");
        if (!Enum.IsDefined(snapshot.Kind)) throw new InvalidDataException("sync.fengdb 条目类型无效。");
        if (snapshot.Kind == EntryKind.File && (snapshot.Fingerprint is null || snapshot.Fingerprint.Size < 0)) throw new InvalidDataException("sync.fengdb 文件指纹无效。");
        if (snapshot.Kind == EntryKind.Directory && snapshot.Fingerprint is not null) throw new InvalidDataException("sync.fengdb 目录指纹无效。");
        if (snapshot.Identity?.StrongDigest is { } digest && (!Enum.IsDefined(digest.Algorithm) || string.IsNullOrWhiteSpace(digest.Hex)))
            throw new InvalidDataException("sync.fengdb 强摘要无效。");
    }

    private static long? NullableInt64(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
    private static int? NullableInt32(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
    private static string? NullableString(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    private static void WriteNullable(Utf8JsonWriter writer, long? value) { if (value is null) writer.WriteNullValue(); else writer.WriteNumberValue(value.Value); }
    private static void WriteNullable(Utf8JsonWriter writer, string? value) { if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value); }
}

internal sealed class BaselineUnsupportedVersionException(int version) : IOException($"不支持的 sync.fengdb stream 版本：{version}。")
{
    public int Version { get; } = version;
}

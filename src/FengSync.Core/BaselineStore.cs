using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace FengSync.Core;
/// <summary>Stores only the last committed snapshot in a SQLite sync.fengdb on both endpoints.</summary>
public sealed class BaselineStore
{
    private const string Name = "sync.fengdb";
    public async Task<IReadOnlyList<BaselineEntry>?> LoadAsync(LocalEndpoint left, LocalEndpoint right, CancellationToken ct = default)
    {
        var a = left.PhysicalPath(Name); var b = right.PhysicalPath(Name);
        if (!File.Exists(a) && !File.Exists(b)) return null;
        if (!File.Exists(a) || !File.Exists(b)) throw new InvalidDataException("只有一侧存在 sync.fengdb；为避免误判删除，已停止比较。");
        await using var streamA = File.OpenRead(a); await using var streamB = File.OpenRead(b);
        var hashA = await SHA256.HashDataAsync(streamA, ct); var hashB = await SHA256.HashDataAsync(streamB, ct);
        if (!CryptographicOperations.FixedTimeEquals(hashA, hashB))
            throw new InvalidDataException("两侧 sync.fengdb 不一致；不能自动选择其中一份。");
        await using var connection = Open(a); await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand(); cmd.CommandText = "PRAGMA quick_check;";
        if (!String.Equals((string?)await cmd.ExecuteScalarAsync(ct), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("sync.fengdb 完整性校验失败。");
        cmd.CommandText = "SELECT path, lk, ls, lm, lh, rk, rs, rm, rh FROM entries;";
        var output = new List<BaselineEntry>(); await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) output.Add(new(reader.GetString(0), Entry(reader, 1), Entry(reader, 5)));
        return output;
    }
    public async Task CommitAsync(LocalEndpoint left, LocalEndpoint right, CancellationToken ct = default)
    {
        var l = left.Scan().ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase); var r = right.Scan().ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var temp = Path.Combine(Path.GetTempPath(), "fengsync-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using (var con = Open(temp))
            {
                await con.OpenAsync(ct); await using var cmd = con.CreateCommand();
                cmd.CommandText = "CREATE TABLE entries(path TEXT PRIMARY KEY, lk INTEGER, ls INTEGER, lm TEXT, lh TEXT, rk INTEGER, rs INTEGER, rm TEXT, rh TEXT);"; await cmd.ExecuteNonQueryAsync(ct);
                foreach (var path in l.Keys.Concat(r.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                { cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO entries VALUES ($p,$lk,$ls,$lm,$lh,$rk,$rs,$rm,$rh)"; cmd.Parameters.AddWithValue("$p", path); Bind(cmd, "l", l.GetValueOrDefault(path)); Bind(cmd, "r", r.GetValueOrDefault(path)); await cmd.ExecuteNonQueryAsync(ct); }
            }
            await Publish(temp, left.PhysicalPath(Name), ct); await Publish(temp, right.PhysicalPath(Name), ct);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    // The file is immediately copied and deleted during two-endpoint publication; pooling would keep a Windows handle alive.
    private static SqliteConnection Open(string path) => new($"Data Source={path};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
    private static EntrySnapshot? Entry(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null; var kind = (EntryKind)r.GetInt32(i); if (kind == EntryKind.Directory) return new(r.GetString(0), kind, null);
        return new(r.GetString(0), kind, new(r.GetInt64(i + 1), DateTimeOffset.Parse(r.GetString(i + 2)), r.IsDBNull(i + 3) ? null : r.GetString(i + 3)));
    }
    private static void Bind(SqliteCommand c, string side, EntrySnapshot? x)
    {
        c.Parameters.AddWithValue("$" + side + "k", x is null ? DBNull.Value : (int)x.Kind); c.Parameters.AddWithValue("$" + side + "s", x?.Fingerprint?.Size is long size ? size : DBNull.Value);
        c.Parameters.AddWithValue("$" + side + "m", x?.Fingerprint?.ModifiedUtc.ToString("O") ?? (object)DBNull.Value); c.Parameters.AddWithValue("$" + side + "h", x?.Fingerprint?.Hash ?? (object)DBNull.Value);
    }
    private static async Task Publish(string source, string destination, CancellationToken ct)
    { var temp = destination + ".fengsync-" + Guid.NewGuid().ToString("N"); await using (var s = File.OpenRead(source)) await using (var d = File.Create(temp)) await s.CopyToAsync(d, ct); File.Move(temp, destination, true); }
}

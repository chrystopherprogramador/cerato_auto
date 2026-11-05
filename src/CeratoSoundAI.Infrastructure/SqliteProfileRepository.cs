using System.Text.Json;
using System.Text.Json.Serialization;
using CeratoSoundAI.Domain;
using Microsoft.Data.Sqlite;

namespace CeratoSoundAI.Infrastructure.Persistence;

public sealed class SqliteProfileRepository : IProfileRepository
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly string _cs;
    private static int _pcl;

    public SqliteProfileRepository(string dbPath)
    {
        if (Interlocked.Exchange(ref _pcl, 1) == 0) SQLitePCL.Batteries_V2.Init();
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqliteConnection(_cs);
        await c.OpenAsync(ct);
        // WAL + FULL sync: committed adjustments must survive sudden power loss when the ignition is cut.
        await Exec(c, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;", ct);
        return c;
    }

    private static async Task Exec(SqliteConnection c, SqliteTransaction? t, string sql, CancellationToken ct, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t; cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        await Exec(c, null, @"
CREATE TABLE IF NOT EXISTS Profiles(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Json TEXT NOT NULL, Driver TEXT, HardwareId TEXT, UpdatedUtc TEXT, SchemaVersion INTEGER, SyncState TEXT, IsFavorite INTEGER, IsBuiltIn INTEGER);
CREATE TABLE IF NOT EXISTS History(Id INTEGER PRIMARY KEY AUTOINCREMENT, ProfileId TEXT NOT NULL, AtUtc TEXT NOT NULL, Note TEXT, Json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS IX_History_Profile ON History(ProfileId, Id DESC);
CREATE TABLE IF NOT EXISTS KeyValue(Key TEXT PRIMARY KEY, Value TEXT);
CREATE TABLE IF NOT EXISTS Logs(Id INTEGER PRIMARY KEY AUTOINCREMENT, AtUtc TEXT, Level TEXT, Message TEXT);", ct);
    }

    public async Task<IReadOnlyList<AudioProfile>> GetAllAsync(CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Json FROM Profiles ORDER BY IsFavorite DESC, IsBuiltIn DESC, Name";
        var list = new List<AudioProfile>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(JsonSerializer.Deserialize<AudioProfile>(r.GetString(0), Json)!);
        return list;
    }

    public async Task<AudioProfile?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        return await GetInternal(c, null, id, ct);
    }

    private static async Task<AudioProfile?> GetInternal(SqliteConnection c, SqliteTransaction? t, Guid id, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "SELECT Json FROM Profiles WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is string s ? JsonSerializer.Deserialize<AudioProfile>(s, Json) : null;
    }

    public async Task SaveAsync(AudioProfile p, string note, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var t = c.BeginTransaction();
        var prev = await GetInternal(c, t, p.Id, ct);
        if (prev != null)
            await Exec(c, t, "INSERT INTO History(ProfileId,AtUtc,Note,Json) VALUES($p,$a,$n,$j)", ct,
                ("$p", p.Id.ToString()), ("$a", DateTime.UtcNow.ToString("o")), ("$n", note), ("$j", JsonSerializer.Serialize(prev, Json)));
        await Exec(c, t, @"INSERT INTO Profiles(Id,Name,Json,Driver,HardwareId,UpdatedUtc,SchemaVersion,SyncState,IsFavorite,IsBuiltIn)
VALUES($id,$n,$j,$d,$h,$u,$s,$ss,$f,$b)
ON CONFLICT(Id) DO UPDATE SET Name=$n,Json=$j,Driver=$d,HardwareId=$h,UpdatedUtc=$u,SchemaVersion=$s,SyncState=$ss,IsFavorite=$f,IsBuiltIn=$b", ct,
            ("$id", p.Id.ToString()), ("$n", p.Name), ("$j", JsonSerializer.Serialize(p, Json)), ("$d", p.Driver), ("$h", p.HardwareId),
            ("$u", p.UpdatedUtc.ToString("o")), ("$s", p.SchemaVersion), ("$ss", p.Sync.ToString()), ("$f", p.IsFavorite ? 1 : 0), ("$b", p.IsBuiltIn ? 1 : 0));
        // keep the history bounded
        await Exec(c, t, "DELETE FROM History WHERE ProfileId=$p AND Id NOT IN (SELECT Id FROM History WHERE ProfileId=$p ORDER BY Id DESC LIMIT 200)", ct, ("$p", p.Id.ToString()));
        t.Commit();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var t = c.BeginTransaction();
        await Exec(c, t, "DELETE FROM Profiles WHERE Id=$i AND IsBuiltIn=0", ct, ("$i", id.ToString()));
        t.Commit();
    }

    public Task<IReadOnlyList<ProfileHistoryEntry>> GetHistoryAsync(Guid profileId, int max = 50, CancellationToken ct = default)
        => QueryHistory("WHERE ProfileId=$p", ("$p", profileId.ToString()), max, ct);

    public Task<IReadOnlyList<ProfileHistoryEntry>> GetAllHistoryAsync(int max = 100, CancellationToken ct = default)
        => QueryHistory("", ("$x", 0), max, ct);

    private async Task<IReadOnlyList<ProfileHistoryEntry>> QueryHistory(string where, (string, object) arg, int max, CancellationToken ct)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT Id,ProfileId,AtUtc,Note,Json FROM History {where} ORDER BY Id DESC LIMIT $max";
        if (where.Length > 0) cmd.Parameters.AddWithValue(arg.Item1, arg.Item2);
        cmd.Parameters.AddWithValue("$max", max);
        var list = new List<ProfileHistoryEntry>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ProfileHistoryEntry
            {
                Id = r.GetInt64(0), ProfileId = Guid.Parse(r.GetString(1)), AtUtc = DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Note = r.IsDBNull(3) ? "" : r.GetString(3), Snapshot = JsonSerializer.Deserialize<AudioProfile>(r.GetString(4), Json)!
            });
        return list;
    }

    private async Task<string?> GetKv(string key, CancellationToken ct)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Value FROM KeyValue WHERE Key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private async Task SetKv(string key, string value, CancellationToken ct)
    {
        using var c = await OpenAsync(ct);
        using var t = c.BeginTransaction();
        await Exec(c, t, "INSERT INTO KeyValue(Key,Value) VALUES($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=$v", ct, ("$k", key), ("$v", value));
        t.Commit();
    }

    public async Task<Guid?> GetActiveProfileIdAsync(CancellationToken ct = default)
        => Guid.TryParse(await GetKv("active", ct), out var g) ? g : null;

    public Task SetActiveProfileIdAsync(Guid id, CancellationToken ct = default) => SetKv("active", id.ToString(), ct);

    public async Task<AudioProfile?> GetLastStableAsync(CancellationToken ct = default)
    {
        var s = await GetKv("stable", ct);
        return s == null ? null : JsonSerializer.Deserialize<AudioProfile>(s, Json);
    }

    public Task SetLastStableAsync(AudioProfile profile, CancellationToken ct = default)
        => SetKv("stable", JsonSerializer.Serialize(profile, Json), ct);

    public async Task AddLogAsync(string level, string message, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        await Exec(c, null, "INSERT INTO Logs(AtUtc,Level,Message) VALUES($a,$l,$m); DELETE FROM Logs WHERE Id NOT IN (SELECT Id FROM Logs ORDER BY Id DESC LIMIT 500)", ct,
            ("$a", DateTime.UtcNow.ToString("o")), ("$l", level), ("$m", message));
    }

    public async Task<IReadOnlyList<string>> GetLogsAsync(int max = 100, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT AtUtc||' ['||Level||'] '||Message FROM Logs ORDER BY Id DESC LIMIT $m";
        cmd.Parameters.AddWithValue("$m", max);
        var l = new List<string>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(r.GetString(0));
        return l;
    }
}

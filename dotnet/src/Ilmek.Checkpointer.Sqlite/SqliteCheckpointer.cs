using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ilmek.Checkpointers.Sqlite;

public sealed record SqliteCheckpointerOptions
{
    /// <summary>Table-name prefix, so several apps can share one database file.</summary>
    public string TablePrefix { get; init; } = "ilmek";

    /// <summary>
    /// Write-ahead logging. On for a file database — it survives a hard kill
    /// better and lets a reader run while a run is writing. Ignored for in-memory.
    /// </summary>
    public bool Wal { get; init; } = true;
}

/// <summary>
/// A SQLite-backed <see cref="ICheckpointer"/> (MODEL.md §7) — durable threads in
/// a single file.
///
/// <para>This is the provider to reach for first: a parked interrupt survives a
/// process restart, a deploy, or a crash, and the whole store is a file you can
/// copy. Reach for Postgres when several processes must share the same
/// threads.</para>
///
/// <code>
/// using var cp = SqliteCheckpointer.Open("./agent.db");
/// var result = await graph.RunAsync(input, new RunOptions { ThreadId = "t1", Checkpointer = cp });
/// </code>
///
/// <para>Values crossing the file boundary are JSON (see
/// <see cref="JsonValue"/>): journal what serializes and re-resolve richer
/// objects from it, exactly as MODEL.md §5.4 requires.</para>
/// </summary>
public sealed class SqliteCheckpointer : ICheckpointer, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly bool _ownsConnection;
    private readonly string _checkpoints;
    private readonly string _journals;
    private readonly bool _wal;
    private bool _migrated;

    /// <summary>Use an existing, open connection. The caller keeps ownership of it.</summary>
    public SqliteCheckpointer(SqliteConnection connection, SqliteCheckpointerOptions? options = null)
        : this(connection, ownsConnection: false, options) { }

    private SqliteCheckpointer(SqliteConnection connection, bool ownsConnection, SqliteCheckpointerOptions? options)
    {
        var opts = options ?? new SqliteCheckpointerOptions();
        _db = connection;
        _ownsConnection = ownsConnection;
        _checkpoints = $"{opts.TablePrefix}_checkpoints";
        _journals = $"{opts.TablePrefix}_journals";
        _wal = opts.Wal;
    }

    /// <summary>
    /// Open a database at <paramref name="path"/> and migrate it.
    /// <c>":memory:"</c> gives an ephemeral store — handy for tests, though it
    /// defeats the point of a durable backend.
    /// </summary>
    public static SqliteCheckpointer Open(string path = ":memory:", SqliteCheckpointerOptions? options = null)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        var cp = new SqliteCheckpointer(connection, ownsConnection: true, options);
        cp.Migrate();
        return cp;
    }

    /// <summary>Create the two tables if absent. Idempotent; safe to call on every boot.</summary>
    public void Migrate()
    {
        if (_wal)
        {
            // An in-memory database has no WAL; SQLite reports that rather than
            // failing, and either way it must not stop the migration.
            try { Exec("PRAGMA journal_mode = WAL"); }
            catch (SqliteException) { /* not a file database */ }
        }

        Exec($"""
            CREATE TABLE IF NOT EXISTS {_checkpoints} (
              thread_id TEXT NOT NULL,
              id        TEXT NOT NULL,
              step      INTEGER NOT NULL,
              data      TEXT NOT NULL,
              PRIMARY KEY (thread_id, id)
            )
            """);

        Exec($"""
            CREATE TABLE IF NOT EXISTS {_journals} (
              task_id TEXT PRIMARY KEY,
              entries TEXT NOT NULL
            )
            """);

        _migrated = true;
    }

    private void EnsureMigrated()
    {
        if (_migrated) return;
        throw new InvalidOperationException(
            "SqliteCheckpointer: call Migrate() once before use (or build it with " +
            "SqliteCheckpointer.Open(), which migrates for you).");
    }

    // ── ICheckpointer ───────────────────────────────────────────────────────

    public Task PutAsync(Checkpoint checkpoint, CancellationToken ct = default)
    {
        EnsureMigrated();
        // The full checkpoint is the source of truth in `data`; thread_id/id/step
        // are columns only for the WHERE and ORDER BY below.
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_checkpoints} (thread_id, id, step, data)
            VALUES ($thread, $id, $step, $data)
            ON CONFLICT(thread_id, id) DO UPDATE SET step = excluded.step, data = excluded.data
            """;
        cmd.Parameters.AddWithValue("$thread", checkpoint.ThreadId);
        cmd.Parameters.AddWithValue("$id", checkpoint.Id);
        cmd.Parameters.AddWithValue("$step", checkpoint.Step);
        cmd.Parameters.AddWithValue("$data", JsonValue.Encode(checkpoint));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<Checkpoint?> GetAsync(string threadId, string? checkpointId = null, CancellationToken ct = default)
    {
        EnsureMigrated();
        using var cmd = _db.CreateCommand();

        if (checkpointId is not null)
        {
            cmd.CommandText = $"SELECT data FROM {_checkpoints} WHERE thread_id = $thread AND id = $id";
            cmd.Parameters.AddWithValue("$id", checkpointId);
        }
        else
        {
            // Ids are lexically monotonic (MODEL.md §7), so max id = latest.
            cmd.CommandText =
                $"SELECT data FROM {_checkpoints} WHERE thread_id = $thread ORDER BY id DESC LIMIT 1";
        }
        cmd.Parameters.AddWithValue("$thread", threadId);

        var json = cmd.ExecuteScalar() as string;
        return Task.FromResult(json is null ? null : DecodeCheckpoint(json));
    }

    public Task<IReadOnlyList<Checkpoint>> ListAsync(string threadId, int? limit = null, CancellationToken ct = default)
    {
        EnsureMigrated();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT data FROM {_checkpoints} WHERE thread_id = $thread ORDER BY id DESC"
                          + (limit is null ? "" : " LIMIT $limit");
        cmd.Parameters.AddWithValue("$thread", threadId);
        if (limit is not null) cmd.Parameters.AddWithValue("$limit", limit.Value);

        var result = new List<Checkpoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(DecodeCheckpoint(reader.GetString(0)));
        return Task.FromResult<IReadOnlyList<Checkpoint>>(result);
    }

    public Task PutJournalAsync(string taskId, Journal journal, CancellationToken ct = default)
    {
        EnsureMigrated();
        var entries = journal.Dump()
            .Select(kv => new Dictionary<string, object?>
            {
                ["Key"] = kv.Key,
                ["Done"] = kv.Value.Done,
                ["Value"] = kv.Value.Value,
                ["Payload"] = kv.Value.Payload,
            })
            .ToList();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_journals} (task_id, entries)
            VALUES ($task, $entries)
            ON CONFLICT(task_id) DO UPDATE SET entries = excluded.entries
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$entries", JsonValue.Encode(entries));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<Journal> GetJournalAsync(string taskId, CancellationToken ct = default)
    {
        EnsureMigrated();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT entries FROM {_journals} WHERE task_id = $task";
        cmd.Parameters.AddWithValue("$task", taskId);

        if (cmd.ExecuteScalar() is not string json) return Task.FromResult(new Journal());

        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.EnumerateArray().Select(e =>
        {
            var key = JsonValue.StringOrNull(e, "Key") ?? "";
            var done = e.TryGetProperty("Done", out var d) && d.ValueKind == JsonValueKind.True;
            var value = e.TryGetProperty("Value", out var v) ? JsonValue.Decode(v) : null;
            var payload = e.TryGetProperty("Payload", out var p) ? JsonValue.Decode(p) : null;
            return new KeyValuePair<string, JournalEntry>(
                key, done ? JournalEntry.AsDone(value) : JournalEntry.AsPending(payload));
        });

        return Task.FromResult(Journal.Load(entries));
    }

    public Task DropJournalAsync(string taskId, CancellationToken ct = default)
    {
        EnsureMigrated();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_journals} WHERE task_id = $task";
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        EnsureMigrated();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_checkpoints} WHERE thread_id = $thread";
        cmd.Parameters.AddWithValue("$thread", threadId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Rebuild a checkpoint from its JSON. Read field by field rather than
    /// letting the serializer construct the record, so every loosely-typed value
    /// goes through <see cref="JsonValue.Decode"/> and comes back as plain CLR
    /// data instead of a JsonElement.
    /// </summary>
    private static Checkpoint DecodeCheckpoint(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var next = root.GetProperty("Next").EnumerateArray()
            .Select(e => new ScheduledTask(
                e.GetProperty("Node").GetString()!,
                e.GetProperty("TaskKey").GetString()!,
                e.GetProperty("IsSend").GetBoolean(),
                e.TryGetProperty("Input", out var input) ? JsonValue.Decode(input) : null))
            .ToList();

        var pending = root.GetProperty("Pending").EnumerateArray()
            .Select(e => new Pending(
                e.GetProperty("Id").GetString()!,
                e.GetProperty("TaskId").GetString()!,
                e.GetProperty("Node").GetString()!,
                e.GetProperty("Key").GetString()!,
                e.TryGetProperty("Payload", out var payload) ? JsonValue.Decode(payload) : null))
            .ToList();

        return new Checkpoint(
            root.GetProperty("Id").GetString()!,
            JsonValue.StringOrNull(root, "ParentId"),
            JsonValue.StringOrNull(root, "PlanId"),
            root.GetProperty("ThreadId").GetString()!,
            JsonValue.DecodeObject(root.GetProperty("Channels")),
            next,
            pending,
            root.GetProperty("Step").GetInt32(),
            root.GetProperty("Ts").GetInt64());
    }

    /// <summary>Closes the connection when this instance opened it.</summary>
    public void Dispose()
    {
        if (_ownsConnection) _db.Dispose();
    }
}

using System.Security.Cryptography;

namespace Ilmek;

/// <summary>
/// One task scheduled for the next superstep (MODEL.md §4, §14).
///
/// A plain task reads the checkpoint's channel state and its <see cref="TaskKey"/>
/// is just the node name — so its task id, and therefore its journal, is
/// unchanged from a graph with no fan-out. A send task reads <see cref="Input"/>
/// as its whole state and gets a disambiguated key (<c>node#n</c>) so N sends to
/// one node never share a journal.
/// </summary>
public sealed record ScheduledTask(string Node, string TaskKey, bool IsSend, object? Input = null);

/// <summary>An interrupt a thread is parked on (MODEL.md §7).</summary>
/// <param name="Id">
/// Thread-scoped handle — <c>"node:key"</c>. This is what a keyed resume answers
/// by. <paramref name="Key"/> alone is NOT enough: it is unique only within its
/// own task, so two nodes pausing in the same superstep both produce
/// <c>interrupt#0</c> and a map keyed by it would silently drop one answer.
/// </param>
public sealed record Pending(string Id, string TaskId, string Node, string Key, object? Payload);

/// <summary>
/// The per-thread record of channel values plus what runs next (MODEL.md §7).
///
/// Every checkpoint names its parent, so a thread is a **tree**, not a line:
/// resuming from a non-latest checkpoint forks a branch.
/// </summary>
/// <param name="PlanId">
/// The checkpoint <c>Next</c> was planned from. Task ids derive from it, so it
/// MUST survive an interrupt/resume cycle unchanged — otherwise a replayed task
/// looks up a journal that does not exist and re-runs every side effect it
/// already performed.
/// </param>
public sealed record Checkpoint(
    string Id,
    string? ParentId,
    string? PlanId,
    string ThreadId,
    IReadOnlyDictionary<string, object?> Channels,
    IReadOnlyList<ScheduledTask> Next,
    IReadOnlyList<Pending> Pending,
    int Step,
    long Ts)
{
    public bool IsInterrupted => Pending.Count > 0;

    // Ids sort lexically, so a backend can range-scan a thread's history without a
    // side index. The counter breaks ties: two checkpoints can land in the same
    // millisecond.
    private static int _seq;

    public static string GenerateId()
    {
        var micros = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000).ToString("D20");
        var tiebreak = (Interlocked.Increment(ref _seq) % 1_000_000).ToString("D6");
        var rand = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        return $"ckpt-{micros}-{tiebreak}-{rand}";
    }
}

/// <summary>
/// The memory port (MODEL.md §7) — one interface, many backends.
///
/// ilmek checkpoints are ilmek's own; they are not a chat server's transcript.
/// </summary>
public interface ICheckpointer
{
    Task PutAsync(Checkpoint checkpoint, CancellationToken ct = default);

    /// <param name="checkpointId">Null means "latest".</param>
    Task<Checkpoint?> GetAsync(string threadId, string? checkpointId = null, CancellationToken ct = default);

    Task<IReadOnlyList<Checkpoint>> ListAsync(string threadId, int? limit = null, CancellationToken ct = default);

    Task PutJournalAsync(string taskId, Journal journal, CancellationToken ct = default);

    Task<Journal> GetJournalAsync(string taskId, CancellationToken ct = default);

    Task DropJournalAsync(string taskId, CancellationToken ct = default);

    Task DeleteThreadAsync(string threadId, CancellationToken ct = default);
}

/// <summary>
/// Reference checkpointer backed by dictionaries (MODEL.md §7).
///
/// Fine for dev, tests and single-process work; use a durable backend when a
/// restart must not lose parked interrupts.
/// </summary>
public sealed class InMemoryCheckpointer : ICheckpointer
{
    private readonly Dictionary<string, Dictionary<string, Checkpoint>> _checkpoints = new();
    private readonly Dictionary<string, IReadOnlyList<KeyValuePair<string, JournalEntry>>> _journals = new();
    private readonly Lock _gate = new();

    public Task PutAsync(Checkpoint checkpoint, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_checkpoints.TryGetValue(checkpoint.ThreadId, out var thread))
            {
                thread = new Dictionary<string, Checkpoint>();
                _checkpoints[checkpoint.ThreadId] = thread;
            }
            thread[checkpoint.Id] = checkpoint;
        }
        return Task.CompletedTask;
    }

    public Task<Checkpoint?> GetAsync(string threadId, string? checkpointId = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_checkpoints.TryGetValue(threadId, out var thread)) return Task.FromResult<Checkpoint?>(null);
            if (checkpointId is not null)
                return Task.FromResult(thread.TryGetValue(checkpointId, out var byId) ? byId : null);

            // Ids are lexically monotonic, so the max id IS the latest.
            Checkpoint? latest = null;
            foreach (var ckpt in thread.Values)
                if (latest is null || string.CompareOrdinal(ckpt.Id, latest.Id) > 0) latest = ckpt;
            return Task.FromResult(latest);
        }
    }

    public Task<IReadOnlyList<Checkpoint>> ListAsync(string threadId, int? limit = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_checkpoints.TryGetValue(threadId, out var thread))
                return Task.FromResult<IReadOnlyList<Checkpoint>>(Array.Empty<Checkpoint>());

            var all = thread.Values
                .OrderByDescending(c => c.Id, StringComparer.Ordinal)
                .ToList();
            IReadOnlyList<Checkpoint> result = limit is null ? all : all.Take(limit.Value).ToList();
            return Task.FromResult(result);
        }
    }

    public Task PutJournalAsync(string taskId, Journal journal, CancellationToken ct = default)
    {
        lock (_gate) _journals[taskId] = journal.Dump();
        return Task.CompletedTask;
    }

    public Task<Journal> GetJournalAsync(string taskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_journals.TryGetValue(taskId, out var dump) ? Journal.Load(dump) : new Journal());
        }
    }

    public Task DropJournalAsync(string taskId, CancellationToken ct = default)
    {
        lock (_gate) _journals.Remove(taskId);
        return Task.CompletedTask;
    }

    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        lock (_gate) _checkpoints.Remove(threadId);
        return Task.CompletedTask;
    }
}

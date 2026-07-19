namespace Ilmek;

/// <summary>
/// A journal entry (MODEL.md §5).
///
/// An interrupt is a step whose value comes from a human (MODEL.md §6), so both
/// live in this one table: <c>Done</c> covers a completed step *and* an answered
/// interrupt; <c>Pending</c> is an interrupt still waiting on a person.
/// </summary>
public sealed record JournalEntry(bool Done, object? Value, object? Payload)
{
    public static JournalEntry AsDone(object? value) => new(true, value, null);
    public static JournalEntry AsPending(object? payload) => new(false, null, payload);
}

/// <summary>
/// The per-task record of completed steps and interrupt answers (MODEL.md §5).
///
/// This is the reason ilmek is not a LangGraph clone. A pure-replay engine
/// re-executes a node from the top on resume, so every side effect before the
/// pause happens twice. Here each effect is wrapped in a <c>ctx.StepAsync</c>
/// whose result is journaled — on replay the step returns the recorded value and
/// the function is never called again.
///
/// <para>A journal is scoped to a task and is dropped once that task's update is
/// reduced. It is replay memory, not history.</para>
/// </summary>
public sealed class Journal
{
    private readonly Dictionary<string, JournalEntry> _entries;
    private readonly List<string> _order;

    public Journal()
    {
        _entries = new Dictionary<string, JournalEntry>();
        _order = new List<string>();
    }

    private Journal(Dictionary<string, JournalEntry> entries, List<string> order)
    {
        _entries = entries;
        _order = order;
    }

    /// <summary>Rebuild from the persisted, order-significant form.</summary>
    public static Journal Load(IEnumerable<KeyValuePair<string, JournalEntry>> dump)
    {
        var journal = new Journal();
        foreach (var (key, entry) in dump) journal.Put(key, entry);
        return journal;
    }

    /// <summary>The persisted form: entries in journaled order.</summary>
    public IReadOnlyList<KeyValuePair<string, JournalEntry>> Dump() =>
        _order.Select(k => new KeyValuePair<string, JournalEntry>(k, _entries[k])).ToList();

    public IReadOnlyList<string> Keys => _order;

    public JournalEntry? Fetch(string key) => _entries.TryGetValue(key, out var e) ? e : null;

    public void Put(string key, JournalEntry entry)
    {
        if (!_entries.ContainsKey(key)) _order.Add(key);
        _entries[key] = entry;
    }

    public void PutDone(string key, object? value) => Put(key, JournalEntry.AsDone(value));

    public void PutPending(string key, object? payload) => Put(key, JournalEntry.AsPending(payload));

    /// <summary>Resolve a pending interrupt with the human's answer.</summary>
    public (bool Ok, string Reason) Answer(string key, object? value)
    {
        if (!_entries.TryGetValue(key, out var entry)) return (false, "unknown_key");
        if (entry.Done) return (false, "already_answered");
        Put(key, JournalEntry.AsDone(value));
        return (true, "");
    }

    /// <summary>Every interrupt still waiting on a human, in journaled order.</summary>
    public IReadOnlyList<(string Key, object? Payload)> Pending() =>
        _order.Where(k => !_entries[k].Done).Select(k => (k, _entries[k].Payload)).ToList();

    public Journal Clone() => new(new Dictionary<string, JournalEntry>(_entries), new List<string>(_order));
}

/// <summary>
/// Per-task bookkeeping around a journal: occurrence counters for key suffixing
/// and the trace strict mode checks (MODEL.md §5.4, §5.5).
///
/// A fresh instance per attempt resets the counters and trace while the
/// underlying <see cref="Journal"/> persists — which is what makes a retry
/// replay completed steps instead of re-running them (MODEL.md §16).
/// </summary>
public sealed class TaskJournal
{
    public Journal Journal { get; }
    private readonly Dictionary<string, int> _counters = new();
    private readonly List<string> _observed = new();

    public TaskJournal(Journal journal) => Journal = journal;

    /// <summary>
    /// Turn a caller-supplied base key into the full journal key by appending
    /// this pass's occurrence count: <c>"charge"</c> → <c>charge#0</c>,
    /// <c>charge#1</c>, …
    ///
    /// <para>Suffixing is uniform so the mapping depends only on how many times
    /// *this* base has been requested in *this* pass. A node sees identical state
    /// on every pass, so the sequence is stable.</para>
    /// </summary>
    public string ResolveKey(string baseKey)
    {
        var n = _counters.TryGetValue(baseKey, out var count) ? count : 0;
        _counters[baseKey] = n + 1;
        var full = $"{baseKey}#{n}";
        _observed.Add(full);
        return full;
    }

    /// <summary>
    /// Strict-mode check (MODEL.md §5.5): every key the journal holds must have
    /// been requested again on this pass. A key that vanishes means the node took
    /// a different path than before — a side effect escaped a step.
    /// </summary>
    public void CheckDeterminism(string node)
    {
        var seen = new HashSet<string>(_observed);
        var missing = Journal.Keys.Where(k => !seen.Contains(k)).ToList();
        if (missing.Count == 0) return;

        throw new NondeterminismException(
            $"node \"{node}\" replayed without requesting journaled step(s) [{string.Join(", ", missing)}].\n\n" +
            "The node body is not deterministic modulo steps: given the same state and journal it took a " +
            "different path. Wrap every side effect and every nondeterministic read (clock, RNG, guid, " +
            "network) in ctx.StepAsync — see MODEL.md §5.2.");
    }
}

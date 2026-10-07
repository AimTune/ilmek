using Ilmek;

namespace Ilmek.Tests;

/// <summary>Journal and TaskJournal at the unit level (MODEL.md §5), mirroring ts/packages/core/test/journal.test.ts.</summary>
public class JournalTests
{
    [Fact(DisplayName = "a fresh journal is empty")]
    public void FreshIsEmpty()
    {
        var j = new Journal();
        Assert.Empty(j.Keys);
        Assert.Empty(j.Dump());
        Assert.Empty(j.Pending());
        Assert.Null(j.Fetch("anything"));
    }

    [Fact(DisplayName = "PutDone records a value, including null, and overwrites without reordering")]
    public void PutDoneRecordsAndOverwrites()
    {
        var j = new Journal();
        j.PutDone("a#0", null);
        j.PutDone("b#0", "B");
        j.PutDone("a#0", "again");

        Assert.Equal(new[] { "a#0", "b#0" }, j.Keys);
        Assert.Equal("again", j.Fetch("a#0")!.Value);
        Assert.True(j.Fetch("a#0")!.Done);
    }

    [Fact(DisplayName = "Pending lists unanswered interrupts in journaled order")]
    public void PendingInOrder()
    {
        var j = new Journal();
        j.PutPending("second#0", "p2");
        j.PutDone("step#0", 1L);
        j.PutPending("first#0", "p1");

        Assert.Equal(new[] { ("second#0", (object?)"p2"), ("first#0", (object?)"p1") }, j.Pending());
    }

    [Fact(DisplayName = "Answer resolves a pending interrupt into a done entry, in place")]
    public void AnswerResolves()
    {
        var j = new Journal();
        j.PutPending("interrupt#0", "q?");
        j.PutDone("after#0", "x");

        var (ok, reason) = j.Answer("interrupt#0", "yes");

        Assert.True(ok);
        Assert.Equal("", reason);
        Assert.Equal("yes", j.Fetch("interrupt#0")!.Value);
        Assert.Empty(j.Pending());
        Assert.Equal(new[] { "interrupt#0", "after#0" }, j.Keys); // not reordered
    }

    [Fact(DisplayName = "Answer refuses an unknown key and a second answer, leaving the entry intact")]
    public void AnswerRefusals()
    {
        var j = new Journal();
        Assert.Equal((false, "unknown_key"), j.Answer("nope#0", 1L));

        j.PutPending("interrupt#0", "q?");
        j.Answer("interrupt#0", "first");
        Assert.Equal((false, "already_answered"), j.Answer("interrupt#0", "second"));
        Assert.Equal("first", j.Fetch("interrupt#0")!.Value);

        j.PutDone("step#0", "v");
        Assert.Equal((false, "already_answered"), j.Answer("step#0", "hijack")); // a step is never answerable
        Assert.Equal("v", j.Fetch("step#0")!.Value);
    }

    [Fact(DisplayName = "Dump/Load round-trips entries, order and status; the loaded journal is independent")]
    public void DumpLoadRoundTrip()
    {
        var j = new Journal();
        j.PutDone("z#0", "Z");
        j.PutPending("interrupt#0", "q");
        j.PutDone("a#0", "A");

        var loaded = Journal.Load(j.Dump());
        Assert.Equal(j.Keys, loaded.Keys);
        Assert.Equal(j.Dump(), loaded.Dump());

        loaded.PutDone("new#0", 1L);
        Assert.DoesNotContain("new#0", j.Keys);
        Assert.Empty(Journal.Load(Array.Empty<KeyValuePair<string, JournalEntry>>()).Keys);
    }

    [Fact(DisplayName = "Clone is a deep-enough copy: writes to either side do not leak")]
    public void CloneIsIndependent()
    {
        var j = new Journal();
        j.PutPending("interrupt#0", "q");
        var clone = j.Clone();

        clone.Answer("interrupt#0", "yes");
        j.PutDone("other#0", 1L);

        Assert.False(j.Fetch("interrupt#0")!.Done);
        Assert.Null(clone.Fetch("other#0"));
    }

    // ── TaskJournal ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "ResolveKey suffixes uniformly from zero and counts per base key")]
    public void ResolveKeyCounts()
    {
        var tj = new TaskJournal(new Journal());
        Assert.Equal("charge#0", tj.ResolveKey("charge"));
        Assert.Equal("refund#0", tj.ResolveKey("refund"));
        Assert.Equal("charge#1", tj.ResolveKey("charge"));
        Assert.Equal("charge#2", tj.ResolveKey("charge"));
    }

    [Fact(DisplayName = "a base key that already looks suffixed is suffixed again, never confused with another occurrence")]
    public void SuffixedLookingKey()
    {
        var tj = new TaskJournal(new Journal());
        Assert.Equal("charge#0#0", tj.ResolveKey("charge#0"));
        Assert.Equal("charge#0", tj.ResolveKey("charge"));
    }

    [Fact(DisplayName = "counters are per pass: a fresh TaskJournal over the same journal restarts the sequence")]
    public void CountersPerPass()
    {
        var journal = new Journal();
        new TaskJournal(journal).ResolveKey("x");
        Assert.Equal("x#0", new TaskJournal(journal).ResolveKey("x"));
    }

    [Fact(DisplayName = "CheckDeterminism passes on an empty journal and when the replay requests more keys")]
    public void DeterminismPasses()
    {
        new TaskJournal(new Journal()).CheckDeterminism("n");

        var journal = new Journal();
        journal.PutDone("a#0", 1L);
        var tj = new TaskJournal(journal);
        tj.ResolveKey("a");
        tj.ResolveKey("b"); // new work beyond the journal is fine
        tj.CheckDeterminism("n");
    }

    [Fact(DisplayName = "CheckDeterminism names the node and every key the replay skipped")]
    public void DeterminismNamesMissing()
    {
        var journal = new Journal();
        journal.PutDone("a#0", 1L);
        journal.PutDone("b#0", 2L);
        journal.PutPending("interrupt#0", null);
        var tj = new TaskJournal(journal);
        tj.ResolveKey("a");

        var ex = Assert.Throws<NondeterminismException>(() => tj.CheckDeterminism("payer"));
        Assert.Contains("\"payer\"", ex.Message);
        Assert.Contains("b#0", ex.Message);
        Assert.Contains("interrupt#0", ex.Message);
        Assert.DoesNotContain("a#0,", ex.Message);
    }

    [Fact(DisplayName = "a shifted ordinal counts as missing: charge#1 journaled, only charge#0 requested")]
    public void ShiftedOrdinal()
    {
        var journal = new Journal();
        journal.PutDone("charge#0", 1L);
        journal.PutDone("charge#1", 2L);
        var tj = new TaskJournal(journal);
        tj.ResolveKey("charge");

        var ex = Assert.Throws<NondeterminismException>(() => tj.CheckDeterminism("n"));
        Assert.Contains("charge#1", ex.Message);
    }

    [Fact(DisplayName = "InterruptSignalException carries key and payload and is recognised by IsInterrupt")]
    public void InterruptSignalShape()
    {
        var signal = new InterruptSignalException("interrupt#0", "q?");
        Assert.Equal("interrupt#0", signal.Key);
        Assert.Equal("q?", signal.Payload);
        Assert.Contains("interrupt#0", signal.Message);
        Assert.True(InterruptSignalException.IsInterrupt(signal));
        Assert.False(InterruptSignalException.IsInterrupt(new InvalidOperationException()));
    }
}

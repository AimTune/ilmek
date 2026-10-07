using Ilmek;

namespace Ilmek.Tests.Contract;

/// <summary>
/// The memory port's contract (MODEL.md §7), run against every backend.
///
/// This file is compiled into both Ilmek.Core.Tests (InMemory) and
/// Ilmek.Checkpointer.Sqlite.Tests (SQLite) — one list of claims, so the
/// backends cannot drift apart. Values are compared by content, since a durable
/// backend hands back decoded JSON rather than the instances that went in.
/// </summary>
public abstract class CheckpointerContract
{
    /// <summary>A fresh, empty checkpointer for one test.</summary>
    protected abstract ICheckpointer Create();

    private static Checkpoint Ckpt(string threadId, string? parent = null, int step = 0,
        IReadOnlyDictionary<string, object?>? channels = null, string? id = null,
        IReadOnlyList<Pending>? pending = null) =>
        new(id ?? Checkpoint.GenerateId(), parent, parent, threadId,
            channels ?? new Dictionary<string, object?>(), Array.Empty<ScheduledTask>(),
            pending ?? Array.Empty<Pending>(), step, 1752570000000);

    // ── checkpoints ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "contract: get and list on an unknown thread return null and empty")]
    public async Task UnknownThread()
    {
        var cp = Create();
        Assert.Null(await cp.GetAsync("nobody"));
        Assert.Null(await cp.GetAsync("nobody", "ckpt-x"));
        Assert.Empty(await cp.ListAsync("nobody"));
    }

    [Fact(DisplayName = "contract: get with an unknown id returns null rather than falling back to latest")]
    public async Task UnknownCheckpointId()
    {
        var cp = Create();
        await cp.PutAsync(Ckpt("t"));
        Assert.Null(await cp.GetAsync("t", "ckpt-does-not-exist"));
    }

    [Fact(DisplayName = "contract: latest is the max id, not the last put")]
    public async Task LatestIsMaxId()
    {
        var cp = Create();
        var older = Ckpt("t", step: 1);
        var newer = Ckpt("t", step: 2);
        await cp.PutAsync(newer);
        await cp.PutAsync(older); // put last, but sorts first

        Assert.Equal(newer.Id, (await cp.GetAsync("t"))!.Id);
        Assert.Equal(older.Id, (await cp.GetAsync("t", older.Id))!.Id);
    }

    [Fact(DisplayName = "contract: putting the same id twice replaces it")]
    public async Task PutSameIdReplaces()
    {
        var cp = Create();
        var id = Checkpoint.GenerateId();
        await cp.PutAsync(Ckpt("t", step: 1, id: id, channels: new Dictionary<string, object?> { ["v"] = "first" }));
        await cp.PutAsync(Ckpt("t", step: 2, id: id, channels: new Dictionary<string, object?> { ["v"] = "second" }));

        var all = await cp.ListAsync("t");
        var only = Assert.Single(all);
        Assert.Equal(2, only.Step);
        Assert.Equal("second", only.Channels["v"]);
    }

    [Fact(DisplayName = "contract: threads are isolated, even with the same checkpoint id")]
    public async Task ThreadsIsolated()
    {
        var cp = Create();
        var id = Checkpoint.GenerateId();
        await cp.PutAsync(Ckpt("a", id: id, channels: new Dictionary<string, object?> { ["who"] = "a" }));
        await cp.PutAsync(Ckpt("b", id: id, channels: new Dictionary<string, object?> { ["who"] = "b" }));

        Assert.Equal("a", (await cp.GetAsync("a", id))!.Channels["who"]);
        Assert.Equal("b", (await cp.GetAsync("b", id))!.Channels["who"]);
        Assert.Single(await cp.ListAsync("a"));
    }

    [Fact(DisplayName = "contract: list is newest-first; limit 0 is empty, a limit past the count returns all")]
    public async Task ListOrderAndLimits()
    {
        var cp = Create();
        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var c = Ckpt("t", parent: ids.LastOrDefault(), step: i);
            ids.Add(c.Id);
            await cp.PutAsync(c);
        }

        var all = await cp.ListAsync("t");
        Assert.Equal(Enumerable.Reverse(ids), all.Select(c => c.Id));
        Assert.Equal(ids.AsEnumerable().Reverse().Take(2), (await cp.ListAsync("t", 2)).Select(c => c.Id));
        Assert.Empty(await cp.ListAsync("t", 0));
        Assert.Equal(4, (await cp.ListAsync("t", 100)).Count);
    }

    [Fact(DisplayName = "contract: a thread is a tree — two children may share one parent")]
    public async Task ThreadIsATree()
    {
        var cp = Create();
        var root = Ckpt("t");
        var left = Ckpt("t", parent: root.Id, step: 1);
        var right = Ckpt("t", parent: root.Id, step: 1);
        foreach (var c in new[] { root, left, right }) await cp.PutAsync(c);

        var children = (await cp.ListAsync("t")).Where(c => c.ParentId == root.Id).Select(c => c.Id).ToList();
        Assert.Equal(new[] { left.Id, right.Id }.OrderBy(x => x, StringComparer.Ordinal),
            children.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "contract: a checkpoint round-trips its pending interrupts, plan id and send inputs")]
    public async Task CheckpointRoundTripsFields()
    {
        var cp = Create();
        var parent = Checkpoint.GenerateId();
        var c = new Checkpoint(Checkpoint.GenerateId(), parent, "ckpt-plan", "t",
            new Dictionary<string, object?> { ["n"] = 3L, ["flag"] = true, ["none"] = null },
            new[]
            {
                new ScheduledTask("a", "a", false),
                new ScheduledTask("w", "w#0", true, new Dictionary<string, object?> { ["id"] = "x" }),
            },
            new[] { new Pending("a:interrupt#0", "t:ckpt-plan:a", "a", "interrupt#0", "q?") },
            7, 42);
        await cp.PutAsync(c);

        var back = (await cp.GetAsync("t"))!;
        Assert.Equal(parent, back.ParentId);
        Assert.Equal("ckpt-plan", back.PlanId);
        Assert.Equal(7, back.Step);
        Assert.Equal(42, back.Ts);
        Assert.True(back.IsInterrupted);
        Assert.Equal(3L, back.Channels["n"]);
        Assert.Equal(true, back.Channels["flag"]);
        Assert.Null(back.Channels["none"]);
        var p = Assert.Single(back.Pending);
        Assert.Equal(("a:interrupt#0", "t:ckpt-plan:a", "a", "interrupt#0", (object?)"q?"),
            (p.Id, p.TaskId, p.Node, p.Key, p.Payload));
        Assert.Equal(new[] { ("a", "a", false), ("w", "w#0", true) },
            back.Next.Select(t => (t.Node, t.TaskKey, t.IsSend)));
        Assert.Equal("x", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(back.Next[1].Input)["id"]);
    }

    [Fact(DisplayName = "contract: unicode — emoji, CJK, RTL and an embedded NUL — round-trips exactly")]
    public async Task UnicodeRoundTrips()
    {
        var cp = Create();
        const string text = "emoji 🧶👩🏽‍💻 · CJK 编织针 · RTL שלום مرحبا · NUL[\0] · tab\t · quote\" · backslash\\";
        await cp.PutAsync(Ckpt("t-ü-🧶", channels: new Dictionary<string, object?> { ["s"] = text, [text] = "key" }));

        var back = (await cp.GetAsync("t-ü-🧶"))!;
        Assert.Equal(text, back.Channels["s"]);
        Assert.Equal("key", back.Channels[text]);

        var journal = new Journal();
        journal.PutDone("step:é🧶#0", text);
        await cp.PutJournalAsync("task-🧶", journal);
        Assert.Equal(text, (await cp.GetJournalAsync("task-🧶")).Fetch("step:é🧶#0")!.Value);
    }

    [Fact(DisplayName = "contract: a multi-megabyte channel value round-trips intact")]
    public async Task HugePayloadRoundTrips()
    {
        var cp = Create();
        var huge = string.Create(4 * 1024 * 1024, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)('a' + i % 26);
        });
        await cp.PutAsync(Ckpt("t", channels: new Dictionary<string, object?> { ["blob"] = huge }));

        var back = (string)(await cp.GetAsync("t"))!.Channels["blob"]!;
        Assert.Equal(huge.Length, back.Length);
        Assert.Equal(huge, back);
    }

    [Fact(DisplayName = "contract: DeleteThread drops that thread only; an unknown thread is a no-op")]
    public async Task DeleteThreadIsolated()
    {
        var cp = Create();
        await cp.PutAsync(Ckpt("gone"));
        await cp.PutAsync(Ckpt("kept"));

        await cp.DeleteThreadAsync("gone");
        await cp.DeleteThreadAsync("never-existed");

        Assert.Null(await cp.GetAsync("gone"));
        Assert.Empty(await cp.ListAsync("gone"));
        Assert.NotNull(await cp.GetAsync("kept"));
    }

    // ── journals ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "contract: an unknown task's journal is fresh and empty; an empty journal round-trips")]
    public async Task EmptyJournals()
    {
        var cp = Create();
        Assert.Empty((await cp.GetJournalAsync("nobody")).Keys);

        await cp.PutJournalAsync("empty", new Journal());
        var back = await cp.GetJournalAsync("empty");
        Assert.Empty(back.Keys);
        Assert.Empty(back.Pending());
    }

    [Fact(DisplayName = "contract: a journal round-trips entries, order and pending status")]
    public async Task JournalRoundTrips()
    {
        var cp = Create();
        var j = new Journal();
        j.PutDone("b#0", "B");
        j.PutPending("interrupt#0", "question?");
        j.PutDone("a#0", 1L);
        j.PutDone("nil#0", null);
        await cp.PutJournalAsync("task", j);

        var back = await cp.GetJournalAsync("task");
        Assert.Equal(new[] { "b#0", "interrupt#0", "a#0", "nil#0" }, back.Keys);
        Assert.Equal("B", back.Fetch("b#0")!.Value);
        Assert.Equal(1L, back.Fetch("a#0")!.Value);
        Assert.True(back.Fetch("nil#0")!.Done);
        Assert.Null(back.Fetch("nil#0")!.Value);
        var pending = Assert.Single(back.Pending());
        Assert.Equal(("interrupt#0", (object?)"question?"), (pending.Key, pending.Payload));
    }

    [Fact(DisplayName = "contract: a put stores a snapshot, and each get hands out an independent journal")]
    public async Task JournalSnapshots()
    {
        var cp = Create();
        var j = new Journal();
        j.PutDone("a#0", "A");
        await cp.PutJournalAsync("task", j);
        j.PutDone("b#0", "B"); // after the put: must not leak in

        var first = await cp.GetJournalAsync("task");
        first.PutDone("c#0", "C");
        var second = await cp.GetJournalAsync("task");

        Assert.Equal(new[] { "a#0" }, second.Keys);
    }

    [Fact(DisplayName = "contract: DropJournal makes a task look unstarted; dropping an unknown task is a no-op")]
    public async Task DropJournal()
    {
        var cp = Create();
        var j = new Journal();
        j.PutDone("a#0", "A");
        await cp.PutJournalAsync("task", j);
        await cp.PutJournalAsync("other", j);

        await cp.DropJournalAsync("task");
        await cp.DropJournalAsync("never");

        Assert.Empty((await cp.GetJournalAsync("task")).Keys);
        Assert.Equal(new[] { "a#0" }, (await cp.GetJournalAsync("other")).Keys);
    }

    [Fact(DisplayName = "contract: DeleteThread drops the thread's journals, and only that thread's")]
    public async Task DeleteThreadDropsItsJournals()
    {
        var cp = Create();
        var j = new Journal();
        j.PutDone("greet#0", "hello");
        j.PutPending("interrupt#0", "?");

        // Task ids exactly as the engine builds them: {thread}:{planId ?? "root"}:{taskKey}.
        // "t2" shares a prefix with "t"; "t%" and "t_" would match a LIKE pattern.
        var mine = new[] { "t:root:w", $"t:{Checkpoint.GenerateId()}:w", "t:root:worker#0" };
        var others = new[] { "t2:root:w", "t%:root:w", "t_:root:w", "tt:root:w", $"t2:{Checkpoint.GenerateId()}:w" };
        foreach (var id in mine.Concat(others)) await cp.PutJournalAsync(id, j);

        await cp.DeleteThreadAsync("t");

        foreach (var id in mine) Assert.Empty((await cp.GetJournalAsync(id)).Keys);
        foreach (var id in others) Assert.Equal(new[] { "greet#0", "interrupt#0" }, (await cp.GetJournalAsync(id)).Keys);
    }

    [Fact(DisplayName = "contract: DeleteThread on an id with SQL wildcards removes only that thread's journals")]
    public async Task DeleteThreadWildcardIds()
    {
        var cp = Create();
        var j = new Journal();
        j.PutDone("s#0", "v");
        await cp.PutJournalAsync("a%_:root:w", j);
        await cp.PutJournalAsync("abc:root:w", j);
        await cp.PutJournalAsync("a%_x:root:w", j);

        await cp.DeleteThreadAsync("a%_");

        Assert.Empty((await cp.GetJournalAsync("a%_:root:w")).Keys);
        Assert.Single((await cp.GetJournalAsync("abc:root:w")).Keys);
        Assert.Single((await cp.GetJournalAsync("a%_x:root:w")).Keys);
    }

    [Fact(DisplayName = "contract: a deleted thread's id starts clean — a new run does not replay the old conversation")]
    public async Task DeletedThreadDoesNotLeak()
    {
        // Alice pauses in the first superstep, so her journal sits at task id
        // "t:root:w" — the same id a fresh run on "t" computes. Without dropping
        // journals on delete, Bob's greet step replayed "hello alice" and he was
        // asked Alice's question.
        var cp = Create();
        var greets = new List<string>();
        var g = Graph.Create("greeter")
            .Channel("who", Channels.LastWrite(""))
            .Channel("log", Channels.Append())
            .Node("w", async (state, ctx) =>
            {
                var who = state.Get<string>("who");
                var greeting = await ctx.StepAsync("greet", () => { greets.Add(who); return $"hello {who}"; });
                var ok = await ctx.InterruptAsync<string>(new Dictionary<string, object?> { ["q"] = $"{greeting}, proceed?" });
                return Update.Of("log", $"{greeting}:{ok}");
            })
            .Edge(Graph.Start, "w").Edge("w", Graph.End)
            .Compile();
        var opts = new RunOptions { ThreadId = "t", Checkpointer = cp };

        var alice = await g.RunAsync(new Dictionary<string, object?> { ["who"] = "alice" }, opts);
        Assert.Equal(RunStatus.Interrupted, alice.Status);

        await cp.DeleteThreadAsync("t");

        var bob = await g.RunAsync(new Dictionary<string, object?> { ["who"] = "bob" }, opts);
        Assert.Equal(RunStatus.Interrupted, bob.Status);
        Assert.Equal(new[] { "alice", "bob" }, greets); // bob's step really ran
        var payload = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(bob.Pending).Payload);
        Assert.Equal("hello bob, proceed?", payload["q"]);

        var done = await g.ResumeAsync("yes", opts);
        Assert.Equal(new List<string> { "hello bob:yes" }, done.State!.GetList<string>("log"));
    }
}

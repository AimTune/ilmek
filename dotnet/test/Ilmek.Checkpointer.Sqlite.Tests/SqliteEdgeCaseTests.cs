using Ilmek;
using Microsoft.Data.Sqlite;

namespace Ilmek.Checkpointers.Sqlite.Tests;

/// <summary>SQLite-specific edges: the table prefix, encoding limits, and concurrent use of one connection.</summary>
public sealed class SqliteEdgeCaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ilmek-sqlite-edge-").FullName;

    public void Dispose() => TempDir.Delete(_dir);

    private string DbPath(string name) => Path.Combine(_dir, $"{name}.db");

    // ── table prefix ────────────────────────────────────────────────────────

    [Theory(DisplayName = "a TablePrefix that is not a plain identifier is refused — it is interpolated into SQL")]
    [InlineData("x; DROP TABLE y; --")]
    [InlineData("ilmek_checkpoints (x); --")]
    [InlineData("a b")]
    [InlineData("1abc")]
    [InlineData("")]
    [InlineData("pre\"fix")]
    [InlineData("pre-fix")]
    [InlineData("abc\n")]
    [InlineData("ünïcode")]
    public void HostilePrefixRefused(string prefix)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var ex = Assert.Throws<ArgumentException>(() =>
            new SqliteCheckpointer(connection, new SqliteCheckpointerOptions { TablePrefix = prefix }));
        Assert.Contains("TablePrefix", ex.Message);
    }

    [Fact(DisplayName = "a null TablePrefix is refused")]
    public void NullPrefixRefused()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Assert.Throws<ArgumentException>(() =>
            new SqliteCheckpointer(connection, new SqliteCheckpointerOptions { TablePrefix = null! }));
    }

    [Theory(DisplayName = "plain identifier prefixes are accepted")]
    [InlineData("ilmek")]
    [InlineData("_private")]
    [InlineData("App_2")]
    public async Task PlainPrefixAccepted(string prefix)
    {
        using var cp = SqliteCheckpointer.Open(":memory:", new SqliteCheckpointerOptions { TablePrefix = prefix });
        Assert.Null(await cp.GetAsync("t"));
    }

    [Fact(DisplayName = "Open with a hostile prefix throws without leaving the database file locked")]
    public void FailedOpenReleasesFile()
    {
        var path = DbPath("refused");
        Assert.Throws<ArgumentException>(() =>
            SqliteCheckpointer.Open(path, new SqliteCheckpointerOptions { TablePrefix = "x;--" }));
        File.Delete(path); // throws on Windows if the failed Open kept a handle
        Assert.False(File.Exists(path));
    }

    [Fact(DisplayName = "two prefixes on one file isolate checkpoints AND journals, including DeleteThread")]
    public async Task PrefixesIsolateEverything()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath("two-apps")}");
        connection.Open();
        var app1 = new SqliteCheckpointer(connection, new SqliteCheckpointerOptions { TablePrefix = "app1" });
        var app2 = new SqliteCheckpointer(connection, new SqliteCheckpointerOptions { TablePrefix = "app2" });
        app1.Migrate();
        app2.Migrate();

        var j = new Journal();
        j.PutDone("s#0", "app1-value");
        await app1.PutJournalAsync("t:root:n", j);
        await app1.PutAsync(new Checkpoint(Checkpoint.GenerateId(), null, null, "t",
            new Dictionary<string, object?>(), Array.Empty<ScheduledTask>(), Array.Empty<Pending>(), 0, 0));

        Assert.Empty((await app2.GetJournalAsync("t:root:n")).Keys);
        Assert.Null(await app2.GetAsync("t"));

        await app2.DeleteThreadAsync("t"); // must not reach into app1's tables
        Assert.NotNull(await app1.GetAsync("t"));
        Assert.Equal("app1-value", (await app1.GetJournalAsync("t:root:n")).Fetch("s#0")!.Value);
    }

    // ── encoding ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "a lone surrogate is stored, and reads back as U+FFFD (the JSON encoder replaces it)")]
    public async Task LoneSurrogateBecomesReplacementChar()
    {
        // Invalid UTF-16 cannot cross a UTF-8 boundary intact. Pinned: the write
        // succeeds and the value reads back with U+FFFD in its place — lossy but
        // readable, never a checkpoint that fails to decode.
        using var cp = SqliteCheckpointer.Open(DbPath("surrogate"));
        var bad = new Checkpoint(Checkpoint.GenerateId(), null, null, "t",
            new Dictionary<string, object?> { ["s"] = "broken \uD800 half" },
            Array.Empty<ScheduledTask>(), Array.Empty<Pending>(), 0, 0);

        await cp.PutAsync(bad);
        var back = await cp.GetAsync("t");
        Assert.Equal("broken � half", back!.Channels["s"]);
    }

    [Fact(DisplayName = "custom CLR objects come back as their JSON shape, numbers as long or double")]
    public async Task CustomTypesComeBackAsJsonShape()
    {
        using var cp = SqliteCheckpointer.Open(DbPath("shapes"));
        var j = new Journal();
        j.PutDone("order#0", new { Id = "o-1", Total = 12.5, Count = 3 });
        await cp.PutJournalAsync("task", j);

        var value = Assert.IsType<Dictionary<string, object?>>((await cp.GetJournalAsync("task")).Fetch("order#0")!.Value);
        Assert.Equal("o-1", value["Id"]);
        Assert.Equal(12.5, value["Total"]);
        Assert.Equal(3L, value["Count"]);
    }

    // ── concurrency ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "a wide fan-out journaling steps concurrently through one SQLite connection stays consistent")]
    public async Task ConcurrentStepsShareOneConnection()
    {
        // Sends run as parallel tasks and every step persists its journal, so one
        // checkpointer's single connection sees concurrent commands.
        using var cp = SqliteCheckpointer.Open(DbPath("fanout-steps"));
        var calls = 0;
        var g = Graph.Create("wide")
            .Channel("out", Channels.Append())
            .Node("map", (_, _) => null)
            .Node("worker", async (state, ctx) =>
            {
                var n = Convert.ToInt64(state["n"]);
                long acc = 0;
                for (var i = 0; i < 5; i++)
                    acc += await ctx.StepAsync($"s{i}", async () =>
                    {
                        Interlocked.Increment(ref calls);
                        await Task.Yield();
                        return n * 10 + i;
                    });
                var ok = await ctx.InterruptAsync<string>(new Dictionary<string, object?> { ["n"] = n });
                return Update.Of("out", $"{n}:{acc}:{ok}");
            })
            .Edge(Graph.Start, "map")
            .Router("map", (_, _) => Enumerable.Range(0, 24)
                .Select(i => (object)new Send("worker", new Dictionary<string, object?> { ["n"] = (long)i })))
            .Edge("worker", Graph.End)
            .Compile();
        var opts = new RunOptions { ThreadId = "wide", Checkpointer = cp };

        var paused = await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Interrupted, paused.Status);
        Assert.Equal(24, paused.Pending.Count);
        Assert.Equal(24 * 5, calls);

        var done = await g.ResumeKeyedAsync(paused.Pending.ToDictionary(p => p.Id, p => (object?)"y"), opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(24 * 5, calls); // every step replayed from SQLite, none re-ran
        var expected = Enumerable.Range(0, 24).Select(n => $"{n}:{Enumerable.Range(0, 5).Sum(i => n * 10 + i)}:y");
        Assert.Equal(expected.OrderBy(x => x), done.State!.GetList<string>("out").OrderBy(x => x));
    }

    [Fact(DisplayName = "concurrent runs on distinct threads sharing one SQLite checkpointer are isolated")]
    public async Task ConcurrentThreadsShareOneCheckpointer()
    {
        using var cp = SqliteCheckpointer.Open(DbPath("threads"));
        var g = Graph.Create("per-thread")
            .Channel("who", Channels.LastWrite(""))
            .Channel("log", Channels.Append())
            .Node("w", async (state, ctx) =>
            {
                var who = state.Get<string>("who");
                var greeting = await ctx.StepAsync("greet", () => $"hi {who}");
                var ok = await ctx.InterruptAsync<string>(who);
                return Update.Of("log", $"{greeting}:{ok}");
            })
            .Edge(Graph.Start, "w").Edge("w", Graph.End)
            .Compile();

        var names = Enumerable.Range(0, 16).Select(i => $"user{i}").ToList();
        await Task.WhenAll(names.Select(n => g.RunAsync(
            new Dictionary<string, object?> { ["who"] = n }, new RunOptions { ThreadId = n, Checkpointer = cp })));
        var results = await Task.WhenAll(names.Select(n => g.ResumeAsync(
            $"ok-{n}", new RunOptions { ThreadId = n, Checkpointer = cp })));

        for (var i = 0; i < names.Count; i++)
            Assert.Equal(new List<string> { $"hi {names[i]}:ok-{names[i]}" }, results[i].State!.GetList<string>("log"));
    }
}

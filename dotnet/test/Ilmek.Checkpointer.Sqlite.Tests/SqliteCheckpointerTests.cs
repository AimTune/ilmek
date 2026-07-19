using Ilmek;
using Ilmek.Checkpointers.Sqlite;

namespace Ilmek.Checkpointers.Sqlite.Tests;

/// <summary>
/// The SQLite checkpointer, against real SQLite.
///
/// The tests that matter most are the restart ones: a pause written by one
/// connection is answered by another after the file is closed and reopened, and
/// the effect before that pause does not re-run. Those are the claims a durable
/// backend actually makes, and they mirror the TypeScript suite exactly.
/// </summary>
public sealed class SqliteCheckpointerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ilmek-sqlite-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string DbPath(string name) => Path.Combine(_dir, $"{name}.db");

    /// <summary>The §12.1 shape: an effect that must not happen twice across a pause.</summary>
    private static CompiledGraph CheckoutGraph(Action onCreate, Action onCharge) =>
        Graph.Create("checkout")
            .Channel("log", Channels.Append())
            .Node("checkout", async (_, ctx) =>
            {
                await ctx.StepAsync("create_order", () => { onCreate(); return "order-1"; });
                var ok = await ctx.InterruptAsync<string>(new Dictionary<string, object?> { ["q"] = "charge?" });
                await ctx.StepAsync("charge", () => { onCharge(); return "charged"; });
                return Update.Of("log", ok);
            })
            .Edge(Graph.Start, "checkout")
            .Edge("checkout", Graph.End)
            .Compile();

    [Fact(DisplayName = "Open() migrates, and Migrate() is idempotent")]
    public async Task OpenMigrates()
    {
        using var cp = SqliteCheckpointer.Open(":memory:");
        cp.Migrate(); // second call must not throw
        Assert.Null(await cp.GetAsync("nobody"));
    }

    [Fact(DisplayName = "using it before Migrate() says so instead of failing on missing tables")]
    public void UnmigratedIsExplicit()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        var cp = new SqliteCheckpointer(connection);

        var ex = Assert.Throws<InvalidOperationException>(() => cp.GetAsync("t").GetAwaiter().GetResult());
        Assert.Contains("Migrate()", ex.Message);
    }

    [Fact(DisplayName = "a step runs once across an interrupt/resume cycle (§12.1) on SQLite")]
    public async Task StepRunsOnceAcrossResume()
    {
        using var cp = SqliteCheckpointer.Open(DbPath("step-once"));
        var creates = 0;
        var charges = 0;

        var g = CheckoutGraph(() => creates++, () => charges++);
        var opts = new RunOptions { ThreadId = "t1", Checkpointer = cp };

        var paused = await g.RunAsync(null, opts);
        Assert.Equal(RunStatus.Interrupted, paused.Status);
        Assert.Equal(1, creates);
        Assert.Equal(0, charges); // the pause gates the charge

        var done = await g.ResumeAsync("yes", opts);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new List<string> { "yes" }, done.State!.GetList<string>("log"));
        Assert.Equal(1, creates); // the journal round-tripped through SQLite
        Assert.Equal(1, charges);
    }

    [Fact(DisplayName = "a pause survives closing the database — a new instance on the same FILE resumes it")]
    public async Task PauseSurvivesRestart()
    {
        // The whole point of a durable checkpointer: nothing but the file crosses
        // the boundary between these two "processes".
        var path = DbPath("across-restart");
        var g = CheckoutGraph(() => { }, () => { });

        using (var first = SqliteCheckpointer.Open(path))
        {
            var paused = await g.RunAsync(null, new RunOptions { ThreadId = "t2", Checkpointer = first });
            Assert.Equal(RunStatus.Interrupted, paused.Status);
        } // the "process" exits here

        using var second = SqliteCheckpointer.Open(path);
        var reread = await second.GetAsync("t2");
        Assert.Single(reread!.Pending); // the parked interrupt came back from disk

        var done = await g.ResumeAsync("yes", new RunOptions { ThreadId = "t2", Checkpointer = second });
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new List<string> { "yes" }, done.State!.GetList<string>("log"));
    }

    [Fact(DisplayName = "an effect before the pause does not re-run after a restart")]
    public async Task EffectsDoNotReRunAcrossRestart()
    {
        var path = DbPath("effects-across-restart");
        var creates = 0;
        var charges = 0;
        var g = CheckoutGraph(() => creates++, () => charges++);

        using (var first = SqliteCheckpointer.Open(path))
        {
            await g.RunAsync(null, new RunOptions { ThreadId = "t3", Checkpointer = first });
        }
        Assert.Equal(1, creates);

        using var second = SqliteCheckpointer.Open(path);
        var done = await g.ResumeAsync("yes", new RunOptions { ThreadId = "t3", Checkpointer = second });

        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(1, creates); // the journal crossed the restart
        Assert.Equal(1, charges);
    }

    [Fact(DisplayName = "channel values survive the JSON boundary as plain CLR data, not JsonElement")]
    public async Task ValuesDecodeToClrTypes()
    {
        // The .NET-specific hazard: System.Text.Json hands back JsonElement for
        // every object?, so a resumed node would see JsonElement where it wrote a
        // list. This asserts the decoder lowers them back.
        var path = DbPath("json-shapes");
        var g = Graph.Create("shapes")
            .Channel("items", Channels.Append())
            .Channel("meta", Channels.LastWrite())
            .Channel("count", Channels.LastWrite(0L))
            .Node("write", (_, _) => new Dictionary<string, object?>
            {
                ["items"] = new List<object?> { "a", "b" },
                ["meta"] = new Dictionary<string, object?> { ["k"] = "v", ["n"] = 2L },
                ["count"] = 3L,
            })
            .Node("pause", async (_, ctx) =>
            {
                await ctx.InterruptAsync<string>(new Dictionary<string, object?> { ["q"] = "?" });
                return null;
            })
            .Edge(Graph.Start, "write")
            .Edge("write", "pause")
            .Edge("pause", Graph.End)
            .Compile();

        using (var first = SqliteCheckpointer.Open(path))
        {
            await g.RunAsync(null, new RunOptions { ThreadId = "t4", Checkpointer = first });
        }

        using var second = SqliteCheckpointer.Open(path);
        var state = await IlmekRuntime.ThreadStateAsync(g, second, "t4");

        Assert.Equal(new List<string> { "a", "b" }, state!.GetList<string>("items"));
        var meta = Assert.IsType<Dictionary<string, object?>>(state["meta"]);
        Assert.Equal("v", meta["k"]);
        Assert.Equal(2L, meta["n"]); // an integral number stays integral
        Assert.Equal(3L, state["count"]);
    }

    [Fact(DisplayName = "get by id, list (newest first, with limit), and DeleteThread")]
    public async Task HistoryOperations()
    {
        using var cp = SqliteCheckpointer.Open(DbPath("history"));

        var g = Graph.Create("chain")
            .Channel("n", Channels.LastWrite(0L))
            .Node("a", (_, _) => Update.Of("n", 1L))
            .Node("b", (_, _) => Update.Of("n", 2L))
            .Edge(Graph.Start, "a")
            .Edge("a", "b")
            .Edge("b", Graph.End)
            .Compile();

        await g.RunAsync(null, new RunOptions { ThreadId = "t5", Checkpointer = cp });

        var history = await cp.ListAsync("t5");
        Assert.True(history.Count >= 2);
        Assert.True(string.CompareOrdinal(history[0].Id, history[^1].Id) > 0); // newest first

        var one = await cp.ListAsync("t5", limit: 1);
        Assert.Single(one);
        Assert.Equal(history[0].Id, one[0].Id);

        var byId = await cp.GetAsync("t5", history[0].Id);
        Assert.Equal(history[0].Id, byId!.Id);

        await cp.DeleteThreadAsync("t5");
        Assert.Null(await cp.GetAsync("t5"));
        Assert.Empty(await cp.ListAsync("t5"));
    }

    [Fact(DisplayName = "a custom TablePrefix isolates two apps in one file")]
    public async Task TablePrefixIsolates()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath("shared")}");
        connection.Open();

        var app1 = new SqliteCheckpointer(connection, new SqliteCheckpointerOptions { TablePrefix = "app1" });
        var app2 = new SqliteCheckpointer(connection, new SqliteCheckpointerOptions { TablePrefix = "app2" });
        app1.Migrate();
        app2.Migrate();

        var g = Graph.Create("g")
            .Channel("n", Channels.LastWrite(0L))
            .Node("a", (_, _) => Update.Of("n", 1L))
            .Edge(Graph.Start, "a")
            .Edge("a", Graph.End)
            .Compile();

        await g.RunAsync(null, new RunOptions { ThreadId = "same-id", Checkpointer = app1 });

        Assert.NotEmpty(await app1.ListAsync("same-id"));
        Assert.Empty(await app2.ListAsync("same-id")); // app2's tables are untouched

        connection.Dispose();
    }

    [Fact(DisplayName = "a fan-out send payload survives the restart")]
    public async Task SendPayloadSurvivesRestart()
    {
        // ScheduledTask.Input is persisted in `next`, so a resumed run must
        // re-dispatch the same fan-out with the same payloads (MODEL.md §14).
        var path = DbPath("fanout");
        var g = Graph.Create("fanout")
            .Channel("out", Channels.Append())
            .Node("map", (_, _) => null)
            .Node("worker", async (state, ctx) =>
            {
                var id = (string)state["id"]!;
                var ok = await ctx.InterruptAsync<string>(new Dictionary<string, object?> { ["q"] = id });
                return Update.Of("out", $"{id}:{ok}");
            })
            .Edge(Graph.Start, "map")
            .Router("map", (_, _) => new object[]
            {
                new Send("worker", new Dictionary<string, object?> { ["id"] = "a" }),
                new Send("worker", new Dictionary<string, object?> { ["id"] = "b" }),
            })
            .Edge("worker", Graph.End)
            .Compile();

        Dictionary<string, object?> answers;
        using (var first = SqliteCheckpointer.Open(path))
        {
            var paused = await g.RunAsync(null, new RunOptions { ThreadId = "t6", Checkpointer = first });
            Assert.Equal(2, paused.Pending.Count);
            answers = paused.Pending.ToDictionary(p => p.Id, p => (object?)"yes");
        }

        using var second = SqliteCheckpointer.Open(path);
        var done = await g.ResumeKeyedAsync(answers, new RunOptions { ThreadId = "t6", Checkpointer = second });

        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(new[] { "a:yes", "b:yes" }, done.State!.GetList<string>("out").OrderBy(x => x));
    }
}

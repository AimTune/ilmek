using System.Globalization;
using Ilmek;
using Ilmek.Checkpointers.Sqlite;

namespace Ilmek.Checkpointers.Sqlite.Tests;

/// <summary>
/// Typed step values across a real reload: the step runs against one
/// <see cref="SqliteCheckpointer"/>, the thread pauses, the "process" exits, and a
/// NEW checkpointer on the same file resumes it. The journal comes back from disk
/// as plain JSON data (dictionaries, lists, longs, doubles), so
/// <c>StepAsync&lt;T&gt;</c> has to turn it back into <c>T</c> — a record, a tuple,
/// an exact decimal, an enum — instead of failing with an InvalidCastException.
/// </summary>
public sealed class TypedStepReloadTests : IDisposable
{
    public enum Status { Pending = 0, Shipped = 2, Lost = 7 }

    public sealed record Line(string Sku, int Qty, decimal Price);

    public sealed record Order(string Id, decimal Total, Status Status, IReadOnlyList<Line> Lines, DateTimeOffset At, Guid Ref)
    {
        public IReadOnlyDictionary<string, object?>? Extra { get; init; }
    }

    public sealed record Answer(bool Approved, string Note);

    private static readonly Guid Ref = Guid.Parse("9f1c2a5e-6b0d-4c4e-8a51-3f0a9d1e7b22");
    private static readonly DateTimeOffset At = new(2026, 10, 7, 9, 30, 15, 123, TimeSpan.FromHours(3));

    private static Order SampleOrder() => new(
        "o-1",
        12345678901234567890.123456789m,
        Status.Shipped,
        [new Line("a", 2, 0.1m), new Line("b", 1, 1.10m)],
        At,
        Ref)
    {
        Extra = new Dictionary<string, object?> { ["k"] = "v", ["n"] = 2L, ["list"] = new List<object?> { 1L, "x" } },
    };

    private readonly string _dir = Directory.CreateTempSubdirectory("ilmek-typed-").FullName;

    public void Dispose() => TempDir.Delete(_dir);

    /// <summary>
    /// Journal <paramref name="make"/>'s value in a step, pause, reload the journal
    /// through a new SqliteCheckpointer on the same file, resume, and return what
    /// the step handed back on the replay pass.
    /// </summary>
    private async Task<T> AcrossReload<T>(string name, Func<T> make)
    {
        var path = Path.Combine(_dir, $"{name}.db");
        var calls = 0;
        var replayed = new List<T>();
        var g = Graph.Create(name)
            .Node("work", async (State _, IContext ctx) =>
            {
                var v = await ctx.StepAsync("make", () => { calls++; return make(); });
                replayed.Add(v);
                await ctx.InterruptAsync<string>("go?");
                return null;
            })
            .Edge(Graph.Start, "work")
            .Edge("work", Graph.End)
            .Compile();

        using (var first = SqliteCheckpointer.Open(path))
        {
            var paused = await g.RunAsync(null, new RunOptions { ThreadId = name, Checkpointer = first });
            Assert.Equal(RunStatus.Interrupted, paused.Status);
        }

        using var second = SqliteCheckpointer.Open(path);
        var done = await g.ResumeAsync("yes", new RunOptions { ThreadId = name, Checkpointer = second });

        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(1, calls); // the step did not re-run: its value came from disk
        Assert.Equal(2, replayed.Count);
        return replayed[1];
    }

    [Fact(DisplayName = "a record with nested records, a decimal, an enum, a date and a guid survives a reload")]
    public async Task Record()
    {
        var back = await AcrossReload("record", SampleOrder);

        var want = SampleOrder();
        Assert.Equal(want.Id, back.Id);
        Assert.Equal(want.Total, back.Total);
        Assert.Equal(want.Total.ToString(CultureInfo.InvariantCulture), back.Total.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(Status.Shipped, back.Status);
        Assert.Equal(want.Lines, back.Lines);
        Assert.Equal("1.10", back.Lines[1].Price.ToString(CultureInfo.InvariantCulture)); // the scale survives too
        Assert.Equal(At, back.At);
        Assert.Equal(Ref, back.Ref);

        // Loosely-typed members come back as plain CLR data, never JsonElement.
        Assert.Equal("v", back.Extra!["k"]);
        Assert.Equal(2L, back.Extra["n"]);
        Assert.Equal(new List<object?> { 1L, "x" }, Assert.IsType<List<object?>>(back.Extra["list"]));
    }

    [Fact(DisplayName = "a value tuple survives a reload")]
    public async Task Tuple()
    {
        var back = await AcrossReload("tuple", () => (Count: 3, Name: "three", Price: 9.99m));
        Assert.Equal(3, back.Count);
        Assert.Equal("three", back.Name);
        Assert.Equal(9.99m, back.Price);
    }

    [Fact(DisplayName = "decimals round-trip exactly — value and scale — whatever the current culture")]
    public async Task DecimalsAreExact()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); // "1,10" — a culture-sensitive path would break
        try
        {
            decimal[] values = [0.1m, 1.10m, 5.0m, 12345678901234567890.123456789m, -0.000000001m, decimal.MaxValue, decimal.MinValue, 79228162514264337593543950335m];
            var back = await AcrossReload("decimals", () => values);
            Assert.Equal(values, back);
            Assert.Equal(
                values.Select(v => v.ToString(CultureInfo.InvariantCulture)),
                back.Select(v => v.ToString(CultureInfo.InvariantCulture)));

            Assert.Equal(1.10m, await AcrossReload("decimal-scalar", () => 1.10m));
            Assert.Equal("1.10", (await AcrossReload("decimal-scale", () => 1.10m)).ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact(DisplayName = "an enum survives a reload")]
    public async Task Enum() => Assert.Equal(Status.Lost, await AcrossReload("enum", () => Status.Lost));

    [Fact(DisplayName = "a list of records survives a reload")]
    public async Task ListOfRecords()
    {
        IReadOnlyList<Line> lines = [new Line("a", 1, 2.5m), new Line("b", 3, 0.3m)];
        Assert.Equal(lines, await AcrossReload("list", () => lines));
        Assert.Equal(lines, await AcrossReload("list-concrete", () => lines.ToList()));
        Assert.Equal(lines, await AcrossReload("array", () => lines.ToArray()));
    }

    [Fact(DisplayName = "a dictionary of typed values survives a reload")]
    public async Task TypedDictionary()
    {
        var prices = new Dictionary<string, decimal> { ["a"] = 0.1m, ["b"] = 1.10m };
        Assert.Equal(prices, await AcrossReload("dict", () => prices));
    }

    [Fact(DisplayName = "narrow numbers survive a reload: int, float, double")]
    public async Task Numbers()
    {
        Assert.Equal(42, await AcrossReload("int", () => 42));
        Assert.Equal((short)7, await AcrossReload("short", () => (short)7));
        Assert.Equal(0.1, await AcrossReload("double", () => 0.1));
        Assert.Equal(5.0, await AcrossReload("double-integral", () => 5.0));
        Assert.Equal(1e300, await AcrossReload("double-huge", () => 1e300));
        Assert.Equal(0.1f, await AcrossReload("float", () => 0.1f));
        Assert.Equal(long.MaxValue, await AcrossReload("long", () => long.MaxValue));
    }

    [Fact(DisplayName = "null survives a reload into nullable types")]
    public async Task Nulls()
    {
        Assert.Null(await AcrossReload<int?>("null-int", () => null));
        Assert.Equal(4, await AcrossReload<int?>("some-int", () => 4));
        Assert.Null(await AcrossReload<Order?>("null-record", () => null));
        Assert.Null(await AcrossReload<string?>("null-string", () => null));
    }

    [Fact(DisplayName = "untyped steps keep the plain-data shape after a reload")]
    public async Task UntypedStaysPlain()
    {
        var back = await AcrossReload<object?>("untyped", () => new Dictionary<string, object?> { ["a"] = 1L, ["b"] = "x" });
        var d = Assert.IsType<Dictionary<string, object?>>(back);
        Assert.Equal(1L, d["a"]);
        Assert.Equal("x", d["b"]);
    }

    [Fact(DisplayName = "a typed interrupt answer replays after a reload")]
    public async Task TypedInterruptAnswer()
    {
        // The first answer is journaled; the second pause sends the thread back to
        // disk; on the final resume the first answer replays from the reloaded
        // journal and must come back as Answer, not as a dictionary.
        var path = Path.Combine(_dir, "answers.db");
        Answer? seen = null;
        var g = Graph.Create("answers")
            .Node("work", async (State _, IContext ctx) =>
            {
                var a = await ctx.InterruptAsync<Answer>("first?", "first");
                await ctx.InterruptAsync<string>("second?", "second");
                seen = a;
                return null;
            })
            .Edge(Graph.Start, "work")
            .Edge("work", Graph.End)
            .Compile();

        using (var cp = SqliteCheckpointer.Open(path))
        {
            await g.RunAsync(null, new RunOptions { ThreadId = "a", Checkpointer = cp });
        }
        using (var cp = SqliteCheckpointer.Open(path))
        {
            var paused = await g.ResumeAsync(new Answer(true, "ok"), new RunOptions { ThreadId = "a", Checkpointer = cp });
            Assert.Equal(RunStatus.Interrupted, paused.Status);
        }
        using (var cp = SqliteCheckpointer.Open(path))
        {
            var done = await g.ResumeAsync("fine", new RunOptions { ThreadId = "a", Checkpointer = cp });
            Assert.Equal(RunStatus.Done, done.Status);
        }

        Assert.Equal(new Answer(true, "ok"), seen);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ilmek.Core.Tests;

/// <summary>
/// Mermaid export (MODEL.md §9.1). The cases under conformance/viz are the
/// cross-language fixture: this suite and the TypeScript one must render each
/// to the same bytes.
/// </summary>
public class MermaidTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "viz");

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Fixtures, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    // ── conformance/viz (shared with TypeScript) ──────────────────────────────

    [Fact(DisplayName = "the fixture is not empty")]
    public void FixtureIsNotEmpty() => Assert.True(Cases().Count >= 5);

    [Theory(DisplayName = "the compiled graph renders to <case>.mmd")]
    [MemberData(nameof(Cases))]
    public void CompiledGraphMatchesFixture(string name)
    {
        var c = Load(name);
        Assert.Equal(Expected(name), Compile(c).ToMermaid(c.Options));
    }

    [Theory(DisplayName = "a code-free case renders the same text from its stored spec")]
    [MemberData(nameof(Cases))]
    public void SpecMatchesFixture(string name)
    {
        var c = Load(name);
        if (c.Routers.Count > 0 || c.Guards.Count > 0) return;   // code has no spec form
        Assert.Equal(Expected(name), Spec.ToMermaid(c.Spec, c.Options));
    }

    // ── behaviour ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "a spec and the graph built from it render identically")]
    public void SpecAndGraphAgree()
    {
        var g = Graph.Create("loop")
            .Channel("n", Channels.LastWrite(0))
            .Node("tick", (_, _) => null, type: "tick")
            .Edge(Graph.Start, "tick")
            .Edge("tick", "tick", (s, _) => s.Get<int>("n") < 3, new SpecPredicate { Channel = "n", Lt = 3 })
            .Edge("tick", Graph.End, (s, _) => s.Get<int>("n") >= 3, new SpecPredicate { Channel = "n", Gt = 2 })
            .Compile();

        Assert.Equal(g.ToMermaid(), Spec.ToMermaid(Spec.ToSpec(g)));
        Assert.Contains("tick -->|\"n #lt; 3\"| tick", g.ToMermaid());
    }

    [Fact(DisplayName = "a typed graph renders its routers' declared targets, send targets included")]
    public void TypedRouterTargets()
    {
        var g = Graph.Create<FanState>("fan")
            .Node("split", (_, _) => null)
            .Node("work", (_, _) => null)
            .Edge(Graph.Start, "split")
            .Router("split", (s, _) => s.Items.Select(i => (object)new Send("work", new { item = i })), targets: new[] { "work" })
            .Edge("work", Graph.End)
            .Compile();

        Assert.Contains("\n  split -.-> work\n", g.ToMermaid());
    }

    [Fact(DisplayName = "an undeclared router draws a ? node; IncludeRouters = false hides it")]
    public void UndeclaredRouter()
    {
        var g = Graph.Create()
            .Node("a", (_, _) => null)
            .Edge(Graph.Start, "a")
            .Router("a", (_, _) => new object[] { Graph.End })
            .Compile();

        Assert.Contains("\n  a -.-> r0\n", g.ToMermaid());
        Assert.Contains("\n  r0{\"?\"}\n", g.ToMermaid());
        Assert.DoesNotContain("r0", g.ToMermaid(new MermaidOptions { IncludeRouters = false }));
    }

    [Fact(DisplayName = "a router target must be a node or End")]
    public void UnknownRouterTarget()
    {
        var b = Graph.Create()
            .Node("a", (_, _) => null)
            .Edge(Graph.Start, "a")
            .Router("a", (_, _) => new object[] { Graph.End }, targets: new[] { "nowhere" });
        Assert.Throws<GraphException>(() => b.Compile());
    }

    [Fact(DisplayName = "an unknown direction is refused")]
    public void UnknownDirection()
    {
        var g = Graph.Create().Node("a", (_, _) => null).Edge(Graph.Start, "a").Compile();
        Assert.Throws<GraphException>(() => g.ToMermaid(new MermaidOptions { Direction = "XY" }));
    }

    [Fact(DisplayName = "highlights a parked thread from its checkpoint")]
    public async Task HighlightsParkedThread()
    {
        var checkpointer = new InMemoryCheckpointer();
        var g = Graph.Create("approve")
            .Channel("log", Channels.Append())
            .Node("draft", (_, _) => Update.Of("log", "drafted"))
            .Node("approve", async (_, ctx) =>
            {
                await ctx.InterruptAsync<string>(new { q = "ship it?" });
                return null;
            })
            .Node("notify", (_, _) => Update.Of("log", "notified"))
            .Edge(Graph.Start, "draft")
            .Edge("draft", "approve")
            .Edge("draft", "notify")
            .Edge("approve", Graph.End)
            .Edge("notify", Graph.End)
            .Compile();

        var paused = await g.RunAsync(new Dictionary<string, object?>(), new RunOptions { ThreadId = "t1", Checkpointer = checkpointer });
        Assert.Equal(RunStatus.Interrupted, paused.Status);

        var text = g.ToMermaid(new MermaidOptions { Highlight = await checkpointer.GetAsync("t1") });
        Assert.Contains("\n  approve[\"⏸ approve\"]\n", text);
        Assert.Contains("\n  class approve pending\n", text);
        // The superstep did not commit, so notify re-runs (from its journal) and is
        // still next; approve is marked pending instead, never both.
        Assert.Contains("\n  class notify next\n", text);
    }

    [Fact(DisplayName = "highlights the next tasks in declaration order, each once")]
    public void HighlightsNext()
    {
        var g = Graph.Create()
            .Node("a", (_, _) => null)
            .Node("b", (_, _) => null)
            .Node("c", (_, _) => null)
            .Edge(Graph.Start, "a")
            .Edge("a", "b")
            .Edge("a", "c")
            .Compile();

        var text = g.ToMermaid(new MermaidOptions { Highlight = Ckpt(next: new[] { "c", "b", "b" }) });
        Assert.Contains("\n  class b,c next\n", text);
        Assert.DoesNotContain("pending", text);
    }

    // ── fixture plumbing ──────────────────────────────────────────────────────

    private sealed class FanState
    {
        public List<string> Items { get; set; } = new();
    }

    private sealed record VizCase(GraphSpec Spec, List<(string From, string[]? Targets)> Routers,
        List<(string From, string To)> Guards, MermaidOptions Options);

    private static string Expected(string name) =>
        File.ReadAllText(Path.Combine(Fixtures, name + ".mmd")).Replace("\r\n", "\n");

    private static VizCase Load(string name)
    {
        var text = File.ReadAllText(Path.Combine(Fixtures, name + ".json"));
        var root = JsonNode.Parse(text)!.AsObject();
        var spec = JsonSerializer.Deserialize<GraphSpec>(root["spec"]!.ToJsonString(), Json)!;

        var routers = (root["routers"]?.AsArray() ?? new JsonArray())
            .Select(r => (r!["from"]!.GetValue<string>(),
                r["targets"] is JsonArray t ? t.Select(x => x!.GetValue<string>()).ToArray() : null))
            .ToList();
        var guards = (root["guards"]?.AsArray() ?? new JsonArray())
            .Select(g => (g!["from"]!.GetValue<string>(), g["to"]!.GetValue<string>()))
            .ToList();

        var o = root["options"]?.AsObject();
        var options = new MermaidOptions();
        if (o?["direction"] is { } d) options = options with { Direction = d.GetValue<string>() };
        if (o?["includeRouters"] is { } ir) options = options with { IncludeRouters = ir.GetValue<bool>() };
        if (o?["highlight"] is JsonObject h)
        {
            options = options with
            {
                Highlight = Ckpt(
                    h["next"]!.AsArray().Select(x => x!.GetValue<string>()),
                    h["pending"]!.AsArray().Select(x => x!.GetValue<string>())),
            };
        }

        return new VizCase(spec, routers, guards, options);
    }

    private static CompiledGraph Compile(VizCase c)
    {
        var registry = new Dictionary<string, NodeBuilder>();
        foreach (var n in c.Spec.Nodes) registry[n.Type] = _ => (_, _) => new ValueTask<object?>((object?)null);

        var g = Spec.FromSpec(c.Spec, registry);
        foreach (var (from, targets) in c.Routers) g.Router(from, (_, _) => Array.Empty<object>(), targets);
        foreach (var (from, to) in c.Guards) g.Edge(from, to, (_, _) => true);
        return g.Compile();
    }

    private static Checkpoint Ckpt(IEnumerable<string>? next = null, IEnumerable<string>? pending = null) => new(
        "ckpt", null, null, "t",
        new Dictionary<string, object?>(),
        (next ?? Array.Empty<string>()).Select(n => new ScheduledTask(n, n, false)).ToList(),
        (pending ?? Array.Empty<string>()).Select(n => new Pending($"{n}:interrupt#0", $"t:root:{n}", n, "interrupt#0", null)).ToList(),
        0, 0);
}

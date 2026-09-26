using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Ilmek;
using Ilmek.Skills;

namespace Ilmek.Skills.Tests;

/// <summary>
/// The SKILL.md reader, the catalog, the prompt renderer and the skill node
/// types. The shared folders under conformance/skills are the cross-language
/// fixture: this suite and the TypeScript one must read them to the same JSON.
/// </summary>
public class SkillsTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "skills");
    private readonly string _work = Directory.CreateTempSubdirectory("ilmek-skills-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string SkillDir(string name, string markdown, IReadOnlyDictionary<string, string>? files = null)
    {
        var dir = Path.Combine(_work, Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), markdown);
        foreach (var (rel, content) in files ?? new Dictionary<string, string>())
        {
            var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return dir;
    }

    // ── conformance/skills (shared with TypeScript) ───────────────────────────

    [Fact(DisplayName = "the fixture folders read to expected.json")]
    public async Task FixturesMatchExpected()
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "expected.json")))!.AsObject();
        var skills = await SkillLoader.DiscoverAsync(Fixtures);

        var actual = new JsonArray(skills.Select(SkillJson).ToArray());
        Assert.Equal(Canonical(expected["skills"]!), Canonical(actual));
        Assert.Equal(expected["prompt"]!.GetValue<string>(), SkillPrompt.Render(skills.Select(s => s.ToMetadata())));
    }

    [Fact(DisplayName = "every fixture skill records where it was loaded from")]
    public async Task FixturesRecordPath()
    {
        foreach (var s in await SkillLoader.DiscoverAsync(Fixtures))
            Assert.Equal(Path.GetFullPath(Path.Combine(Fixtures, s.Name)), s.Path);
    }

    /// <summary>The TypeScript object shape, minus <c>path</c> (machine-specific).</summary>
    private static JsonNode SkillJson(Skill s)
    {
        var o = new JsonObject
        {
            ["name"] = s.Name,
            ["description"] = s.Description,
            ["instructions"] = s.Instructions,
            ["resources"] = new JsonArray(s.Resources.Select(r => (JsonNode?)r).ToArray()),
        };
        if (s.License is not null) o["license"] = s.License;
        if (s.Compatibility is not null) o["compatibility"] = s.Compatibility;
        if (s.AllowedTools is not null) o["allowedTools"] = new JsonArray(s.AllowedTools.Select(t => (JsonNode?)t).ToArray());
        if (s.Metadata is not null)
        {
            var m = new JsonObject();
            foreach (var (k, v) in s.Metadata) m[k] = v;
            o["metadata"] = m;
        }
        return o;
    }

    private static readonly JsonSerializerOptions CanonicalOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Canonical(JsonNode node) => Sort(node).ToJsonString(CanonicalOptions);

    private static JsonNode Sort(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, kv.Value is null ? null : Sort(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(v => v is null ? null : Sort(v)).ToArray()),
        _ => node.DeepClone(),
    };

    // ── frontmatter reader ────────────────────────────────────────────────────

    [Fact(DisplayName = "scalars, quoting, comments, nested mapping, block and flow sequences")]
    public void FrontmatterShapes()
    {
        var (data, body) = FrontmatterReader.Parse(string.Join("\n",
        [
            "---",
            "name: demo",
            "plain: hello world   # a comment",
            "dq: \"a \\\"quoted\\\" value\"",
            "sq: 'it''s'",
            "url: https://example.com/#anchor",
            "flow: [a, \"b, c\", d]",
            "block:",
            "  - one",
            "  - two",
            "nested:",
            "  k1: v1",
            "  k2: 2",
            "empty:",
            "literal: |",
            "  line one",
            "  line two",
            "folded: >-",
            "  folded",
            "  together",
            "",
            "  new paragraph",
            "---",
            "",
            "# Body",
            "text",
        ]));
        Assert.Equal("demo", data["name"]);
        Assert.Equal("hello world", data["plain"]);
        Assert.Equal("a \"quoted\" value", data["dq"]);
        Assert.Equal("it's", data["sq"]);
        Assert.Equal("https://example.com/#anchor", data["url"]);
        Assert.Equal(new object?[] { "a", "b, c", "d" }, (IReadOnlyList<object?>)data["flow"]!);
        Assert.Equal(new object?[] { "one", "two" }, (IReadOnlyList<object?>)data["block"]!);
        var nested = (IReadOnlyDictionary<string, object?>)data["nested"]!;
        Assert.Equal("v1", nested["k1"]);
        Assert.Equal("2", nested["k2"]);
        Assert.Equal("", data["empty"]);
        Assert.Equal("line one\nline two\n", data["literal"]);
        Assert.Equal("folded together\nnew paragraph", data["folded"]);
        Assert.Equal("# Body\ntext", body);
    }

    [Fact(DisplayName = "CRLF and a BOM are tolerated")]
    public void CrlfAndBom()
    {
        var (data, body) = FrontmatterReader.Parse("\uFEFF---\r\nname: x\r\n---\r\nbody\r\n");
        Assert.Equal("x", data["name"]);
        Assert.Equal("body\n", body);
    }

    [Theory]
    [InlineData("missing_frontmatter", "# no frontmatter")]
    [InlineData("unterminated_frontmatter", "---\nname: x\n")]
    [InlineData("bad_structure", "---\nnot a key\n---\n")]
    [InlineData("bad_indentation", "---\nname: x\n  stray: y\n---\n")]
    [InlineData("duplicate_key", "---\nname: x\nname: y\n---\n")]
    [InlineData("unsupported", "---\nx: {a: 1}\n---\n")]
    [InlineData("unsupported", "---\nx: &anchor v\n---\n")]
    [InlineData("unsupported", "---\nx:\n  - k: v\n---\n")]
    public void FrontmatterRejects(string code, string text)
    {
        var ex = Assert.Throws<SkillParseException>(() => FrontmatterReader.Parse(text));
        Assert.Equal(code, ex.Code);
    }

    // ── SkillParser ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "reads the required and optional fields")]
    public void ParsesFields()
    {
        var s = SkillParser.Parse("---\nname: a-b\ndescription: Does things.\nlicense: MIT\nallowed-tools: Read Bash(git:*)\nmetadata:\n  team: core\n---\n\nBody here.\n");
        Assert.Equal("a-b", s.Name);
        Assert.Equal("Does things.", s.Description);
        Assert.Equal("MIT", s.License);
        Assert.Equal(["Read", "Bash(git:*)"], s.AllowedTools!);
        Assert.Equal("core", s.Metadata!["team"]);
        Assert.Equal("Body here.", s.Instructions);
        Assert.Empty(s.Resources);
        Assert.Null(s.Path);
    }

    [Fact(DisplayName = "allowed-tools also accepts a list")]
    public void AllowedToolsList()
    {
        var s = SkillParser.Parse("---\nname: a\ndescription: d\nallowed-tools: [Read, Write]\n---\n");
        Assert.Equal(["Read", "Write"], s.AllowedTools!);
        Assert.Equal("", s.Instructions);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("pdf", true)]
    [InlineData("brand-voice", true)]
    [InlineData("a1-b2", true)]
    [InlineData("", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData("a--b", false)]
    [InlineData("Pdf", false)]
    [InlineData("a_b", false)]
    [InlineData("a b", false)]
    public void NameRules(string name, bool ok)
    {
        Assert.Equal(ok, SkillParser.IsValidName(name));
        Assert.True(SkillParser.IsValidName(new string('x', 64)));
        Assert.False(SkillParser.IsValidName(new string('x', 65)));
    }

    [Theory]
    [InlineData("invalid_name", "---\ndescription: d\n---\n")]
    [InlineData("invalid_name", "---\nname: Not Valid\ndescription: d\n---\n")]
    [InlineData("invalid_description", "---\nname: ok\n---\n")]
    [InlineData("invalid_field", "---\nname: ok\ndescription: d\nmetadata: just-a-string\n---\n")]
    [InlineData("invalid_field", "---\nname: ok\ndescription: d\nmetadata:\n  k:\n    - nested\n---\n")]
    [InlineData("invalid_field", "---\nname: ok\ndescription: d\nlicense:\n  - MIT\n---\n")]
    public void ParserRejects(string code, string text)
    {
        var ex = Assert.Throws<SkillParseException>(() => SkillParser.Parse(text));
        Assert.Equal(code, ex.Code);
    }

    [Fact(DisplayName = "an over-long description is refused")]
    public void LongDescription()
    {
        var ex = Assert.Throws<SkillParseException>(() => SkillParser.Parse($"---\nname: ok\ndescription: {new string('x', 1025)}\n---\n"));
        Assert.Equal("invalid_description", ex.Code);
    }

    [Fact(DisplayName = "expectName enforces the folder-name rule")]
    public void ExpectName()
    {
        var ex = Assert.Throws<SkillParseException>(() => SkillParser.Parse("---\nname: a\ndescription: d\n---\n", expectName: "b"));
        Assert.Equal("name_mismatch", ex.Code);
    }

    // ── loader ────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "LoadAsync lists bundled resources as sorted /-relative paths, skipping dot-entries")]
    public async Task LoadListsResources()
    {
        var dir = SkillDir("demo", "---\nname: demo\ndescription: d\n---\nbody", new Dictionary<string, string>
        {
            ["scripts/run.sh"] = "echo",
            ["references/b.md"] = "b",
            ["references/a.md"] = "a",
            [".hidden"] = "no",
            ["assets/.DS_Store"] = "no",
        });
        var s = await SkillLoader.LoadAsync(dir);
        Assert.Equal(["references/a.md", "references/b.md", "scripts/run.sh"], s.Resources);
        Assert.Equal(dir, s.Path);
    }

    [Fact(DisplayName = "a folder whose name differs from the skill's is refused — unless strictName is off")]
    public async Task StrictName()
    {
        var dir = SkillDir("folder", "---\nname: other\ndescription: d\n---\n");
        var ex = await Assert.ThrowsAsync<SkillParseException>(() => SkillLoader.LoadAsync(dir));
        Assert.Equal("name_mismatch", ex.Code);
        Assert.Equal("other", (await SkillLoader.LoadAsync(dir, strictName: false)).Name);
    }

    [Fact(DisplayName = "a folder without SKILL.md is not a skill")]
    public async Task NotASkill()
    {
        var dir = Path.Combine(_work, "plain");
        Directory.CreateDirectory(dir);
        var ex = await Assert.ThrowsAsync<SkillParseException>(() => SkillLoader.LoadAsync(dir));
        Assert.Equal("not_a_skill", ex.Code);
    }

    [Fact(DisplayName = "DiscoverAsync: later roots override, non-skill folders are skipped, absent roots are empty")]
    public async Task Discover()
    {
        var rootA = Path.Combine(_work, "roots", "a");
        var rootB = Path.Combine(_work, "roots", "b");
        foreach (var (root, desc) in new[] { (rootA, "from a"), (rootB, "from b") })
        {
            Directory.CreateDirectory(Path.Combine(root, "shared"));
            File.WriteAllText(Path.Combine(root, "shared", "SKILL.md"), $"---\nname: shared\ndescription: {desc}\n---\n");
        }
        Directory.CreateDirectory(Path.Combine(rootA, "only-a"));
        File.WriteAllText(Path.Combine(rootA, "only-a", "SKILL.md"), "---\nname: only-a\ndescription: a\n---\n");
        Directory.CreateDirectory(Path.Combine(rootA, "not-a-skill"));
        File.WriteAllText(Path.Combine(rootA, "loose-file.md"), "ignored");

        var skills = await SkillLoader.DiscoverAsync([rootA, rootB, Path.Combine(_work, "missing")]);
        Assert.Equal([("only-a", "a"), ("shared", "from b")], skills.Select(s => (s.Name, s.Description)).ToArray());
    }

    [Fact(DisplayName = "a malformed skill fails discovery loudly instead of vanishing")]
    public async Task BrokenSkillFails()
    {
        var root = Path.Combine(_work, "broken-root");
        Directory.CreateDirectory(Path.Combine(root, "bad"));
        File.WriteAllText(Path.Combine(root, "bad", "SKILL.md"), "no frontmatter");
        var ex = await Assert.ThrowsAsync<SkillParseException>(() => SkillLoader.DiscoverAsync(root));
        Assert.Equal("missing_frontmatter", ex.Code);
    }

    // ── SkillCatalog ──────────────────────────────────────────────────────────

    private static Skill Inline(string name, string? description = null, string? instructions = null) => new()
    {
        Name = name,
        Description = description ?? $"about {name}",
        Instructions = instructions ?? $"do {name}",
    };

    [Fact(DisplayName = "lists metadata sorted by name and gets a skill by name")]
    public void CatalogListsAndGets()
    {
        var cat = new SkillCatalog([Inline("zeta"), Inline("alpha")]);
        Assert.Equal(2, cat.Count);
        Assert.Equal(["alpha", "zeta"], cat.List().Select(s => s.Name).ToArray());
        Assert.IsNotType<Skill>(cat.List()[0]);
        Assert.Equal("do zeta", cat.Get("zeta")!.Instructions);
        Assert.Null(cat.Get("nope"));
        Assert.True(cat.Has("alpha"));
    }

    [Fact(DisplayName = "duplicate names are refused")]
    public void CatalogDuplicates()
    {
        var ex = Assert.Throws<SkillParseException>(() => new SkillCatalog([Inline("a"), Inline("a")]));
        Assert.Equal("duplicate_skill", ex.Code);
    }

    [Fact(DisplayName = "FromDirectoriesAsync + ReadResourceAsync stay inside the skill folder")]
    public async Task ReadResource()
    {
        var cat = await SkillCatalog.FromDirectoriesAsync(Fixtures);
        Assert.Equal(["brand-voice", "pdf"], cat.List().Select(s => s.Name).ToArray());
        Assert.Contains("snake_case", await cat.ReadResourceAsync("pdf", "references/forms.md"));
        foreach (var bad in new[] { "../brand-voice/SKILL.md", "/etc/passwd", "references/../../brand-voice/SKILL.md", "." })
        {
            var ex = await Assert.ThrowsAsync<SkillParseException>(() => cat.ReadResourceAsync("pdf", bad));
            Assert.Equal("outside_skill", ex.Code);
        }
        Assert.Equal("unknown_skill", (await Assert.ThrowsAsync<SkillParseException>(() => cat.ReadResourceAsync("nope", "x"))).Code);
        var inlineCat = new SkillCatalog([Inline("mem")]);
        Assert.Equal("no_resources", (await Assert.ThrowsAsync<SkillParseException>(() => inlineCat.ReadResourceAsync("mem", "x"))).Code);
    }

    // ── SkillPrompt ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "empty list renders nothing; default intro, XML escaping, intro overrides")]
    public void Prompt()
    {
        Assert.Equal("", SkillPrompt.Render([]));
        var skills = new[] { new SkillMetadata { Name = "a", Description = "x < y & z" } };
        Assert.Equal(
            $"{SkillPrompt.DefaultIntro}\n\n<available_skills>\n  <skill>\n    <name>a</name>\n    <description>x &lt; y &amp; z</description>\n  </skill>\n</available_skills>",
            SkillPrompt.Render(skills));
        Assert.StartsWith("<available_skills>", SkillPrompt.Render(skills, intro: null));
        Assert.StartsWith("Custom.\n\n<available_skills>", SkillPrompt.Render(skills, intro: "Custom."));
    }

    // ── skill node types (graphs as data) ─────────────────────────────────────

    private static readonly SkillCatalog Source = new([Inline("brand-voice", "Voice.", "Short sentences.")]);

    [Fact(DisplayName = "a `skill` node writes the instructions to a channel; `skills` writes the catalog prompt")]
    public async Task SkillNodesRun()
    {
        var spec = new GraphSpec
        {
            Name = "briefed",
            Channels = new Dictionary<string, SpecChannel> { ["system"] = new(), ["catalog"] = new() },
            Nodes =
            [
                new SpecNode("brief", "skill", new Dictionary<string, object?> { ["skill"] = "brand-voice", ["to"] = "system" }),
                new SpecNode("menu", "skills", new Dictionary<string, object?> { ["to"] = "catalog", ["intro"] = null }),
            ],
            Edges = [new SpecEdge(Graph.Start, "brief"), new SpecEdge("brief", "menu"), new SpecEdge("menu", Graph.End)],
        };
        var g = Spec.FromSpec(spec, SkillNodes.Registry(Source)).Compile();
        var result = await IlmekRuntime.RunAsync(g, new Dictionary<string, object?>(), new RunOptions { ThreadId = "t1" });
        Assert.Equal(RunStatus.Done, result.Status);
        Assert.Equal("Short sentences.", result.State!.Get<string>("system"));
        Assert.StartsWith("<available_skills>", result.State!.Get<string>("catalog"));
    }

    [Fact(DisplayName = "an unknown skill fails at build time")]
    public void UnknownSkillFailsEarly()
    {
        SpecNode Node(Dictionary<string, object?> config) => new("brief", "skill", config);
        GraphSpec Spec1(SpecNode node) => new()
        {
            Name = "bad",
            Channels = new Dictionary<string, SpecChannel> { ["instructions"] = new() },
            Nodes = [node],
            Edges = [new SpecEdge(Graph.Start, "brief"), new SpecEdge("brief", Graph.End)],
        };
        var ex = Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(Node(new() { ["skill"] = "missing" })), SkillNodes.Registry(Source)));
        Assert.Contains("does not hold", ex.Message);
        var ex2 = Assert.Throws<GraphException>(() => Spec.FromSpec(Spec1(Node(new())), SkillNodes.Registry(Source)));
        Assert.Contains("config.skill", ex2.Message);
    }
}

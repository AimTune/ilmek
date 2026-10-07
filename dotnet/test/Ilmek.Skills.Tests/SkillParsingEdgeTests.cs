using Ilmek.Skills;

namespace Ilmek.Skills.Tests;

/// <summary>Malformed and awkward SKILL.md documents, and catalog lookups by unknown names.</summary>
public sealed class SkillParsingEdgeTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("ilmek-skills-edge-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    [Theory(DisplayName = "a SKILL.md missing its name or description is refused with a stable code")]
    [InlineData("invalid_name", "---\ndescription: d\n---\nbody")]
    [InlineData("invalid_name", "---\nname:\ndescription: d\n---\n")]
    [InlineData("invalid_name", "---\nname: [a, b]\ndescription: d\n---\n")]
    [InlineData("invalid_name", "---\nname: UPPER\ndescription: d\n---\n")]
    [InlineData("invalid_description", "---\nname: ok\n---\n")]
    [InlineData("invalid_description", "---\nname: ok\ndescription:    \n---\n")]
    [InlineData("invalid_description", "---\nname: ok\ndescription: [a, b]\n---\n")]
    [InlineData("missing_frontmatter", "")]
    [InlineData("missing_frontmatter", "name: ok\ndescription: d\n")]
    [InlineData("unterminated_frontmatter", "---\nname: ok\ndescription: d\n")]
    [InlineData("invalid_field", "---\nname: ok\ndescription: d\nallowed-tools:\n  nested: map\n---\n")]
    [InlineData("invalid_field", "---\nname: ok\ndescription: d\ncompatibility: [a]\n---\n")]
    public void MalformedRefused(string code, string markdown)
    {
        var ex = Assert.Throws<SkillParseException>(() => SkillParser.Parse(markdown));
        Assert.Equal(code, ex.Code);
    }

    [Fact(DisplayName = "a SKILL.md saved with a BOM and CRLF line endings loads from disk like a clean one")]
    public async Task BomAndCrlfOnDisk()
    {
        var dir = Path.Combine(_work, "crlf");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"),
            "---\r\nname: crlf\r\ndescription: Windows-saved.\r\n---\r\n\r\nLine one.\r\nLine two.\r\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var skill = await SkillLoader.LoadAsync(dir);
        Assert.Equal("crlf", skill.Name);
        Assert.Equal("Windows-saved.", skill.Description);
        Assert.DoesNotContain('\r', skill.Instructions);
        Assert.Equal("Line one.\nLine two.", skill.Instructions);
    }

    [Fact(DisplayName = "a description of exactly 1024 characters is accepted; whitespace around it is trimmed")]
    public void DescriptionBoundary()
    {
        var max = new string('d', SkillParser.DescriptionMax);
        Assert.Equal(max, SkillParser.Parse($"---\nname: ok\ndescription:   {max}  \n---\n").Description);
    }

    [Fact(DisplayName = "unicode in the description and body round-trips")]
    public void UnicodeContent()
    {
        var s = SkillParser.Parse("---\nname: ok\ndescription: Örgü ilmeği 🧶 — 编织\n---\nשלום\n");
        Assert.Equal("Örgü ilmeği 🧶 — 编织", s.Description);
        Assert.Equal("שלום", s.Instructions);
    }

    [Fact(DisplayName = "progressive disclosure: List carries metadata only, Get carries the instructions, resources load on demand")]
    public async Task ProgressiveDisclosure()
    {
        var dir = Path.Combine(_work, "roots", "guide");
        Directory.CreateDirectory(Path.Combine(dir, "references"));
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"), "---\nname: guide\ndescription: How to.\n---\nSECRET INSTRUCTIONS\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "references", "deep.md"), "level three");

        var cat = await SkillCatalog.FromDirectoriesAsync(Path.Combine(_work, "roots"));
        var listed = Assert.Single(cat.List());
        Assert.Equal(typeof(SkillMetadata), listed.GetType()); // not the Skill subtype: no instructions leak
        Assert.Equal("How to.", listed.Description);
        Assert.Equal("SECRET INSTRUCTIONS", cat.Get("guide")!.Instructions);
        Assert.Equal(new[] { "references/deep.md" }, cat.Get("guide")!.Resources);

        // Level 3 is read at call time, not at load: an edit after loading shows up.
        await File.WriteAllTextAsync(Path.Combine(dir, "references", "deep.md"), "edited");
        Assert.Equal("edited", await cat.ReadResourceAsync("guide", "references/deep.md"));
    }

    [Theory(DisplayName = "unknown skill names are refused by ReadResource and absent from Get")]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData("../guide")]
    [InlineData("GUIDE")]
    public async Task UnknownSkillName(string name)
    {
        var cat = new SkillCatalog([new Skill { Name = "guide", Description = "d", Instructions = "i" }]);
        Assert.Null(cat.Get(name));
        Assert.False(cat.Has(name));
        var ex = await Assert.ThrowsAsync<SkillParseException>(() => cat.ReadResourceAsync(name, "x.md"));
        Assert.Equal("unknown_skill", ex.Code);
    }
}

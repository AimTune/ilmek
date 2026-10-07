using System.Diagnostics;
using Ilmek.Skills;

namespace Ilmek.Skills.Tests;

/// <summary>
/// ReadResourceAsync takes a model-supplied path, so it is an attack surface:
/// nothing a model can type may read outside the skill folder. Every traversal
/// form — dot-dot, absolute, rooted, drive-relative, UNC, device, backslash,
/// encoded — and every link (symlink, junction) must be refused.
/// </summary>
public sealed class SkillResourceSecurityTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("ilmek-skills-sec-").FullName;
    private readonly string _skillDir;
    private readonly string _secret;

    public SkillResourceSecurityTests()
    {
        // <work>/skills/pdf/...   the skill
        // <work>/secret.txt       what an attacker wants
        // <work>/skills/pdf-evil  a sibling whose name shares the skill's prefix
        _skillDir = Path.Combine(_work, "skills", "pdf");
        Directory.CreateDirectory(Path.Combine(_skillDir, "references"));
        File.WriteAllText(Path.Combine(_skillDir, "SKILL.md"), "---\nname: pdf\ndescription: PDFs.\n---\nUse forms.\n");
        File.WriteAllText(Path.Combine(_skillDir, "references", "forms.md"), "inside");
        _secret = Path.Combine(_work, "secret.txt");
        File.WriteAllText(_secret, "TOP SECRET");
        Directory.CreateDirectory(Path.Combine(_work, "skills", "pdf-evil"));
        File.WriteAllText(Path.Combine(_work, "skills", "pdf-evil", "x.md"), "TOP SECRET");
    }

    public void Dispose()
    {
        // Remove links first (without descending into them, a loop would never
        // end) so the recursive delete can never follow one out of _work.
        RemoveLinks(new DirectoryInfo(_work));
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private static void RemoveLinks(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                try { entry.Delete(); } catch (IOException) { }
            }
            else if (entry is DirectoryInfo sub)
            {
                RemoveLinks(sub);
            }
        }
    }

    private Task<SkillCatalog> Catalog() => SkillCatalog.FromDirectoriesAsync(Path.Combine(_work, "skills"));

    private static async Task<string> Refused(SkillCatalog cat, string path)
    {
        var ex = await Assert.ThrowsAsync<SkillParseException>(() => cat.ReadResourceAsync("pdf", path));
        Assert.Equal("outside_skill", ex.Code);
        return ex.Message;
    }

    // ── lexical traversal ───────────────────────────────────────────────────

    public static TheoryData<string> TraversalPaths()
    {
        var data = new TheoryData<string>
        {
            "../secret.txt", "../../secret.txt", "references/../../../secret.txt", "./../../secret.txt",
            "references/./../../secret.txt", "../pdf-evil/x.md", "..", "../", ".", "", "references/..",
            "/etc/passwd", "/", "//server/share/x",
        };
        if (OperatingSystem.IsWindows())
        {
            foreach (var p in new[]
                     {
                         @"..\secret.txt", @"references\..\..\..\secret.txt", @"..\pdf-evil\x.md", @"\Windows\win.ini",
                         @"C:\Windows\win.ini", "C:/Windows/win.ini", "C:secret.txt", "c:", @"\\server\share\x",
                         @"\\?\C:\Windows\win.ini", @"\\.\C:\Windows\win.ini", @"..\\..\secret.txt", "references/..\\..\\..\\secret.txt",
                         // "..." is trimmed to "" by Windows, so these climb exactly once too many:
                         "...\\..\\..\\secret.txt", ".. \\..\\..\\secret.txt",
                     })
                data.Add(p);
        }
        return data;
    }

    [Theory(DisplayName = "every lexical traversal form is refused as outside_skill")]
    [MemberData(nameof(TraversalPaths))]
    public async Task LexicalTraversalRefused(string path) => await Refused(await Catalog(), path);

    [Theory(DisplayName = "percent-encoded and look-alike dots are never decoded into a traversal")]
    [InlineData("%2e%2e/secret.txt")]
    [InlineData("%2e%2e%5csecret.txt")]
    [InlineData("\uFF0E\uFF0E/secret.txt")] // fullwidth full stops
    public async Task EncodedFormsStayInside(string path)
    {
        // Taken literally, they name a file inside the skill that does not exist.
        var cat = await Catalog();
        var ex = await Assert.ThrowsAnyAsync<IOException>(() => cat.ReadResourceAsync("pdf", path));
        Assert.True(ex is FileNotFoundException or DirectoryNotFoundException, ex.GetType().Name);
    }

    [Theory(DisplayName = "a literal name that merely starts with two dots is refused (conservative), never read outside")]
    [InlineData("..%2fsecret.txt")]
    [InlineData("..%c0%afsecret.txt")]
    [InlineData("..hidden.md")]
    public async Task LeadingDoubleDotNameRefused(string path) => await Refused(await Catalog(), path);

    [Fact(DisplayName = "a path containing NUL is refused as outside_skill before touching the filesystem")]
    public async Task NulRefused()
    {
        var message = await Refused(await Catalog(), "references/forms.md\0../../secret.txt");
        Assert.Contains("NUL", message);
    }

    [Fact(DisplayName = "a null path is an ArgumentNullException")]
    public async Task NullPath()
    {
        var cat = await Catalog();
        await Assert.ThrowsAsync<ArgumentNullException>(() => cat.ReadResourceAsync("pdf", null!));
    }

    [Fact(DisplayName = "a legitimate resource, with either separator, reads; SKILL.md itself is readable")]
    public async Task LegitimateReads()
    {
        var cat = await Catalog();
        Assert.Equal("inside", await cat.ReadResourceAsync("pdf", "references/forms.md"));
        Assert.Equal("inside", await cat.ReadResourceAsync("pdf", "references/./forms.md"));
        Assert.Equal("inside", await cat.ReadResourceAsync("pdf", "references/../references/forms.md"));
        if (OperatingSystem.IsWindows())
            Assert.Equal("inside", await cat.ReadResourceAsync("pdf", @"references\forms.md"));
        Assert.Contains("Use forms.", await cat.ReadResourceAsync("pdf", "SKILL.md"));
    }

    [Fact(DisplayName = "a missing file inside the skill surfaces the platform's file-not-found exception")]
    public async Task MissingFile()
    {
        var cat = await Catalog();
        await Assert.ThrowsAsync<FileNotFoundException>(() => cat.ReadResourceAsync("pdf", "references/nope.md"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => cat.ReadResourceAsync("pdf", "nodir/nope.md"));
    }

    // ── links ───────────────────────────────────────────────────────────────

    /// <summary>A directory link: a symlink where allowed, else (Windows) a junction, which needs no privilege.</summary>
    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        if (!OperatingSystem.IsWindows()) return false;
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    /// <summary>A file symlink, or false when the OS refuses (Windows without Developer Mode).</summary>
    private static bool TryLinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    [Fact(DisplayName = "a directory link (symlink or junction) pointing outside the skill cannot be read through")]
    public async Task DirectoryLinkEscapeRefused()
    {
        Assert.True(TryLinkDirectory(Path.Combine(_skillDir, "references", "outside"), _work),
            "could not create a directory link or junction");
        var cat = await Catalog();

        var message = await Refused(cat, "references/outside/secret.txt");
        Assert.Contains("links outside", message);
        await Refused(cat, "references/outside/skills/pdf-evil/x.md");
        // ...and it is not advertised as a resource either.
        Assert.DoesNotContain(cat.Get("pdf")!.Resources, r => r.StartsWith("references/outside"));
    }

    [Fact(DisplayName = "a file symlink pointing outside the skill cannot be read (skipped where the OS forbids symlinks)")]
    public async Task FileLinkEscapeRefused()
    {
        // Windows without Developer Mode refuses file symlinks to non-admins;
        // the directory-junction test above covers the same code path there.
        if (!TryLinkFile(Path.Combine(_skillDir, "references", "leak.md"), _secret)) return;
        var cat = await Catalog();

        Assert.Contains("links outside", await Refused(cat, "references/leak.md"));
        Assert.DoesNotContain("references/leak.md", cat.Get("pdf")!.Resources);
    }

    [Fact(DisplayName = "a link that stays inside the skill is still readable")]
    public async Task InsideLinkReadable()
    {
        Assert.True(TryLinkDirectory(Path.Combine(_skillDir, "alias"), Path.Combine(_skillDir, "references")),
            "could not create a directory link or junction");
        var cat = await Catalog();
        Assert.Equal("inside", await cat.ReadResourceAsync("pdf", "alias/forms.md"));
        // Each real directory is listed once, under its real path (as in TS).
        Assert.Equal(new[] { "references/forms.md" }, cat.Get("pdf")!.Resources);
    }

    [Fact(DisplayName = "a link reached before its real directory is still listed once, by its real path")]
    public async Task RealPathWinsRegardlessOfOrder()
    {
        // "a-alias" sorts and enumerates before "z-real"; the real path still wins.
        var real = Path.Combine(_skillDir, "z-real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "doc.md"), "real");
        Assert.True(TryLinkDirectory(Path.Combine(_skillDir, "a-alias"), real), "could not create a directory link or junction");

        var skill = await SkillLoader.LoadAsync(_skillDir);
        Assert.Contains("z-real/doc.md", skill.Resources);
        Assert.DoesNotContain("a-alias/doc.md", skill.Resources);
    }

    [Fact(DisplayName = "a dangling link is skipped by the resource walk")]
    public async Task DanglingLinkSkipped()
    {
        Assert.True(TryLinkDirectory(Path.Combine(_skillDir, "ghost"), Path.Combine(_skillDir, "not-there")),
            "could not create a directory link or junction");
        var skill = await SkillLoader.LoadAsync(_skillDir);
        Assert.Equal(new[] { "references/forms.md" }, skill.Resources);
    }

    [Fact(DisplayName = "a link to the skill folder itself cannot loop the resource walk")]
    public async Task LinkLoopTerminates()
    {
        Assert.True(TryLinkDirectory(Path.Combine(_skillDir, "references", "loop"), _skillDir),
            "could not create a directory link or junction");

        var skill = await SkillLoader.LoadAsync(_skillDir);
        Assert.Contains("references/forms.md", skill.Resources);
        Assert.DoesNotContain(skill.Resources, r => r.Contains("loop/references/loop"));
    }

    [Fact(DisplayName = "the skill root itself being reached through a link is fine")]
    public async Task RootThroughLink()
    {
        var linkedRoot = Path.Combine(_work, "linked-skills");
        Assert.True(TryLinkDirectory(linkedRoot, Path.Combine(_work, "skills")),
            "could not create a directory link or junction");

        var cat = await SkillCatalog.FromDirectoriesAsync(linkedRoot);
        Assert.Equal("inside", await cat.ReadResourceAsync("pdf", "references/forms.md"));
        await Refused(cat, "../pdf-evil/x.md");
    }
}

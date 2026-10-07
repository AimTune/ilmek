namespace Ilmek.Skills;

/// <summary>
/// Loading skills from disk: one folder → one <see cref="Skill"/>, a root → every
/// skill folder under it. The only place in the package that touches the
/// filesystem. Mirror of <c>@ilmek/skills</c>' <c>loadSkill</c> / <c>discoverSkills</c>.
/// </summary>
public static class SkillLoader
{
    /// <summary>The file every skill folder must contain at its root.</summary>
    public const string SkillFile = "SKILL.md";

    /// <summary>
    /// Load the skill in <paramref name="dir"/>: read its SKILL.md, list its bundled
    /// resources. Throws <see cref="SkillParseException"/> on a malformed SKILL.md
    /// (<c>not_a_skill</c> when the folder has none).
    /// </summary>
    /// <param name="dir">The skill folder.</param>
    /// <param name="strictName">
    /// Require the frontmatter <c>name</c> to equal the folder name — the format's
    /// rule, and what makes a skill addressable by the path it was found at. Default
    /// true; turn it off to load a folder whose name is not the skill's.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<Skill> LoadAsync(string dir, bool strictName = true, CancellationToken ct = default)
    {
        var path = System.IO.Path.GetFullPath(dir).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var file = System.IO.Path.Combine(path, SkillFile);
        if (!File.Exists(file)) throw new SkillParseException("not_a_skill", $"{path} has no {SkillFile}");
        var markdown = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        var resources = ListResources(path);
        var expectName = strictName ? System.IO.Path.GetFileName(path) : null;
        return SkillParser.Parse(markdown, path, expectName, resources);
    }

    /// <summary>
    /// Load every skill folder directly under each root — a folder counts when it
    /// holds a SKILL.md; anything else is skipped silently. A malformed SKILL.md still
    /// throws: a broken skill should fail loudly, not vanish from the catalog.
    /// </summary>
    /// <remarks>
    /// Roots are read in order and a later root's skill <b>replaces</b> an earlier one
    /// of the same name (project skills over shared ones). The result is sorted by name.
    /// </remarks>
    public static async Task<IReadOnlyList<Skill>> DiscoverAsync(IEnumerable<string> roots, bool strictName = true, CancellationToken ct = default)
    {
        var byName = new Dictionary<string, Skill>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var rootPath = System.IO.Path.GetFullPath(root);
            if (!Directory.Exists(rootPath)) continue; // an absent root holds no skills
            foreach (var dir in Directory.EnumerateDirectories(rootPath).OrderBy(d => System.IO.Path.GetFileName(d), StringComparer.Ordinal))
            {
                if (!File.Exists(System.IO.Path.Combine(dir, SkillFile))) continue;
                var skill = await LoadAsync(dir, strictName, ct).ConfigureAwait(false);
                byName[skill.Name] = skill;
            }
        }
        return byName.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Overload of <see cref="DiscoverAsync(IEnumerable{string}, bool, CancellationToken)"/> for a single root.</summary>
    public static Task<IReadOnlyList<Skill>> DiscoverAsync(string root, bool strictName = true, CancellationToken ct = default) =>
        DiscoverAsync([root], strictName, ct);

    /// <summary>Every file under <paramref name="dir"/> except SKILL.md and dot-entries, as sorted <c>/</c>-relative paths.</summary>
    private static List<string> ListResources(string dir)
    {
        // Links (symlinks, junctions) are followed only when their real target is
        // strictly inside the skill: one pointing out would advertise a resource
        // ReadResourceAsync refuses, and one pointing at an ancestor would make the
        // walk recurse until the path is too long. Each real directory is walked
        // once, real paths first — links are deferred until every plain entry has
        // been seen, so "references/a.md" wins over "alias/a.md". Dangling links
        // are skipped. Mirrors the TS walk.
        var result = new List<string>();
        var realRoot = RealPath.Resolve(dir);
        var visited = new HashSet<string>(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        { realRoot };
        var links = new Queue<FileSystemInfo>();

        Walk(dir);
        while (links.Count > 0)
        {
            var link = links.Dequeue();
            var real = RealPath.Resolve(link.FullName);
            if (!RealPath.IsStrictlyInside(realRoot, real)) continue;
            if (Directory.Exists(real))
            {
                if (visited.Add(real)) Walk(link.FullName);
            }
            else if (File.Exists(real) && !(link.Name == SkillFile && System.IO.Path.GetDirectoryName(link.FullName) == dir))
            {
                Add(link);
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result;

        void Walk(string current)
        {
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if (entry.Name.StartsWith('.')) continue;
                if (entry.LinkTarget is not null)
                {
                    links.Enqueue(entry);
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    if (visited.Add(RealPath.Resolve(entry.FullName))) Walk(entry.FullName);
                    continue;
                }
                if (current == dir && entry.Name == SkillFile) continue;
                Add(entry);
            }
        }

        void Add(FileSystemInfo entry) =>
            result.Add(System.IO.Path.GetRelativePath(dir, entry.FullName).Replace('\\', '/'));
    }
}

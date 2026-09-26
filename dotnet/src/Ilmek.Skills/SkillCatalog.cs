namespace Ilmek.Skills;

/// <summary>
/// Where skills come from, as a host sees them — the port behind the three
/// levels of progressive disclosure. Level 1 is <see cref="List"/>, level 2 is
/// <see cref="Get"/>, level 3 is <see cref="ReadResourceAsync"/>.
/// </summary>
public interface ISkillSource
{
    /// <summary>Every skill's level-1 metadata, sorted by name. What goes in the system prompt.</summary>
    IReadOnlyList<SkillMetadata> List();

    /// <summary>The whole skill — instructions included — or null when no skill has that name.</summary>
    Skill? Get(string name);

    /// <summary>
    /// The text of one bundled file (<see cref="Skill.Resources"/> names them).
    /// Throws <see cref="SkillParseException"/> (<c>no_resources</c>) for a source
    /// with nothing on disk.
    /// </summary>
    Task<string> ReadResourceAsync(string name, string path, CancellationToken ct = default);
}

/// <summary>
/// An in-memory <see cref="ISkillSource"/> over loaded skills — from folders via
/// <see cref="FromDirectoriesAsync(IEnumerable{string}, bool, CancellationToken)"/>,
/// or from objects for inline and test skills. Names are unique; a duplicate
/// throws at construction. Mirror of <c>@ilmek/skills</c>' <c>SkillCatalog</c>.
/// </summary>
public sealed class SkillCatalog : ISkillSource
{
    private readonly SortedDictionary<string, Skill> _skills = new(StringComparer.Ordinal);

    public SkillCatalog(IEnumerable<Skill> skills)
    {
        foreach (var skill in skills)
        {
            if (_skills.ContainsKey(skill.Name))
                throw new SkillParseException("duplicate_skill", $"two skills are named \"{skill.Name}\"");
            _skills[skill.Name] = skill;
        }
    }

    /// <summary>Discover every skill folder under the given roots (see <see cref="SkillLoader.DiscoverAsync(IEnumerable{string}, bool, CancellationToken)"/>) and catalog them.</summary>
    public static async Task<SkillCatalog> FromDirectoriesAsync(IEnumerable<string> roots, bool strictName = true, CancellationToken ct = default) =>
        new(await SkillLoader.DiscoverAsync(roots, strictName, ct).ConfigureAwait(false));

    /// <summary>Single-root overload of <see cref="FromDirectoriesAsync(IEnumerable{string}, bool, CancellationToken)"/>.</summary>
    public static Task<SkillCatalog> FromDirectoriesAsync(string root, bool strictName = true, CancellationToken ct = default) =>
        FromDirectoriesAsync([root], strictName, ct);

    /// <summary>How many skills the catalog holds.</summary>
    public int Count => _skills.Count;

    public bool Has(string name) => _skills.ContainsKey(name);

    public IReadOnlyList<SkillMetadata> List() => _skills.Values.Select(s => s.ToMetadata()).ToList();

    public Skill? Get(string name) => _skills.GetValueOrDefault(name);

    /// <summary>Every skill, instructions included, sorted by name.</summary>
    public IReadOnlyList<Skill> All() => _skills.Values.ToList();

    /// <summary>
    /// Read one bundled file of a skill. <paramref name="path"/> is <c>/</c>-relative to
    /// the skill folder and must stay inside it — <c>..</c> segments and absolute paths
    /// are refused — so a model-supplied path can never reach outside the skill.
    /// </summary>
    public async Task<string> ReadResourceAsync(string name, string path, CancellationToken ct = default)
    {
        var skill = _skills.GetValueOrDefault(name)
            ?? throw new SkillParseException("unknown_skill", $"no skill named \"{name}\"");
        if (skill.Path is null)
            throw new SkillParseException("no_resources", $"skill \"{name}\" is inline and has no files on disk");

        var root = System.IO.Path.GetFullPath(skill.Path);
        if (System.IO.Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\'))
            throw new SkillParseException("outside_skill", $"resource paths are relative to the skill folder: {path}");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
        var rel = System.IO.Path.GetRelativePath(root, full);
        if (rel == "." || rel.Length == 0 || rel.StartsWith("..") || System.IO.Path.IsPathRooted(rel))
            throw new SkillParseException("outside_skill", $"resource \"{path}\" is outside skill \"{name}\"");
        return await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
    }
}

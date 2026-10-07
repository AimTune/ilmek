namespace Ilmek.Skills;

/// <summary>
/// Link-aware path containment for the skill folder.
///
/// A lexical check (<c>GetFullPath</c> + <c>GetRelativePath</c>) cannot see a
/// symlink or junction: <c>references/link</c> looks inside the skill while the
/// file it opens lives anywhere on the disk. These helpers resolve every link
/// along a path before asking "is it inside?".
/// </summary>
internal static class RealPath
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The path with every symlink/junction along it resolved to its final
    /// target, component by component. Components that do not exist are kept
    /// verbatim (the caller's open will report the missing file).
    /// </summary>
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        var parts = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is null) continue; // not a link (or not there at all)
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            // An unresolvable link (dangling, or a loop the OS gives up on) has no
            // real location — return something that cannot be inside any root.
            if (target is null) return "\0unresolvable";
            current = Path.GetFullPath(target.FullName);
        }
        return current;
    }

    /// <summary>Is <paramref name="path"/> strictly inside <paramref name="root"/>? Both must already be resolved.</summary>
    public static bool IsStrictlyInside(string root, string path)
    {
        var r = Path.TrimEndingDirectorySeparator(root);
        return path.Length > r.Length + 1
            && path.StartsWith(r, PathComparison)
            && (path[r.Length] == Path.DirectorySeparatorChar || path[r.Length] == Path.AltDirectorySeparatorChar);
    }
}

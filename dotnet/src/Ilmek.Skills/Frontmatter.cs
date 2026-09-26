using System.Text.RegularExpressions;

namespace Ilmek.Skills;

/// <summary>Why a SKILL.md could not be read. <see cref="Code"/> is stable; the message is for humans.</summary>
public sealed class SkillParseException : Exception
{
    public string Code { get; }
    public SkillParseException(string code, string message) : base(message) => Code = code;
}

/// <summary>A parsed frontmatter block and the markdown body that followed it.</summary>
/// <remarks>
/// Values are <see cref="string"/>, <c>List&lt;object?&gt;</c> (a sequence) or
/// <c>Dictionary&lt;string, object?&gt;</c> (a mapping). Every scalar is a string —
/// the skill layer interprets.
/// </remarks>
public sealed record Frontmatter(IReadOnlyDictionary<string, object?> Data, string Body);

/// <summary>
/// A deliberately small YAML reader for SKILL.md frontmatter — the mirror of
/// <c>@ilmek/skills</c>' <c>parseFrontmatter</c>.
/// </summary>
/// <remarks>
/// The Agent Skills format needs a handful of shapes — <c>key: value</c>, a nested
/// mapping (<c>metadata:</c>), a block or flow sequence, quoted strings and
/// <c>|</c>/<c>&gt;</c> block scalars — and nothing else. Implementing exactly that
/// keeps the package dependency-free and keeps the two languages' readers
/// byte-identical on the same input (conformance/skills). Anchors, tags, flow
/// mappings and sequences of mappings are rejected with a clear error.
/// </remarks>
public static class FrontmatterReader
{
    private static readonly Regex KeyLine = new(@"^([^\s#""'\-][^:]*?|""[^""]*""|'[^']*')\s*:(?:\s+(.*))?$", RegexOptions.Compiled);
    private static readonly Regex BlockHeader = new(@"^[|>][+-]?$", RegexOptions.Compiled);
    private static readonly Regex CommentStart = new(@"\s#", RegexOptions.Compiled);

    /// <summary>
    /// Split a markdown document into its YAML frontmatter and body. The document
    /// MUST start with a <c>---</c> line (a UTF-8 BOM is tolerated) and close it
    /// with a second <c>---</c> (or <c>...</c>) line.
    /// </summary>
    public static Frontmatter Parse(string text)
    {
        var normalized = text.TrimStart((char)0xFEFF).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
            throw new SkillParseException("missing_frontmatter", "SKILL.md must start with a `---` YAML frontmatter block");

        var close = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            var t = lines[i].TrimEnd();
            if (t is "---" or "...")
            {
                close = i;
                break;
            }
        }
        if (close == -1)
            throw new SkillParseException("unterminated_frontmatter", "the frontmatter block is not closed by a `---` line");

        var cursor = new Cursor(lines[1..close]);
        var data = ParseMapping(cursor, 0);
        cursor.SkipBlank();
        if (cursor.I < cursor.Lines.Length)
            throw new SkillParseException("bad_indentation", $"unexpected indentation at frontmatter line {cursor.I + 2}");

        var bodyStart = close + 1;
        while (bodyStart < lines.Length && lines[bodyStart].Trim().Length == 0) bodyStart++;
        return new Frontmatter(data, string.Join("\n", lines[bodyStart..]));
    }

    private sealed class Cursor(string[] lines)
    {
        public readonly string[] Lines = lines;
        public int I;

        public (string Line, int Indent)? Peek()
        {
            for (var j = I; j < Lines.Length; j++)
            {
                if (IsSkippable(Lines[j])) continue;
                return (Lines[j], IndentOf(Lines[j]));
            }
            return null;
        }

        public void SkipBlank()
        {
            while (I < Lines.Length && IsSkippable(Lines[I])) I++;
        }
    }

    private static bool IsSkippable(string line) => line.Trim().Length == 0 || line.TrimStart().StartsWith('#');
    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    private static Dictionary<string, object?> ParseMapping(Cursor c, int indent)
    {
        var result = new Dictionary<string, object?>();
        while (true)
        {
            c.SkipBlank();
            var next = c.Peek();
            if (next is null || next.Value.Indent < indent) break;
            if (next.Value.Indent > indent)
                throw new SkillParseException("bad_indentation", $"unexpected indentation at frontmatter line {c.I + 2}");

            var content = next.Value.Line[indent..].TrimEnd();
            if (content.StartsWith("- ") || content == "-")
                throw new SkillParseException("bad_structure", $"expected a `key: value` line, found a sequence item at frontmatter line {c.I + 2}");

            var m = KeyLine.Match(content);
            if (!m.Success)
                throw new SkillParseException("bad_structure", $"expected a `key: value` line at frontmatter line {c.I + 2}: \"{content}\"");

            var key = Unquote(m.Groups[1].Value);
            var rest = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            c.I++;
            if (result.ContainsKey(key)) throw new SkillParseException("duplicate_key", $"frontmatter key \"{key}\" appears twice");
            result[key] = ParseValue(c, indent, rest);
        }
        return result;
    }

    private static object? ParseValue(Cursor c, int indent, string rest)
    {
        if (rest.Length == 0)
        {
            var next = c.Peek();
            if (next is not null && next.Value.Indent > indent)
            {
                var content = next.Value.Line[next.Value.Indent..];
                return content.StartsWith("- ") || content == "-"
                    ? ParseSequence(c, next.Value.Indent)
                    : ParseMapping(c, next.Value.Indent);
            }
            return "";
        }
        if (BlockHeader.IsMatch(rest)) return ParseBlockScalar(c, indent, rest);
        if (rest.StartsWith('['))
        {
            if (!rest.EndsWith(']')) throw new SkillParseException("bad_structure", $"unterminated flow sequence: {rest}");
            return SplitFlow(rest[1..^1]).Select(s => (object?)Scalar(s)).ToList();
        }
        if (rest.StartsWith('{'))
            throw new SkillParseException("unsupported", "flow mappings (`{…}`) are not supported in SKILL.md frontmatter");
        if (rest.StartsWith('&') || rest.StartsWith('*') || rest.StartsWith('!'))
            throw new SkillParseException("unsupported", "YAML anchors, aliases and tags are not supported in SKILL.md frontmatter");
        return Scalar(rest);
    }

    private static List<object?> ParseSequence(Cursor c, int indent)
    {
        var result = new List<object?>();
        while (true)
        {
            c.SkipBlank();
            var next = c.Peek();
            if (next is null || next.Value.Indent < indent) break;
            if (next.Value.Indent > indent)
                throw new SkillParseException("bad_indentation", $"unexpected indentation at frontmatter line {c.I + 2}");

            var content = next.Value.Line[indent..].TrimEnd();
            if (!(content.StartsWith("- ") || content == "-")) break; // back to the enclosing mapping
            var item = content == "-" ? "" : content[2..].Trim();
            c.I++;
            if (item.Length == 0)
            {
                var inner = c.Peek();
                if (inner is null || inner.Value.Indent <= indent)
                {
                    result.Add("");
                    continue;
                }
                throw new SkillParseException("unsupported", "nested blocks inside a sequence item are not supported in SKILL.md frontmatter");
            }
            var quoted = item.StartsWith('"') || item.StartsWith('\'');
            if (!quoted && KeyLine.IsMatch(item))
                throw new SkillParseException("unsupported", "sequences of mappings are not supported in SKILL.md frontmatter");
            result.Add(Scalar(item));
        }
        return result;
    }

    /// <summary><c>|</c> keeps newlines, <c>&gt;</c> folds them; <c>-</c> strips the final newline, <c>+</c> keeps all, neither clips to one.</summary>
    private static string ParseBlockScalar(Cursor c, int indent, string header)
    {
        var folded = header[0] == '>';
        var chomp = header.Length > 1 ? header[1] : '\0';
        var raw = new List<string>();
        var blockIndent = -1;
        while (c.I < c.Lines.Length)
        {
            var line = c.Lines[c.I];
            if (line.Trim().Length == 0)
            {
                raw.Add("");
                c.I++;
                continue;
            }
            var ind = IndentOf(line);
            if (ind <= indent) break;
            if (blockIndent == -1) blockIndent = ind;
            if (ind < blockIndent) break;
            raw.Add(line[blockIndent..]);
            c.I++;
        }
        var end = raw.Count;
        while (end > 0 && raw[end - 1].Length == 0) end--;
        var content = raw.Take(end).ToList();
        var trailing = raw.Count - end;

        string text;
        if (folded)
        {
            var acc = new System.Text.StringBuilder();
            for (var i = 0; i < content.Count; i++)
            {
                var line = content[i];
                if (line.Length == 0)
                {
                    acc.Append('\n');
                    continue;
                }
                var prev = i > 0 ? content[i - 1] : null;
                if (prev is not null && prev.Length != 0) acc.Append(' ');
                acc.Append(line);
            }
            text = acc.ToString();
        }
        else
        {
            text = string.Join("\n", content);
        }
        if (chomp == '-') return text;
        if (chomp == '+') return text + new string('\n', trailing + 1);
        return content.Count == 0 ? "" : text + "\n";
    }

    /// <summary>Split <c>a, "b, c", d</c> on top-level commas.</summary>
    private static List<string> SplitFlow(string inner)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var ch in inner)
        {
            if (quote is not null)
            {
                cur.Append(ch);
                if (ch == quote) quote = null;
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
                cur.Append(ch);
            }
            else if (ch == ',')
            {
                result.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(ch);
            }
        }
        result.Add(cur.ToString());
        return result.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>A scalar: strip a trailing <c> # comment</c> from a plain value, unquote a quoted one.</summary>
    private static string Scalar(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith('"') || s.StartsWith('\''))
        {
            var end = ClosingQuote(s);
            if (end == -1) throw new SkillParseException("bad_structure", $"unterminated quoted string: {s}");
            var tail = s[(end + 1)..].Trim();
            if (tail.Length != 0 && !tail.StartsWith('#'))
                throw new SkillParseException("bad_structure", $"unexpected text after a quoted string: {s}");
            return Unquote(s[..(end + 1)]);
        }
        var hash = CommentStart.Match(s);
        return (hash.Success ? s[..hash.Index] : s).Trim();
    }

    private static int ClosingQuote(string s)
    {
        var q = s[0];
        for (var i = 1; i < s.Length; i++)
        {
            var ch = s[i];
            if (q == '"' && ch == '\\')
            {
                i++;
                continue;
            }
            if (ch == q)
            {
                if (q == '\'' && i + 1 < s.Length && s[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                return i;
            }
        }
        return -1;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s.StartsWith('"') && s.EndsWith('"'))
        {
            var inner = s[1..^1];
            var sb = new System.Text.StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                var ch = inner[i];
                if (ch == '\\' && i + 1 < inner.Length)
                {
                    var e = inner[i + 1];
                    var mapped = e switch { '"' => "\"", '\\' => "\\", '/' => "/", 'n' => "\n", 'r' => "\r", 't' => "\t", _ => null };
                    if (mapped is not null)
                    {
                        sb.Append(mapped);
                        i++;
                        continue;
                    }
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }
        if (s.Length >= 2 && s.StartsWith('\'') && s.EndsWith('\'')) return s[1..^1].Replace("''", "'");
        return s;
    }
}

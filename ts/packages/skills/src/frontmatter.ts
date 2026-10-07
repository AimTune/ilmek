// A deliberately small YAML reader for SKILL.md frontmatter.
//
// The Agent Skills format needs a handful of shapes — `key: value`, a nested
// mapping (`metadata:`), a block or flow sequence, quoted strings and `|`/`>`
// block scalars — and nothing else. Implementing exactly that keeps
// `@ilmek/skills` dependency-free and, more importantly, keeps the TypeScript
// and .NET readers byte-identical on the same input (conformance/skills).
// Anchors, tags, multi-document streams and sequences of mappings are rejected
// with a clear error rather than half-supported.

/** A frontmatter value: every scalar is a string — the skill layer interprets. */
export type FrontmatterValue = string | FrontmatterValue[] | { [key: string]: FrontmatterValue };

export interface Frontmatter {
    /** The parsed `---` block. */
    readonly data: Record<string, FrontmatterValue>;
    /** Everything after the closing `---`, leading blank lines removed. */
    readonly body: string;
}

/** Why a SKILL.md could not be read. `code` is stable; `message` is for humans. */
export class SkillParseError extends Error {
    readonly code: string;
    constructor(code: string, message: string) {
        super(message);
        this.name = "SkillParseError";
        this.code = code;
    }
}

/**
 * Split a markdown document into its YAML frontmatter and body. The document
 * MUST start with a `---` line (a UTF-8 BOM is tolerated) and close it with a
 * second `---` (or `...`) line.
 */
export function parseFrontmatter(text: string): Frontmatter {
    const normalized = text.replace(/^﻿/, "").replace(/\r\n?/g, "\n");
    const lines = normalized.split("\n");
    if (lines[0]?.trimEnd() !== "---") {
        throw new SkillParseError("missing_frontmatter", "SKILL.md must start with a `---` YAML frontmatter block");
    }
    let close = -1;
    for (let i = 1; i < lines.length; i++) {
        const t = lines[i]!.trimEnd();
        if (t === "---" || t === "...") {
            close = i;
            break;
        }
    }
    if (close === -1) {
        throw new SkillParseError("unterminated_frontmatter", "the frontmatter block is not closed by a `---` line");
    }
    const cursor: Cursor = { lines: lines.slice(1, close), i: 0 };
    const data = parseMapping(cursor, 0);
    skipBlank(cursor);
    if (cursor.i < cursor.lines.length) {
        throw new SkillParseError("bad_indentation", `unexpected indentation at frontmatter line ${cursor.i + 2}`);
    }
    let bodyStart = close + 1;
    while (bodyStart < lines.length && lines[bodyStart]!.trim() === "") bodyStart++;
    return { data, body: lines.slice(bodyStart).join("\n") };
}

interface Cursor {
    readonly lines: readonly string[];
    i: number;
}

const isSkippable = (line: string): boolean => line.trim() === "" || line.trimStart().startsWith("#");
const indentOf = (line: string): number => line.length - line.trimStart().length;

/** The next meaningful line at or after the cursor, without consuming it. */
function peek(c: Cursor): { line: string; indent: number } | null {
    for (let j = c.i; j < c.lines.length; j++) {
        const line = c.lines[j]!;
        if (isSkippable(line)) continue;
        return { line, indent: indentOf(line) };
    }
    return null;
}

function skipBlank(c: Cursor): void {
    while (c.i < c.lines.length && isSkippable(c.lines[c.i]!)) c.i++;
}

const KEY_LINE = /^([^\s#"'\-][^:]*?|"[^"]*"|'[^']*')\s*:(?:\s+(.*))?$/;

function parseMapping(c: Cursor, indent: number): Record<string, FrontmatterValue> {
    const out: Record<string, FrontmatterValue> = {};
    for (;;) {
        skipBlank(c);
        const next = peek(c);
        if (!next || next.indent < indent) break;
        if (next.indent > indent) {
            throw new SkillParseError("bad_indentation", `unexpected indentation at frontmatter line ${c.i + 2}`);
        }
        const content = next.line.slice(indent).trimEnd();
        if (content.startsWith("- ") || content === "-") {
            throw new SkillParseError(
                "bad_structure",
                `expected a \`key: value\` line, found a sequence item at frontmatter line ${c.i + 2}`,
            );
        }
        const m = KEY_LINE.exec(content);
        if (!m) {
            throw new SkillParseError(
                "bad_structure",
                `expected a \`key: value\` line at frontmatter line ${c.i + 2}: ${JSON.stringify(content)}`,
            );
        }
        const key = unquote(m[1]!);
        const rest = m[2]?.trim() ?? "";
        c.i++;
        // Own properties only: `constructor` is a legal key, not a duplicate of
        // Object.prototype's, and `__proto__` must be stored, not assigned.
        if (Object.hasOwn(out, key)) throw new SkillParseError("duplicate_key", `frontmatter key ${JSON.stringify(key)} appears twice`);
        Object.defineProperty(out, key, { value: parseValue(c, indent, rest), writable: true, enumerable: true, configurable: true });
    }
    return out;
}

function parseValue(c: Cursor, indent: number, rest: string): FrontmatterValue {
    if (rest === "") {
        // Either a nested block follows (deeper indent) or the value is empty.
        const next = peek(c);
        if (next && next.indent > indent) {
            const content = next.line.slice(next.indent);
            return content.startsWith("- ") || content === "-"
                ? parseSequence(c, next.indent)
                : parseMapping(c, next.indent);
        }
        return "";
    }
    if (/^[|>][+-]?$/.test(rest)) return parseBlockScalar(c, indent, rest);
    if (rest.startsWith("[")) {
        if (!rest.endsWith("]")) throw new SkillParseError("bad_structure", `unterminated flow sequence: ${rest}`);
        return splitFlow(rest.slice(1, -1)).map(scalar);
    }
    if (rest.startsWith("{")) {
        throw new SkillParseError("unsupported", "flow mappings (`{…}`) are not supported in SKILL.md frontmatter");
    }
    if (rest.startsWith("&") || rest.startsWith("*") || rest.startsWith("!")) {
        throw new SkillParseError("unsupported", "YAML anchors, aliases and tags are not supported in SKILL.md frontmatter");
    }
    return scalar(rest);
}

function parseSequence(c: Cursor, indent: number): FrontmatterValue[] {
    const out: FrontmatterValue[] = [];
    for (;;) {
        skipBlank(c);
        const next = peek(c);
        if (!next || next.indent < indent) break;
        if (next.indent > indent) {
            throw new SkillParseError("bad_indentation", `unexpected indentation at frontmatter line ${c.i + 2}`);
        }
        const content = next.line.slice(indent).trimEnd();
        if (!(content.startsWith("- ") || content === "-")) break; // back to the enclosing mapping
        const item = content === "-" ? "" : content.slice(2).trim();
        c.i++;
        if (item === "") {
            const inner = peek(c);
            if (!inner || inner.indent <= indent) {
                out.push("");
                continue;
            }
            throw new SkillParseError("unsupported", "nested blocks inside a sequence item are not supported in SKILL.md frontmatter");
        }
        const quoted = item.startsWith('"') || item.startsWith("'");
        if (!quoted && KEY_LINE.test(item)) {
            throw new SkillParseError("unsupported", "sequences of mappings are not supported in SKILL.md frontmatter");
        }
        out.push(scalar(item));
    }
    return out;
}

/** `|` keeps newlines, `>` folds them; `-` strips the final newline, `+` keeps all, neither clips to one. */
function parseBlockScalar(c: Cursor, indent: number, header: string): string {
    const folded = header[0] === ">";
    const chomp = header[1] ?? "";
    const raw: string[] = [];
    let blockIndent = -1;
    while (c.i < c.lines.length) {
        const line = c.lines[c.i]!;
        if (line.trim() === "") {
            raw.push("");
            c.i++;
            continue;
        }
        const ind = indentOf(line);
        if (ind <= indent) break;
        if (blockIndent === -1) blockIndent = ind;
        if (ind < blockIndent) break;
        raw.push(line.slice(blockIndent));
        c.i++;
    }
    // Trailing blank lines belong to chomping, not to the content.
    let end = raw.length;
    while (end > 0 && raw[end - 1] === "") end--;
    const content = raw.slice(0, end);
    const trailing = raw.length - end;

    let text: string;
    if (folded) {
        let acc = "";
        for (let i = 0; i < content.length; i++) {
            const line = content[i]!;
            if (line === "") {
                acc += "\n";
                continue;
            }
            const prev = i > 0 ? content[i - 1]! : null;
            if (prev !== null && prev !== "") acc += " ";
            acc += line;
        }
        text = acc;
    } else {
        text = content.join("\n");
    }
    if (chomp === "-") return text;
    if (chomp === "+") return text + "\n".repeat(trailing + 1);
    return content.length === 0 ? "" : text + "\n";
}

/** Split `a, "b, c", d` on top-level commas. */
function splitFlow(inner: string): string[] {
    const out: string[] = [];
    let cur = "";
    let quote: string | null = null;
    for (const ch of inner) {
        if (quote !== null) {
            cur += ch;
            if (ch === quote) quote = null;
        } else if (ch === '"' || ch === "'") {
            quote = ch;
            cur += ch;
        } else if (ch === ",") {
            out.push(cur);
            cur = "";
        } else {
            cur += ch;
        }
    }
    out.push(cur);
    return out.map((s) => s.trim()).filter((s) => s !== "");
}

/** A scalar: strip a trailing ` # comment` from a plain value, unquote a quoted one. */
function scalar(raw: string): string {
    const s = raw.trim();
    if (s.startsWith('"') || s.startsWith("'")) {
        const end = closingQuote(s);
        if (end === -1) throw new SkillParseError("bad_structure", `unterminated quoted string: ${s}`);
        const tail = s.slice(end + 1).trim();
        if (tail !== "" && !tail.startsWith("#")) {
            throw new SkillParseError("bad_structure", `unexpected text after a quoted string: ${s}`);
        }
        return unquote(s.slice(0, end + 1));
    }
    const hash = s.search(/\s#/);
    return (hash === -1 ? s : s.slice(0, hash)).trim();
}

/** Index of the quote that closes the one `s` opens with, honouring `\"` and `''` escapes; -1 if none. */
function closingQuote(s: string): number {
    const q = s[0]!;
    for (let i = 1; i < s.length; i++) {
        const ch = s[i];
        if (q === '"' && ch === "\\") {
            i++;
            continue;
        }
        if (ch === q) {
            if (q === "'" && s[i + 1] === "'") {
                i++;
                continue;
            }
            return i;
        }
    }
    return -1;
}

const ESCAPES: Readonly<Record<string, string>> = { '"': '"', "\\": "\\", "/": "/", n: "\n", r: "\r", t: "\t" };

function unquote(s: string): string {
    if (s.length >= 2 && s.startsWith('"') && s.endsWith('"')) {
        return s.slice(1, -1).replace(/\\(["\\/nrt])/g, (_, ch: string) => ESCAPES[ch] ?? ch);
    }
    if (s.length >= 2 && s.startsWith("'") && s.endsWith("'")) return s.slice(1, -1).replace(/''/g, "'");
    return s;
}

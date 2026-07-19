namespace Ilmek;

/// <summary>
/// One streamed text delta (MODEL.md §10.2).
///
/// ilmek is LLM-agnostic — the core never calls a model. But "stream the answer
/// as it is generated" is the one streaming need every agent has, so ilmek fixes
/// a shape for it instead of leaving each caller to invent one. Emitted through
/// <see cref="IContext.EmitToken"/>, which lets the <c>Messages</c> stream mode
/// pick tokens out of the generic custom channel without guessing.
///
/// <para>Tokens ride the same transient side channel as <see cref="IContext.Emit"/>
/// — they are NOT journaled. On replay a node re-streams its tokens; only the
/// value it commits through a step is memoized. That is deliberate: a resumed run
/// should show its work again, not silently skip to the answer.</para>
/// </summary>
public sealed record TokenChunk(string Text, IReadOnlyDictionary<string, object?>? Meta = null);

using System.Runtime.CompilerServices;

namespace Ilmek;

/// <summary>
/// The classic LangGraph stream shapes, over ilmek's event stream (MODEL.md §10.1).
///
/// <list type="bullet">
/// <item><c>Values</c> — the full channel state after each superstep.</item>
/// <item><c>Updates</c> — one <c>{node: update}</c> per node that ran.</item>
/// <item><c>Custom</c> — every payload pushed through <see cref="IContext.Emit"/>.</item>
/// <item><c>Messages</c> — just the token deltas.</item>
/// <item><c>Debug</c> — every event, unchanged.</item>
/// </list>
/// </summary>
public enum StreamMode { Values, Updates, Custom, Messages, Debug }

/// <summary>One projected chunk. <see cref="Seq"/>/<see cref="Ns"/> echo the source event.</summary>
public sealed record StreamPart(StreamMode Mode, int Seq, IReadOnlyList<string> Ns, object? Data);

/// <summary>
/// Projection over the canonical stream (MODEL.md §10.1).
///
/// ilmek's native stream is a single sequence of typed events. This is the thin
/// view layer over it: LangGraph asks callers to pick a mode up front and hands
/// back mode-shaped chunks; ilmek keeps the one canonical stream and lets you
/// project it after the fact, so a reconnect buffer, a test, and a live run all
/// filter through the identical function.
/// </summary>
public static class Streaming
{
    /// <summary>Project one event into zero or more parts, one per requested mode it matches.</summary>
    public static IReadOnlyList<StreamPart> Project(IlmekEvent ev, IReadOnlyCollection<StreamMode> modes)
    {
        var parts = new List<StreamPart>();

        foreach (var mode in modes)
        {
            switch (mode)
            {
                case StreamMode.Values:
                    // The final superstep emits a state event before run_end, so
                    // this covers the terminal state too.
                    if (ev is StateEvent s) parts.Add(new StreamPart(mode, ev.Seq, ev.Ns, s.Channels));
                    break;

                case StreamMode.Updates:
                    if (ev is NodeEndEvent ne)
                        parts.Add(new StreamPart(mode, ev.Seq, ev.Ns,
                            new Dictionary<string, object?> { [ne.Node] = ne.StateUpdate }));
                    break;

                case StreamMode.Custom:
                    if (ev is CustomEvent c) parts.Add(new StreamPart(mode, ev.Seq, ev.Ns, c.Payload));
                    break;

                case StreamMode.Messages:
                    if (ev is CustomEvent { Payload: TokenChunk token })
                        parts.Add(new StreamPart(mode, ev.Seq, ev.Ns, token));
                    break;

                case StreamMode.Debug:
                    parts.Add(new StreamPart(mode, ev.Seq, ev.Ns, ev));
                    break;
            }
        }

        return parts;
    }

    /// <summary>
    /// Wrap any event source as a stream of parts in the requested modes.
    /// Composes with every producer — a fresh run, a resume, or a replay of
    /// buffered events.
    /// </summary>
    public static async IAsyncEnumerable<StreamPart> Projected(
        IAsyncEnumerable<IlmekEvent> source, IReadOnlyCollection<StreamMode> modes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in source.WithCancellation(ct).ConfigureAwait(false))
            foreach (var part in Project(ev, modes))
                yield return part;
    }

    /// <summary>Stream a run as parts in the given modes — the ergonomic entry point.</summary>
    public static IAsyncEnumerable<StreamPart> StreamModes(
        CompiledGraph graph, IReadOnlyDictionary<string, object?>? input,
        IReadOnlyCollection<StreamMode> modes, RunOptions? options = null, CancellationToken ct = default) =>
        Projected(IlmekRuntime.Stream(graph, input, options, ct), modes, ct);
}

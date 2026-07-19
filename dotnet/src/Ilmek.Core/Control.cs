namespace Ilmek;

/// <summary>
/// One dynamic fan-out branch (MODEL.md §14): run <see cref="Node"/> with
/// <see cref="Input"/> as its entire state, in parallel with the other sends of
/// this superstep.
///
/// <para><see cref="Input"/> must be serializable — it is written into the
/// checkpoint so a resumed run re-dispatches the same fan-out.</para>
/// </summary>
public sealed record Send(string Node, object? Input);

/// <summary>
/// A node's return that carries a routing decision alongside its update
/// (MODEL.md §15).
///
/// <para><see cref="Goto"/> replaces the node's static edges for this superstep;
/// leave it null to fall back to them. <see cref="StateUpdate"/> reduces exactly
/// like a bare return.</para>
/// </summary>
public sealed class Command
{
    public IReadOnlyDictionary<string, object?>? StateUpdate { get; }

    /// <summary>Node names, <see cref="Graph.End"/>, and/or <see cref="Send"/> values.</summary>
    public IReadOnlyList<object>? Goto { get; }

    private Command(IReadOnlyDictionary<string, object?>? update, IReadOnlyList<object>? gotoTargets)
    {
        StateUpdate = update;
        Goto = gotoTargets;
    }

    /// <summary>Update state and route: <c>Command.Create(update, "next")</c>.</summary>
    public static Command Create(IReadOnlyDictionary<string, object?>? update = null, params object[] gotoTargets) =>
        new(update, gotoTargets.Length == 0 ? null : gotoTargets);

    /// <summary>Route only, leaving state untouched.</summary>
    public static Command Goto_(params object[] gotoTargets) => new(null, gotoTargets);

    /// <summary>Update only — equivalent to returning the update directly.</summary>
    public static Command Update_(IReadOnlyDictionary<string, object?> update) => new(update, null);
}

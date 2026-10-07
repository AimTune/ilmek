using System.Text.RegularExpressions;

namespace Ilmek.A2A;

/// <summary>A task as a node wants it. Plain data — it goes in the journal. Mirror of TypeScript's <c>A2aResult</c>.</summary>
public sealed record A2aResult
{
    public required string TaskId { get; init; }
    /// <summary>The remote agent's conversation id; pass it back to continue.</summary>
    public required string ContextId { get; init; }
    public required string State { get; init; }
    /// <summary>The text of the task's artifacts (the reply), joined; <c>""</c> when there is none.</summary>
    public required string Text { get; init; }
    /// <summary>The text of the agent's status message — what it needs, or why it stopped.</summary>
    public required string StatusText { get; init; }
    /// <summary>The first data part of the status message (a mekik agent puts its open interrupts here as <c>pending</c>).</summary>
    public IReadOnlyDictionary<string, object?>? StatusData { get; init; }
    /// <summary><c>State == "input-required"</c>: the caller must send another message on <see cref="TaskId"/>.</summary>
    public bool NeedsInput => State == "input-required";
    /// <summary>The task, verbatim.</summary>
    public required IReadOnlyDictionary<string, object?> Task { get; init; }

    /// <summary>The TypeScript object shape, minus <c>task</c>.</summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var d = new Dictionary<string, object?>
        {
            ["taskId"] = TaskId, ["contextId"] = ContextId, ["state"] = State, ["text"] = Text, ["statusText"] = StatusText, ["needsInput"] = NeedsInput,
        };
        if (StatusData is not null) d["statusData"] = StatusData;
        return d;
    }
}

public sealed record SendOptions
{
    /// <summary>Continue the remote conversation this id names.</summary>
    public string? ContextId { get; init; }
    /// <summary>Answer an <c>input-required</c> task: the message goes to this task.</summary>
    public string? TaskId { get; init; }
    /// <summary>A data part sent alongside the text (e.g. <c>{ answers: {…} }</c> for a mekik agent with several pauses).</summary>
    public IReadOnlyDictionary<string, object?>? Data { get; init; }
    /// <summary>Journal key; defaults to <c>a2a:&lt;agent&gt;:send</c>, so one node can talk to several agents.</summary>
    public string? Key { get; init; }
    /// <summary>The message id; minted when absent.</summary>
    public string? MessageId { get; init; }
}

/// <summary>
/// A remote A2A agent. <see cref="ConnectAsync"/> fetches the Agent Card once;
/// <see cref="SendAsync"/> runs inside <c>ctx.StepAsync</c> — on the replay pass
/// after an interrupt the recorded task comes back and the remote agent is <b>not</b>
/// messaged again. <see cref="InvokeAsync"/> is the raw, unjournaled call. Mirror of
/// <c>@ilmek/a2a</c>' <c>A2aAgent</c>.
/// </summary>
public sealed class A2aAgent
{
    private static readonly Regex NotAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);

    public string Name { get; }
    public IReadOnlyDictionary<string, object?> Card { get; }
    private readonly IA2aTransport _transport;
    private readonly Func<string> _mint;
    private long _rpcSeq;

    private A2aAgent(IA2aTransport transport, string name, IReadOnlyDictionary<string, object?> card, Func<string> mint)
    {
        _transport = transport;
        Name = name;
        Card = card;
        _mint = mint;
    }

    /// <summary>Fetch the card and wrap the agent. <paramref name="name"/> is the journal-key namespace (default: the card's name, slugified).</summary>
    public static async Task<A2aAgent> ConnectAsync(IA2aTransport transport, string? name = null, Func<string>? mintId = null, CancellationToken ct = default)
    {
        var card = await transport.GetAgentCardAsync(ct).ConfigureAwait(false);
        name ??= Slug(card.GetValueOrDefault("name") as string);
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("an A2A agent needs a name", nameof(name));
        var n = 0;
        var mint = mintId ?? (() => $"msg-{name}-{Interlocked.Increment(ref n)}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}");
        return new A2aAgent(transport, name, card, mint);
    }

    /// <summary>Raw, <b>unjournaled</b> <c>message/send</c>. Prefer <see cref="SendAsync"/> inside a node.</summary>
    public async Task<A2aResult> InvokeAsync(string text, SendOptions? options = null, CancellationToken ct = default)
    {
        options ??= new SendOptions();
        var parts = new List<object?>();
        if (text.Length > 0) parts.Add(new Dictionary<string, object?> { ["kind"] = "text", ["text"] = text });
        if (options.Data is not null) parts.Add(new Dictionary<string, object?> { ["kind"] = "data", ["data"] = options.Data });
        if (parts.Count == 0) throw new ArgumentException("an A2A message needs text or data", nameof(text));
        var message = new Dictionary<string, object?>
        {
            ["kind"] = "message", ["messageId"] = options.MessageId ?? _mint(), ["role"] = "user", ["parts"] = parts,
        };
        if (options.TaskId is not null) message["taskId"] = options.TaskId;
        if (options.ContextId is not null) message["contextId"] = options.ContextId;
        var result = await RpcAsync("message/send", new Dictionary<string, object?> { ["message"] = message }, ct).ConfigureAwait(false);
        return Normalize(AsTask(result, Name));
    }

    /// <summary>
    /// Send a message <b>once</b> across any number of resumes: the round-trip runs in
    /// <c>ctx.StepAsync</c>, keyed <c>a2a:&lt;agent&gt;:send</c> unless <see cref="SendOptions.Key"/>
    /// says otherwise. A node that sends twice (ask, then answer the agent's pause) must give
    /// the second send its own key.
    /// </summary>
    public ValueTask<A2aResult> SendAsync(IContext ctx, string text, SendOptions? options = null) =>
        ctx.StepAsync(options?.Key ?? $"a2a:{Name}:send", async () => await InvokeAsync(text, options, ctx.CancellationToken).ConfigureAwait(false));

    /// <summary>Raw <c>tasks/get</c>.</summary>
    public async Task<A2aResult> GetTaskAsync(string taskId, int? historyLength = null, CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?> { ["id"] = taskId };
        if (historyLength is { } n) p["historyLength"] = (long)n;
        return Normalize(await RpcAsync("tasks/get", p, ct).ConfigureAwait(false));
    }

    /// <summary>Raw <c>tasks/cancel</c>.</summary>
    public async Task<A2aResult> CancelTaskAsync(string taskId, CancellationToken ct = default) =>
        Normalize(await RpcAsync("tasks/cancel", new Dictionary<string, object?> { ["id"] = taskId }, ct).ConfigureAwait(false));

    private async Task<IReadOnlyDictionary<string, object?>> RpcAsync(string method, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var request = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _rpcSeq), ["method"] = method, ["params"] = parameters };
        var response = await _transport.PostAsync(request, ct).ConfigureAwait(false);
        if (response?.GetValueOrDefault("error") is IReadOnlyDictionary<string, object?> error)
        {
            // A sloppy server may send a non-numeric (or out-of-range) code: keep the
            // error an A2aException rather than letting the conversion throw instead.
            var code = error.GetValueOrDefault("code") switch
            {
                long l and >= int.MinValue and <= int.MaxValue => (int)l,
                int i => i,
                _ => InternalError,
            };
            throw new A2aException(code, error.GetValueOrDefault("message") as string ?? "error", error.GetValueOrDefault("data"));
        }
        return response?.GetValueOrDefault("result") as IReadOnlyDictionary<string, object?>
            ?? throw new A2aException(InternalError, $"A2A {method}: malformed JSON-RPC response — no result object and no error");
    }

    /// <summary>JSON-RPC "internal error" — the code for a response this client cannot use.</summary>
    private const int InternalError = -32603;

    /// <summary><c>message/send</c> may return a bare Message for agents that skip tasks; wrap it as a completed task.</summary>
    private static IReadOnlyDictionary<string, object?> AsTask(IReadOnlyDictionary<string, object?> r, string agent)
    {
        if (r.GetValueOrDefault("kind") as string == "task" || (r.ContainsKey("status") && r.ContainsKey("id"))) return r;
        if (r.GetValueOrDefault("messageId") is not string messageId || r.GetValueOrDefault("parts") is not IEnumerable<object?>)
            throw new A2aException(InternalError, "A2A message/send: the result is neither a task nor a message");
        return new Dictionary<string, object?>
        {
            ["kind"] = "task",
            ["id"] = r.GetValueOrDefault("taskId") as string ?? $"message:{messageId}",
            ["contextId"] = r.GetValueOrDefault("contextId") as string ?? agent,
            ["status"] = new Dictionary<string, object?> { ["state"] = "completed" },
            ["artifacts"] = new List<object?> { new Dictionary<string, object?> { ["artifactId"] = messageId, ["parts"] = r.GetValueOrDefault("parts") } },
        };
    }

    /// <summary>Reduce a task to <see cref="A2aResult"/>. Pure — both languages pin it through conformance/a2a.</summary>
    public static A2aResult Normalize(IReadOnlyDictionary<string, object?> task)
    {
        var status = task.GetValueOrDefault("status") as IReadOnlyDictionary<string, object?>
            ?? throw new A2aException(InternalError, $"A2A task {task.GetValueOrDefault("id")} has no status");
        var statusMessage = status.GetValueOrDefault("message") as IReadOnlyDictionary<string, object?>;
        var artifacts = (task.GetValueOrDefault("artifacts") as IEnumerable<object?> ?? []).OfType<IReadOnlyDictionary<string, object?>>();
        return new A2aResult
        {
            TaskId = task.GetValueOrDefault("id") as string ?? "",
            ContextId = task.GetValueOrDefault("contextId") as string ?? "",
            State = status.GetValueOrDefault("state") as string ?? "unknown",
            Text = string.Join("\n", artifacts.Select(a => A2aParts.TextOf(a.GetValueOrDefault("parts"))).Where(t => t.Length > 0)),
            StatusText = A2aParts.TextOf(statusMessage?.GetValueOrDefault("parts")),
            StatusData = A2aParts.DataOf(statusMessage?.GetValueOrDefault("parts")),
            Task = task,
        };
    }

    private static string Slug(string? raw)
    {
        var slug = NotAlnum.Replace((raw ?? "").ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 64 ? slug[..64] : slug;
    }
}

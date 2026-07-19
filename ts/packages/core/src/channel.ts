import { ReducerError } from "./errors.ts";

/** The sentinel meaning `this channel has never been written`. */
export const UNSET: unique symbol = Symbol.for("ilmek.unset");
export type Unset = typeof UNSET;

export type Reducer<Value, Update> = (current: Value | Unset, incoming: Update) => Value;

export type ReducerKind = "last_write" | "append" | "merge" | "custom";

/**
 * One named slot of state plus the reducer that folds updates into it
 * (MODEL.md §2).
 *
 * `Value` is what the channel holds and what a node reads; `Update` is what a
 * node may write. They differ for `append` (holds `T[]`, accepts `T | T[]`) and
 * `merge` (holds `T`, accepts `Partial<T>`).
 */
export interface Channel<Value, Update = Value> {
    readonly kind: ReducerKind;
    readonly reducer: Reducer<Value, Update>;
    readonly default: Value;
}

export type ChannelMap = Record<string, Channel<any, any>>;

/** The state a node reads: every channel's `Value`. */
export type StateOf<C extends ChannelMap> = {
    [K in keyof C]: C[K] extends Channel<infer V, any> ? V : never;
};

/** The partial update a node returns: any channel's `Update`. */
export type UpdateOf<C extends ChannelMap> = {
    [K in keyof C]?: C[K] extends Channel<any, infer U> ? U : never;
};

function lastWriteImpl<T>(def: T): Channel<T, T> {
    return { kind: "last_write", default: def, reducer: (_current, incoming) => incoming };
}

/** `incoming` wins. The default reducer. */
export function lastWrite<T>(): Channel<T | undefined, T>;
export function lastWrite<T>(def: T): Channel<T, T>;
export function lastWrite<T>(def?: T): Channel<T | undefined, T> {
    return lastWriteImpl<T | undefined>(def) as Channel<T | undefined, T>;
}

/** List concat. Holds `T[]`, accepts one `T` or many. */
export function append<T>(): Channel<T[], T | T[]> {
    return {
        kind: "append",
        // Frozen: every read of an unwritten channel hands out this same
        // instance, so a node doing `state.messages.push(x)` instead of
        // returning an update would otherwise corrupt the channel's default for
        // the whole process. Freezing turns that into a loud throw.
        default: Object.freeze([]) as unknown as T[],
        reducer: (current, incoming) => {
            const base = current === UNSET ? [] : current;
            return base.concat(Array.isArray(incoming) ? incoming : [incoming]);
        },
    };
}

/** Shallow object merge, `incoming` wins per key. */
export function merge<T extends Record<string, unknown>>(): Channel<T, Partial<T>> {
    return {
        kind: "merge",
        default: Object.freeze({}) as T,
        reducer: (current, incoming) => ({ ...(current === UNSET ? {} : current), ...incoming }) as T,
    };
}

/** Any `(current, incoming) -> next`. `current` is `undefined` on the first write. */
export function reduce<Value, Update = Value>(
    fn: (current: Value | undefined, incoming: Update) => Value,
    def: Value,
): Channel<Value, Update> {
    return {
        kind: "custom",
        default: def,
        reducer: (current, incoming) => fn(current === UNSET ? undefined : current, incoming),
    };
}

/** The built-in reducers, as a namespace: `channel.append<string>()`. */
export const channel = { lastWrite, append, merge, reduce };

export function reduceChannel<V, U>(name: string, ch: Channel<V, U>, current: V | Unset, incoming: U): V {
    try {
        return ch.reducer(current, incoming);
    } catch (cause) {
        throw new ReducerError(
            `channel ${JSON.stringify(name)}: reducer ${ch.kind} cannot fold ` +
                `${JSON.stringify(incoming) ?? String(incoming)} into ${String(current === UNSET ? "(unset)" : current)}`,
            { cause },
        );
    }
}

/** Materialize raw channel values, substituting defaults for unwritten channels. */
export function materialize<C extends ChannelMap>(
    channels: C,
    values: Record<string, unknown>,
): StateOf<C> {
    const out: Record<string, unknown> = {};
    for (const [name, ch] of Object.entries(channels)) {
        const raw = values[name];
        out[name] = raw === undefined || raw === UNSET ? ch.default : raw;
    }
    return out as StateOf<C>;
}

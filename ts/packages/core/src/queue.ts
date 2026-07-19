/**
 * An unbounded async queue: producers `push` without awaiting, one consumer
 * `take`s in order.
 *
 * This is what lets a node's `ctx.emit()` reach the event stream *while* its
 * superstep is still running (MODEL.md §10). Without it the engine would have
 * to await every task before it could yield anything, and a token stream would
 * arrive in one lump at the end of the superstep.
 */
export class AsyncQueue<T> {
    private readonly items: T[] = [];
    private readonly waiters: Array<(value: T) => void> = [];

    push(item: T): void {
        const waiter = this.waiters.shift();
        if (waiter) waiter(item);
        else this.items.push(item);
    }

    take(): Promise<T> {
        // Test the length, not the shifted value: `undefined` is a legal T and
        // must not be mistaken for an empty queue.
        if (this.items.length > 0) return Promise.resolve(this.items.shift() as T);
        return new Promise<T>((resolve) => this.waiters.push(resolve));
    }
}

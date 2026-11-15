# C#

## Naming

`PascalCase` for types, public members, and private methods. `camelCase` for parameters and local variables. Private fields are `_camelCase` (leading underscore). File names are `PascalCase.cs`, matching the primary type they define.

## Instantiation

Prefer target-typed `new`: `Node node = new();` over `var node = new Node();`. The type is already stated on the left; repeating it on the right adds nothing.

## Memory & Concurrency

Default to immutable data and message-passing over shared mutable state — a `Channel<T>` or a producer/consumer queue removes the need for locking entirely in most producer/consumer or work-distribution scenarios. Reach for `lock`, `Interlocked`, or a `Concurrent*` collection only when the actual access pattern doesn't fit a queue or ownership-transfer model — a shared read-heavy cache or a simple counter, for instance, where forcing it through a queue would add latency and complexity for no benefit. The goal is to not reach for a mutex or concurrent collection reflexively; it's fine when the shape of the problem actually calls for it.

## Errors

Exceptions are the mechanism, used at the boundary. If a public method on this code is called and fails because the caller did something invalid (bad argument, missing initialization, precondition violated) — throw, and the caller's `try`/`catch` around that boundary call is where it gets handled and surfaced. Internal calls between functions within the same unit that aren't the public surface should let exceptions propagate uncaught rather than adding a `try`/`catch` at every internal layer. A broken internal invariant is not something to catch and paper over — let it throw and terminate the operation.

## Class Member Ordering

Follow the general static → constructor(s) → fields/properties → methods → interface implementations order from the base skill. Properties are grouped with fields, not methods, regardless of whether they're auto-properties or carry logic.

If a class implements `IDisposable`, it is always listed last in the inheritance declaration — no exceptions — so `Dispose` (and any `Dispose(bool)` overload) is always the last member in the file:

```
public sealed class Widget : IComparable<Widget>, IDisposable
{
    // static members

    // constructor(s)

    // fields and properties

    // methods

    // IComparable<Widget>
    public int CompareTo(Widget other) { ... }

    // IDisposable
    public void Dispose() { ... }
}
```

## Pointers

Only relevant in `unsafe` blocks, used sparingly and only where genuinely necessary: asterisk sticks to the type, `int* value;`.

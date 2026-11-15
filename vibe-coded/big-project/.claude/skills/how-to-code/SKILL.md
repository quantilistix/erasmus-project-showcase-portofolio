---
name: how-to-code
description: Coding, architecture, and interaction style for C, C++, C#, and Rust. Use whenever writing, editing, reviewing, or planning code in .c, .h, .cpp, .hpp, .cc, .cs, or .rs files. Covers architecture philosophy, formatting, comments, error handling, memory safety, and interaction rules. Load references/c.md, references/cpp.md, references/csharp.md, or references/rust.md for the language(s) actually in play.
---

# Claude Code Style Skill

These are defaults. Anything the user states explicitly for the current task overrides anything in this file, permanently for that task. If the user gives no direction on a point, fall back to what's written here.

## Architecture

Design for scalability and modularity that costs nothing to run. A module boundary, an interface, an abstraction layer — each one must earn its place through clearer ownership or easier change, not exist by default. Prefer flat, composable structures over deep inheritance chains or indirection stacks that add virtual dispatch, allocation, or lookup cost without a corresponding benefit.

Optimization happens at the level of algorithm and data-structure choice first: pick the approach with the right complexity for the actual data shape and access pattern before ever touching low-level tuning. Only drop into pointer arithmetic, manual buffer management, or hand-rolled structures where profiling or an obvious hot path justifies it — not as a default posture. The standard library of each language is a tool, not an enemy; see the per-language reference for where the line sits.

## Formatting

Allman brace style: `{` and `}` always get their own line for any multi-line block — function bodies, `if`/`else`, `for`, `while`, `switch`, `class`/`struct`/`impl`, everything. No dangling brace on the same line as a control statement or declaration.

Inline groupings — tuples/argument lists `(0, 1)`, array/collection literals `[2, 3]`, single-instruction inline blocks `{ return 0; }` — stay on one line whenever they fit. Space these properly: a space after commas, a space inside `{ }` around the single statement, no cramming.

The moment a grouping's contents don't fit on one line, it goes full Allman regardless of the delimiter — `(`, `[`, and `{` are treated the same. The opening delimiter gets its own line, one element per line, and the closing delimiter gets its own line. This applies uniformly to argument lists, parameter lists, array/collection literals, and brace blocks alike — there's no special-casing one delimiter type over another:

```
void Foo
(
    int a,
    int b,
    int c
)
{
    ...
}

int[] xs =
[
    2,
    3
];
```

Pointers: the asterisk sticks to the type, never the name — `int* a;`, not `int *a;`. This applies anywhere a language uses raw pointers (C, C++, and `unsafe` Rust/C# contexts).

C# specifically: prefer target-typed `A a = new();` over `var a = new A();` — the type is already declared on the left, repeating it on the right is noise.

When implementing an interface, trait, or abstract base, implement its members in the order they're declared in the interface/trait/base. Don't reorder for convenience — a reader diffing the implementation against the contract should be able to follow along top to bottom.

Within a class, struct, or `impl` block, order members top to bottom as: static members (fields and methods) first, right after the declaration; then constructor(s), all overloads grouped together; then instance fields and properties; then instance methods; then interface implementations last, one block per interface, in the same order the interfaces are listed in the type's inheritance declaration — the order they're listed is a deliberate choice, not incidental. See the per-language reference for language-specific conventions on interface placement (e.g., C#'s `IDisposable`).

## Comments

Comments exist for: documentation (what a public function/type does and why it exists), placeholders (marking unfinished work explicitly), headers (file/section banners), code separation (dividing a file into logical regions), and justifying a genuinely non-obvious choice — an unusual algorithm, an implementation that looks wrong at first glance but isn't, a workaround for a real constraint.

Comments do not exist to explain bad naming or bad structure. If something needs a comment because a variable name is unclear or a function does too much, fix the name or the structure — don't annotate around it. The code should read clearly on its own; comments add context that the code itself cannot express, not a translation of what the code already says.

## Error Handling

The governing question is: who needs to know about this error? Not whose fault it was.

- If a failure needs to be visible to whoever is calling this code — bad input, a resource that's unavailable, an operation that legitimately can fail for reasons outside anyone's control — surface it through the language's standard recoverable-error mechanism (see the per-language reference for the exact shape: `Result<T, E>`, an exception at the API boundary, a return code, etc.).
- If a failure represents a bug in the code being written right now — a broken invariant, a state that should be structurally impossible — it does not need a recovery path. Let it fail loudly and immediately (`panic!`, `assert`, an uncaught propagating exception, a debug abort) rather than wrapping it in a handler that pretends it's a normal outcome.

Never catch an error just to swallow it or catch it further from the boundary than necessary. A wrapper function calling into another function does not need its own try/catch unless it is the layer responsible for actually surfacing the result to an external caller.

## Memory Safety

Memory safety comes from how the code is structured, not from wrapping things in safety-net types. Model ownership clearly: know at every point who owns a piece of memory, who is responsible for releasing it, and make that explicit in the design rather than discovered by reading implementation details. Prefer message-passing and immutable data over shared mutable state as the default answer to concurrency — reach for a mutex, lock, or a concurrency-safe collection only when the access pattern genuinely doesn't fit a queue or ownership-transfer model, not as a first instinct. See the per-language reference for what's idiomatic vs. what to avoid defaulting to.

## Personality & Interaction

Give honest, direct feedback when asked for it — flaws in an approach, a design, or existing code are stated plainly, not softened or buried in caveats.

If the user asks a question, answer it — don't start writing or executing code in response to a question unless answering it requires an obvious action already implied by prior direction (e.g., "did you handle X?" where X was already planned but missed — go fix it). A genuine question about intent, tradeoffs, or what was done gets an answer, not code.

If the user asks for a subset of a plan — one step, one file, one function — do exactly that and stop. Don't continue on to the rest of the plan uninvited.

Be interactive to the degree the user asks for it — more back-and-forth and more checkpoints when they want that, fewer when they want to hand off a task and get a result.

## Loop & Context Discipline

Never enter or produce a genuinely unbounded loop — in generated code or in the agent's own working process. If a task's own scope seems open-ended, stop and check rather than iterating indefinitely trying to converge.

Keep context lean: don't re-read, re-print, or re-quote things already established in the conversation, and don't pad responses with restated context the user already has.

## Uncertainty

When a specification is genuinely ambiguous, ask before proceeding — don't guess and rewrite later, and don't ship code built on a shaky assumption. Silence on a point means the defaults in this file apply; a real ambiguity (something this file and the user's own words don't resolve) is worth a direct question before writing anything.

## Language References

Load the relevant file(s) for the language(s) in play:
- `references/c.md`
- `references/cpp.md`
- `references/csharp.md`
- `references/rust.md`

# Rust

## Naming

`snake_case` for functions, variables, parameters, modules, and fields. `PascalCase` for types, traits, and enum variants. `SCREAMING_SNAKE_CASE` for constants and statics. File names are `snake_case.rs`, matching the module they define — this is standard `rustfmt`/ecosystem convention, so it should already feel unremarkable.

## Ownership & Concurrency

Ownership and borrowing are the by-design memory-safety mechanism — this is Rust's whole premise, so there's no "unless necessary" qualifier here the way there is elsewhere. For concurrency specifically, channels and `Arc` are acceptable and idiomatic; they're the standard way to move data across thread boundaries safely and don't need special justification. `Mutex` and `RefCell` are fine when the access pattern genuinely calls for shared mutable state that ownership alone can't express — that's a normal part of Rust, not a fallback to avoid at all costs. `unsafe` is reserved for the specific cases where the borrow checker genuinely can't express what's needed (FFI, certain low-level performance work) — not a shortcut around fighting the borrow checker on a design that could be reworked instead.

## Errors

`Result<T, E>` for anything recoverable, propagated with `?` rather than manual matching at every call site. Reserve `panic!`/`assert!`/`debug_assert!` for states that should be structurally impossible — a broken invariant, not a normal failure mode a caller should plan around.

## Pointers

Raw pointers are rare outside `unsafe`/FFI contexts; when they appear, the asterisk sticks to the type: `*const i32`, `*mut Node` — consistent with Rust's own raw-pointer syntax, so no special casing needed here.

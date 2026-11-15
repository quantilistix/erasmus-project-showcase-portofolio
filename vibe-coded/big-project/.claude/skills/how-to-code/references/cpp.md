# C++

## Naming

Standard C/C++ convention: `snake_case` for functions, variables, parameters, free functions, and type names alike — consistent with the standard library it sits next to (`std::vector`, `std::unordered_map`). Private class members take a trailing underscore (`count_`, `buffer_`) to distinguish them from parameters and locals without changing case style. Constants and macros are `ALL_CAPS_WITH_UNDERSCORES`. File names are `snake_case.cpp` / `snake_case.hpp`.

## Standard Library

Use it. `std::vector`, `std::string`, `std::unordered_map`, `std::array`, and standard algorithms are the right default — they're well-optimized and hand-rolling them from scratch is wasted effort, not discipline. What to avoid is unnecessary bloat on top of that baseline: unneeded heap allocation, `<iostream>` in hot paths, heavy template metaprogramming or header-only libraries pulled in for something a plain loop would do, and abstraction layers (virtual dispatch, `std::function`, deep templates) added without a concrete need. Reach for raw arrays and pointers when there's a real, specific reason — a known-size buffer, a hot loop where container overhead is measured to matter — not as a default posture.

## Memory

RAII is the by-design mechanism: tie a resource's lifetime to a scope via constructors/destructors rather than manual `new`/`delete` pairs scattered through the code. `std::unique_ptr` and `std::shared_ptr` are appropriate here — they express ownership directly as part of the type, which is a design choice, not a safety net bolted on afterward. Avoid raw owning pointers with manual lifetime management outside of the specific low-level cases where it's justified.

## Errors

Avoid exceptions as the default error-handling mechanism — they carry runtime cost and control-flow complexity this style is trying to avoid. Surface recoverable errors through return codes, `std::optional`, or `std::expected` (C++23) instead. A state that should be structurally impossible is an `assert()`, not a caught exception.

## Pointers

Asterisk sticks to the type: `int* value;`, `Node* head;`.

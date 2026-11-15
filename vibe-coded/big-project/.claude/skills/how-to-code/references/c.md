# C

## Naming

Standard C convention throughout: `snake_case` for functions, variables, parameters, and struct fields. Types defined via `typedef` also use `snake_case`, typically with a `_t` suffix (`node_t`, `buffer_t`). Macros and constants (`#define`, `enum` constants, file-scope `const`) are `ALL_CAPS_WITH_UNDERSCORES`. File names are `snake_case.c` / `snake_case.h`, matching the module they implement.

## Memory

No language-level ownership tracking, so ownership must be explicit by convention: document who allocates and who is responsible for freeing at the point of declaration, not buried in a comment three functions away. Prefer stack allocation and fixed-size arrays wherever the bound is known at compile time; reach for `malloc`/`free` only when the size is genuinely dynamic. Pair every allocation with an obvious, symmetric release path — avoid ownership that transfers implicitly through a chain of calls without a clear handoff point.

## Errors

C has no exceptions, so this is simple: recoverable errors are surfaced via return codes or an output parameter (e.g., return an `int` status and write the result through a pointer parameter). A function that hits a state that should be structurally impossible (a broken invariant, corrupted internal state) should `assert()` and abort in debug builds rather than trying to recover from something that indicates a bug in the code itself.

## Pointers

Asterisk sticks to the type: `int* value;`, `node_t* head;`. This holds in declarations, parameters, and return types alike.

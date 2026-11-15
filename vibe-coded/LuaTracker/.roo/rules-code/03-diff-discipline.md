# Rule: apply_diff Discipline — Prevent Malformed Diffs and Cascading Errors

## Purpose

This rule exists because the `apply_diff` tool has a **strict, non-negotiable format** and prior sessions have repeatedly failed by violating it. Every failed diff wastes tokens, corrupts files, and creates cascading errors. Follow every section below without exception.

---

## 1. ALWAYS Read the File Before Editing

- **NEVER** assume you know a file's current content. Files change between turns.
- Before every `apply_diff` call, use `read_file` to get the **exact, current** content of the target file (or the relevant line range).
- Use the line numbers returned by `read_file` to set `:start_line:` accurately.
- If a file was "just edited" (the environment tells you), you **must** re-read it.

---

## 2. Diff Format — Exact Syntax Required

The **only** valid format is:

```
<<<<<<< SEARCH
:start_line:N
-------
[exact content to find — verbatim from the file, whitespace-perfect]
=======
[new content to replace with — clean, final code only]
>>>>>>> REPLACE
```

### Hard rules:

| Rule | Violation example | Why it fails |
|------|-------------------|--------------|
| **No `+` or `-` prefixes** on any line inside SEARCH or REPLACE blocks | `-import { useState } from "react";` | Literal `+`/`-` chars are inserted into the file or cause "identical" errors |
| **No `@@` hunk markers** | `@@` between blocks | Treated as content, corrupts the diff |
| **Separator is exactly `=======`** (7 equals signs) | `===` or `======` | Parser rejects it as malformed |
| **No marker strings inside content** | Code containing `=======` or `<<<<<<< SEARCH` or `>>>>>>> REPLACE` or `-------` as a line | Parser sees it as a format marker, not code, and the diff structure breaks |
| **SEARCH block must match the file exactly** | Guessed content that's 66% similar | Diff is rejected; read the file first |
| **SEARCH and REPLACE must be different** | Pasting the same text in both | Tool rejects with "no changes would be made" |
| **No reasoning, comments, or planning text** inside the diff `input` | `"We'll replace this with..."` | Corrupts the diff or the file |

### ⚠️ Reserved marker strings — NEVER appear as content

The diff parser uses these exact strings as structural delimiters. If any of them appear on their own line inside your SEARCH or REPLACE content, the parser will misinterpret the diff structure:

- `<<<<<<< SEARCH`
- `-------`
- `=======`
- `>>>>>>> REPLACE`

**If the code you are editing actually contains one of these strings** (e.g., a file with merge conflict markers, or a markdown file documenting the diff format itself), you **cannot** use `apply_diff` on that region. Use `write_to_file` instead to replace the entire file, or use `apply_diff` on a surrounding region that does not include the marker string on its own line.

---

## 2b. Every Diff Block MUST Have All Five Markers In Order

Every single SEARCH/REPLACE block requires exactly these five lines as structural markers, in exactly this order:

```
<<<<<<< SEARCH        ← opens the block
:start_line:N         ← line number
-------               ← ends the header, begins SEARCH content
=======               ← separates SEARCH content from REPLACE content
>>>>>>> REPLACE       ← closes the block
```

**If you skip any marker, the diff is malformed.** The most common mistake is omitting `=======` and jumping from the SEARCH content directly to `>>>>>>> REPLACE`. The parser then interprets `>>>>>>> REPLACE` as content and crashes.

**Self-check before submitting:** count the markers in your diff. Every block must have all five. If you have multiple blocks, each block needs its own complete set of five.

---

## 3. No Git Unified-Diff Syntax

You are **not** writing a `git diff`. You are filling in a structured SEARCH/REPLACE template.

- ❌ `+import { useEffect } from "react";` — **WRONG** (has `+` prefix)
- ❌ `-import { useState } from "react";` — **WRONG** (has `-` prefix)
- ✅ `import { useEffect } from "react";` — **CORRECT** (clean code only)

The SEARCH block contains the **old code exactly as it appears in the file**.
The REPLACE block contains the **new code exactly as it should appear in the file**.
Neither block should have `+`, `-`, or any diff annotation prefix.

---

## 4. Batch Related Changes Into One Diff

- When modifying imports **and** the code that uses them, include **all** related changes in a single `apply_diff` call using multiple SEARCH/REPLACE blocks.
- Do NOT change an import in one call and the usage in the next — this creates intermediate broken states with errors like `'X' is defined but never used` or `Cannot find name 'Y'`.

Example of multiple blocks in one call:

```
<<<<<<< SEARCH
:start_line:3
-------
import { useEffect, useState } from "react";
import { DataService, type Server } from "@/services/data.service";
=======
import { useEffect } from "react";
import { type Server } from "@/services/data.service";
import { useServers } from "@/hooks/use-data";
>>>>>>> REPLACE

<<<<<<< SEARCH
:start_line:44
-------
    const [servers, setServers] = useState<Server[]>([]);
    const { activeServerId } = useChatStore();

    useEffect(() => {
        DataService.getServers().then(setServers).catch(console.error);
    }, []);
=======
    const { data: servers = [], isLoading, error } = useServers();
    const { activeServerId } = useChatStore();
    if (error) console.error(error);
>>>>>>> REPLACE
```

---

## 5. Verify API Signatures Before Using Them

Before calling any library function (e.g., `useQuery`, `useMutation`, `fetch`), confirm which version of the library is installed by checking `package.json`. Use the correct call signature for that version:

- **@tanstack/react-query v5**: requires object syntax `useQuery({ queryKey: [...], queryFn: () => ... })`
- **@tanstack/react-query v4**: supports positional syntax `useQuery(key, fn)` but object syntax also works
- When in doubt, **always use the object syntax** — it works in both v4 and v5.

---

## 6. Clean Up Every Reference in the Same Diff

When you remove or rename a symbol (function, variable, import, type), you **must** also remove or update every reference to it in the same file within the **same diff call**. Specifically:

- If you remove `DataService` from imports → also remove every `DataService.xxx()` call in the file
- If you remove `useState` → also remove the `const [x, setX] = useState(...)` line
- If you remove a `useEffect` → also remove its dependency array and body
- If you add an import → use it somewhere, or don't add it

---

## 7. After Every Diff — Check the Problems List

After each `apply_diff` result, the tool reports `problems`. You **must**:

1. Read every reported problem.
2. Fix **all** of them in your next diff call (batch them).
3. Do NOT move on to the next file or task until the current file has zero new problems from your changes.

---

## 8. When a Diff Fails — Do NOT Retry Blindly

If a diff fails:

1. **Read the error message carefully** — it tells you exactly what went wrong.
2. **Re-read the file** with `read_file` to get current content.
3. **Construct a new diff** using the actual file content.
4. Do NOT re-submit the same diff. Do NOT guess at a fix without re-reading.

### Specific failure: "No sufficiently similar match found (X% similar, needs 100%)"

This means your SEARCH block does not match the actual file. The error output includes the **actual file content** under "Original Content:" or "Best Match Found:". When you see this:

1. **Do NOT guess what changed.** Use `read_file` on the target line range.
2. **Copy the exact lines** from `read_file` output into your SEARCH block — character for character, including comments, blank lines, and whitespace.
3. Common causes of this error:
   - A previous diff or manual edit changed the file since you last read it (e.g., `// moved above` replaced a real line of code)
   - You assumed the content from an earlier turn is still current — **it is not**
   - You omitted or added a line (e.g., missing a comment line, extra blank line)
4. **The 100% threshold is non-negotiable.** There is no "close enough." Every character must match.

---

## 9. Thinking and Planning Stay Out of Tool Calls

Your reasoning, planning, and internal monologue must **never** appear inside an `apply_diff` (or any tool) input. Tool inputs contain only the required parameters — nothing else.

---

## 10. Work in Small Steps — Avoid Model Overload

Large, complex operations in a single turn cause model failures ("inability to use a tool properly"). To prevent this:

- **One file per turn.** Do not try to edit multiple files in a single response. Finish one file, confirm it has zero problems, then move to the next.
- **Limit diff size.** Each `apply_diff` call should change no more than ~40 lines of SEARCH content and ~40 lines of REPLACE content. If the change is larger, split it into multiple sequential SEARCH/REPLACE blocks within the same call, or use `write_to_file` for the whole file.
- **Plan before acting.** Before writing any diff, state in plain text: (1) which file you will edit, (2) which lines, (3) what the change is. Then make the tool call. Do not combine planning and execution into one giant output.
- **If a task requires changes to 4+ files**, list all files and changes first as a plan, then execute them one file at a time across multiple turns.
- **Never combine reasoning with large diffs.** If you need to think through a problem, do that in a response with no tool calls. Then in the next step, apply the diff.

---

## 11. Every Response MUST Include a Tool Call — No Thinking Without Acting

Roo Code requires tool calls to make progress. If you respond with only text/reasoning and no tool call, the system will force a retry, which often crashes.

**Rules:**

- **Never respond with only analysis, planning, or explanation.** Every response must include at least one tool call.
- If you need to plan, **plan AND act in the same response**: write a brief plan (2-3 sentences), then immediately call `read_file` on the first file you intend to edit. This satisfies the tool call requirement while setting up the next step.
- If you are unsure what to do, call `read_file` on the most relevant file. Reading a file is always a safe, productive action.
- If you are stuck or confused, use `list_files` to orient yourself. Do not just explain your confusion in text.
- **Acceptable tool calls when you need to think:** `read_file`, `list_files`, `search_files`, `list_code_definitions`. Any of these count — pick whichever is most relevant.

**Why this matters:** When you output only text, Roo auto-retries with "please use a tool," which doubles the context size and frequently causes the model to crash on the retry attempt. One small tool call in every response prevents this entirely.

---

## Summary Checklist (Before Every `apply_diff`)

- [ ] I read the file (or relevant lines) with `read_file` this turn
- [ ] My SEARCH block is **verbatim** from the file — no `+`/`-` prefixes, no `@@`
- [ ] My REPLACE block is clean final code — no `+`/`-` prefixes, no `@@`
- [ ] SEARCH and REPLACE are **different**
- [ ] The separator is `=======` (7 equals)
- [ ] Each block has all five markers in order: `<<<<<<< SEARCH`, `:start_line:`, `-------`, `=======`, `>>>>>>> REPLACE`
- [ ] No reserved marker strings appear as content lines
- [ ] I batched all related changes (imports + usages) in one call
- [ ] I checked the library version for correct API signatures
- [ ] I removed all references to deleted symbols
- [ ] This diff changes only **one file** and is **under ~40 lines** per block
- [ ] This response includes at least one tool call (never text-only)
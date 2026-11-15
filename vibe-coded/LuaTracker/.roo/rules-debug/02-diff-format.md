# Diff Format Rules (CRITICAL)

## TOOL PERSISTENCE & DIFF FORMAT

**PROBLEM:** The model stops using tools or creates malformed diffs.

**ROOT CAUSE:** The diff tool has a STRICT format that must be followed exactly.

## CORRECT DIFF FORMAT (MANDATORY)

When using the `apply_diff` or similar diff tools, you MUST follow this EXACT format:

```
<<<<<<< SEARCH
:start_line: 42
-------
[EXACT content to find, INCLUDING all whitespace, indentation, and newlines]
=======
[New content to replace with]
>>>>>>> REPLACE
```

### CRITICAL RULES:

1. **`:start_line:` is REQUIRED** - Must be the first line after `<<<<<<< SEARCH`
2. **Separator order:**
   - `<<<<<<< SEARCH` (opens the search block)
   - `:start_line: N` (line number where search starts)
   - `-------` (separates metadata from search content)
   - `[search content]` (exact text to find)
   - `=======` (separates search from replace)
   - `[replace content]` (new text)
   - `>>>>>>> REPLACE` (closes the block)

3. **The `=======` marker:**
   - MUST come AFTER the search content
   - MUST come BEFORE the replace content
   - MUST come BEFORE `>>>>>>> REPLACE`

4. **The `>>>>>>> REPLACE` marker:**
   - MUST be the LAST line
   - MUST have ONLY replace content between `=======` and `>>>>>>>`

## COMMON MISTAKES (DO NOT DO THIS)

❌ **WRONG** - Missing `:start_line:`:
```
<<<<<<< SEARCH
-------
const foo = 'bar';
=======
const foo = 'baz';
>>>>>>> REPLACE
```

❌ **WRONG** - `>>>>>>> REPLACE` before `=======`:
```
<<<<<<< SEARCH
:start_line: 10
-------
const foo = 'bar';
>>>>>>> REPLACE  <-- TOO EARLY!
=======
const foo = 'baz';
```

❌ **WRONG** - Extra content after `>>>>>>> REPLACE`:
```
<<<<<<< SEARCH
:start_line: 10
-------
const foo = 'bar';
=======
const foo = 'baz';
>>>>>>> REPLACE
And more stuff here  <-- ILLEGAL!
```

✅ **CORRECT**:
```
<<<<<<< SEARCH
:start_line: 10
-------
const foo = 'bar';
=======
const foo = 'baz';
>>>>>>> REPLACE
```

## EXACT MATCHING (CRITICAL)

The SEARCH block must match the file content EXACTLY - character for character.

**CRITICAL RULES:**
- **NO git diff prefixes:** Don't add `-`, `+`, or line numbers
- **NO markdown code fences:** Don't wrap in ` ```typescript ` or similar
- **NO comments about the content:** Just the raw content
- **Exact whitespace** (spaces vs tabs)
- **Exact indentation** (count the spaces/tabs, don't guess)
- **Exact newlines** (blank lines matter)
- **Exact capitalization**

### What "EXACT" means:

When you use `read_file`, you get output like this:
```
 1 | import { Plus } from "lucide-react";
 2 | import { cn } from "@/lib/utils";
 3 | import { useEffect } from "react";
```

❌ **WRONG** - Includes line numbers and `|` separator:
```
<<<<<<< SEARCH
:start_line: 1
-------
 1 | import { Plus } from "lucide-react";
 2 | import { cn } from "@/lib/utils";
 3 | import { useEffect } from "react";
=======
```

❌ **WRONG** - Has git diff `-` prefixes:
```
<<<<<<< SEARCH
:start_line: 1
-------
-import { Plus } from "lucide-react";
-import { cn } from "@/lib/utils";
-import { useEffect } from "react";
=======
```

✅ **CORRECT** - Just the raw content (strip the line numbers):
```
<<<<<<< SEARCH
:start_line: 1
-------
import { Plus } from "lucide-react";
import { cn } from "@/lib/utils";
import { useEffect } from "react";
=======
```

### How to extract content from `read_file`:

When `read_file` shows:
```
15 | const name = 'John';
16 | const age = 30;
```

Your SEARCH block should contain:
```
const name = 'John';
const age = 30;
```

**Strip everything before the `|` separator.** Only include the actual code.

### DEBUGGING MISMATCHES

If you get "content not found" errors:

1. **Read the file first** using `read_file` tool
2. **Copy the EXACT text** from the file (including line numbers)
3. **Count spaces/tabs** - don't guess indentation
4. **Include trailing/leading newlines** if they exist in the original

## TOOL PERSISTENCE RULES

**MANDATORY WORKFLOW:** Before EVERY diff, you MUST:
1. **Read the file** using `read_file` tool
2. **Verify** what the current content actually is
3. **Only then** create the diff with exact current content in SEARCH block

**NEVER** create a diff from memory or assumptions about file content.

**NEVER** give up after one diff failure. Instead:

1. **Attempt 1:** Read file → Apply the diff
2. **If it fails:** Read the file AGAIN to see current state
3. **Attempt 2:** Correct the diff with exact content from the re-read
4. **If it fails again:** Read the error message carefully
5. **Attempt 3:** Try a different approach (maybe a larger diff block)
6. **Only after 3 attempts:** Report to user

**IMPORTANT:** Each attempt should use the `apply_diff` tool. Do not explain the error to the user before trying to fix it.

## ALTERNATIVE APPROACH (FALLBACK)

If diffs keep failing after 3 attempts, switch to `write_to_file` with the FULL file content:

```javascript
write_to_file({
  path: "src/components/navigation/navigation-sidebar.tsx",
  content: `[ENTIRE FILE CONTENT HERE]`
})
```

This is slower but guaranteed to work.

## MULTI-FILE EDITS

When editing multiple files in one task:
- Apply diffs to ALL files before stopping
- Do NOT ask "should I continue?" between files
- Only stop if you hit a blocker (missing dependency, unclear requirement)

## EXAMPLE WORKFLOW

**User:** "Add a new prop to NavigationSidebar"

**Your response:**
1. Read `navigation-sidebar.tsx`
2. Apply diff to add the prop to the interface
3. Apply diff to use the prop in the component
4. Apply diff to update the parent component that uses it
5. **Then** respond to user with summary

**DO NOT:**
1. Apply one diff
2. Ask "should I continue?"
3. Wait for user

## ERROR: "Search and replace content are identical"

This error means you're trying to replace content with itself (no actual change).

**CAUSE:** You copied the same content into both SEARCH and REPLACE blocks.

**FIX:**
1. **Read the file first** to see the CURRENT state
2. **Identify what actually needs to change**
3. **SEARCH block:** Current content (what's in the file NOW)
4. **REPLACE block:** New content (what it should become)

### Example of the error:

❌ **WRONG** - Both blocks are identical:
```
<<<<<<< SEARCH
:start_line: 10
-------
const name = 'John';
=======
const name = 'John';  <-- Same as search!
>>>>>>> REPLACE
```

✅ **CORRECT** - Replace block is different:
```
<<<<<<< SEARCH
:start_line: 10
-------
const name = 'John';
=======
const name = 'Jane';  <-- Actually changed
>>>>>>> REPLACE
```

### Common Scenario:

**User:** "Update the username to 'admin'"

**Wrong approach:**
1. You think the file says `const username = 'user';`
2. You create a diff to change it to `'admin'`
3. But the file ALREADY says `const username = 'admin';`
4. Error: Search and replace are identical

**Right approach:**
1. **Read the file FIRST** using `read_file`
2. See it already says `const username = 'admin';`
3. **Tell the user:** "The username is already set to 'admin'. No changes needed."
4. Don't create a diff at all

## ERROR: "98% similar, needs 100%"

This means the SEARCH block is ALMOST correct but has minor formatting differences.

**COMMON CAUSES:**

### 1. Git Diff Format Prefixes
❌ **WRONG** - Has `-` prefixes from git diff:
```
<<<<<<< SEARCH
:start_line: 3
-------
-import { useEffect, useState } from "react";
-import { type Server } from "@/services/data.service";
-import { useServers } from "@/hooks/use-data";
=======
import { useEffect, useState } from "react";
import { type Server } from "@/services/data.service";
>>>>>>> REPLACE
```

✅ **CORRECT** - No prefixes, just the raw content:
```
<<<<<<< SEARCH
:start_line: 3
-------
import { useEffect, useState } from "react";
import { type Server } from "@/services/data.service";
import { useServers } from "@/hooks/use-data";
=======
import { useEffect, useState } from "react";
import { type Server } from "@/services/data.service";
>>>>>>> REPLACE
```

### 2. Wrong Number of Spaces/Tabs
❌ **WRONG** - Has 2 spaces when file has 4:
```
<<<<<<< SEARCH
:start_line: 10
-------
  const name = 'John';  // 2 spaces
=======
    const name = 'Jane';  // 4 spaces
>>>>>>> REPLACE
```

✅ **CORRECT** - Match the file's indentation exactly:
```
<<<<<<< SEARCH
:start_line: 10
-------
    const name = 'John';  // 4 spaces (matches file)
=======
    const name = 'Jane';  // 4 spaces
>>>>>>> REPLACE
```

### 3. Missing/Extra Blank Lines
❌ **WRONG** - Missing blank line that exists in file:
```
<<<<<<< SEARCH
:start_line: 10
-------
const foo = 'bar';
const baz = 'qux';
=======
```

✅ **CORRECT** - Includes the blank line:
```
<<<<<<< SEARCH
:start_line: 10
-------
const foo = 'bar';

const baz = 'qux';
=======
```

### 4. Line Ending Differences (Windows CRLF vs Unix LF)
This is handled automatically by the tool, but if you're copying content manually, ensure you don't add extra `\r` characters.

## FIX WORKFLOW FOR 98% ERRORS:

1. **Read the file** using `read_file` tool
2. **Copy the EXACT text** from the output (character-for-character)
3. **Count indentation** - don't assume, COUNT the spaces/tabs
4. **Check for blank lines** - they matter!
5. **Do NOT add git diff prefixes** (no `-`, `+`, or line numbers in the SEARCH content)
6. **Paste exactly** into your SEARCH block

## SELF-CHECK BEFORE APPLYING DIFF

Before calling the diff tool, ask yourself:
- [ ] Did I **read the file** to verify current content?
- [ ] Are the SEARCH and REPLACE blocks **actually different**?
- [ ] Did I include `:start_line:`?
- [ ] Is the separator order correct? (`<<<<<<< SEARCH` → `:start_line:` → `-------` → search → `=======` → replace → `>>>>>>>`)
- [ ] Did I copy the search content EXACTLY from the file?
- [ ] Is there anything after `>>>>>>> REPLACE`? (there shouldn't be)
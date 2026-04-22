# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the codebase map, rendering paths, data-flow diagrams, and the ABI-boundary policy.

## Interaction Guidelines

**Answer questions before coding**: When asked a question, provide an actual answer first. Don't leap straight to writing code.

**Evaluate, don't assume**: "Why don't we X?" is a request for evaluation, not a suggestion to do X. Explain the tradeoffs, potential issues, or reasons why X might or might not be a good idea.

## Workflow

**Step 1 — Plan.** Enter plan mode (the actual `EnterPlanMode` tool — not a freeform text plan) and research the task and produce a plan. Skippable for trivial changes (under ~a dozen lines). Include unit tests in the plan whenever they're plausible to add — UI generally can't be tested, most other things can.

**Step 2 — Hostile-review the plan.** Before leaving plan mode, spawn a hostile-review agent against the plan itself. Brief it like a design reviewer: explain the problem being solved, point it at CLAUDE.md (and the rest of the tree — it can read whatever it needs to research), give it the plan, but do not justify the plan's choices. Give it enough feedback space to actually push back on the approach. Apply the same adjudication rules as the final review (below). Fold valid objections into the plan, then exit plan mode.

**Step 3 — Tests first (when applicable).** For bugfixes, or any feature whose tests can be sensibly written before the implementation exists, write the tests first and verify they fail. Then complete the implementation.

**Step 4 — Run all tests.** Always, even when the change seems unrelated. If anything breaks, return to step 3 — or step 1 if the fix requires significant redesign. For UI changes that can't be unit-tested, explicitly say so rather than claiming success.

Don't treat a failing test as a hard veto on the change. Tests exist to catch *unintentional* drift — a test that pins behavior the change deliberately replaced should be updated alongside the code, not worked around to preserve the old behavior. Fix the test to match the new intent; only fall back to step 3 / step 1 when the failure exposes an actual regression.

**Step 5 — Hostile review.** Spawn a hostile-review agent. Brief it like a PR reviewer: explain the problem being solved, point it at CLAUDE.md (and the rest of the tree — it can read whatever it needs to research), but do not explain or justify the implementation. Explicitly ask it to **review the general architecture** too, not just the diff — does the chosen approach fit the surrounding code, are there cleaner factorings, does it introduce abstractions that don't pay rent, etc. Give it enough feedback space to cover both the local change and the architectural read effectively (don't cap it to a terse response). Then:
  - If it raises valid objections, fix them. Significant redesign → back to step 1; code changes → back to step 3.
  - If I disagree with an objection, push back once. If it still objects and I'm still confident, surface the disagreement to the user for adjudication rather than looping.
  - Either way — adjudication needed or not — give the user a quick summary of the review at the end.

## Code Patterns and Conventions

### Naming Conventions
- **PascalCase**: Public members, types, static fields
- **camelCase**: Private/protected fields, parameters
- **Interface prefix**: `I` (e.g., `IComponent`)
- **Category-instance prefix**: When a name combines a category with an instance, put the category first so related names group alphabetically and the category reads as the classification. `SpawnerBurst`, `ShapeRadial`, `AttackStart()` — not `BurstSpawner`, `RadialShape`, `StartAttack()`. The category is the "kind of thing"; the instance is the specific variant.

### Critical Rules
1. **Always use absolute paths** in file operations

### Coding Guidelines

**KISS/YAGNI**: Keep it simple. Don't build abstractions or features that aren't immediately needed. Write the simplest code that solves the current problem.

**No backwards compatibility**: Remove stubs and dead code completely. Don't preserve backwards compatibility for its own sake—if something is unused or being replaced, delete it outright.

**Native code is for ABI interop only**: Native-language source (e.g. `native/*.c`) holds only what the protocol's C ABI forces — inline stubs that aren't directly P/Invoke-able, listener function-pointer structs, the per-handle state those listeners need to identify their target, one-shot handshake loops that dispatch those listeners (protocol completion, not policy), and lookups from ABI-exposed handles to stable opaque IDs. Everything else — algorithms, thresholds, accumulation, classification, formatting, logging, multi-call decision trees — lives in C# behind trampoline callbacks. Keep identifiers crossing the ABI as stable opaque values (e.g. wire-level names) rather than raw pointers, so consumer lifetime is decoupled from proxy lifetime.

**Avoid default parameters**: Prefer explicit overloads or requiring all parameters. Default parameters hide complexity and make call sites harder to understand.

**Always use braces**: Always include `{}` for `if`, `else`, `for`, `foreach`, `while`, etc., even for single-line bodies.
```csharp
// Good
if (condition)
{
    return;
}

// Bad
if (condition) return;
if (condition)
    return;
```

**Avoid expression-bodied members (`=>`)**: Prefer block bodies with explicit `return` statements for methods and properties. Expression bodies obscure control flow.
```csharp
// Good
public int GetValue()
{
    return value;
}

// Bad
public int GetValue() => value;
```

**Error handling**:
- Don't add excessive or preemptive error handling. Don't validate everything before it's ever been an issue.
- **Silent error handling is banned.** Never swallow exceptions or ignore error conditions. If something fails, it must be reported or thrown.

**Don't unnecessarily remove comments**: Existing comments are there for a reason. If a comment is out of date or actively misleading, remove (or fix) it. Otherwise leave it alone — don't strip comments just because they explain the "what" rather than the "why", or because you wouldn't have written them yourself.

**Don't hand-wrap lines**: Don't manually break comments or code onto multiple lines to fit a character limit. Good editors handle soft-wrapping. Let lines be as long as they naturally want to be; only break when it genuinely improves readability (e.g. a paragraph split, or a structurally-motivated break in a long expression).

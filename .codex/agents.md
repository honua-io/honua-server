# Agent Configuration

## Serena MCP Integration

### Memory Keys (Standardized Schema)

```
session/context          # Complete session state
session/last             # Previous session summary
session/checkpoint       # Progress snapshots

plan/[phase]/hypothesis  # Phase planning
plan/[phase]/tasks       # Task breakdown

execution/[feature]/log  # Implementation log
execution/[feature]/errors  # Error tracking

learning/patterns/[name] # Reusable success patterns
learning/solutions/[error] # Error solution database
```

### Session Start Protocol

1. `list_memories()` → Check for existing state
2. `read_memory("session/last")` → What was done previously
3. `read_memory("session/context")` → Restore context
4. Report to user: previous session, current progress, next actions

### Session End Protocol

1. `write_memory("session/last", summary)`
2. `write_memory("session/context", complete_state)`
3. Ensure next session can resume seamlessly

## Quality Checkpoints

Before any PR, verify:

- [ ] Tests written first (TDD)
- [ ] Integration tests use Testcontainers + PostGIS
- [ ] No legacy code copy-pasted
- [ ] Coverage meets phase checkpoint
- [ ] AOT build passes
- [ ] No new analyzer warnings

## Legacy Reference Protocol

When referencing legacy code at `../Honua.Server/`:

1. **Search** for relevant implementation
2. **Understand** the behavior and edge cases
3. **Document** the reference path
4. **Reimplement** from scratch following new architecture
5. **Test** against same scenarios as legacy

Never use Edit/Write to copy legacy code directly.

## PDCA Cycle

### Plan (Before Implementation)
- Create `docs/pdca/[feature]/plan.md`
- Define hypothesis and expected outcomes
- Identify risks and edge cases

### Do (During Implementation)
- Update `docs/pdca/[feature]/do.md` with progress
- Log errors and solutions as they occur
- Use `write_memory("execution/[feature]/log", ...)`

### Check (After Implementation)
- Create `docs/pdca/[feature]/check.md`
- Compare results vs expectations
- Document what worked and what didn't

### Act (Formalize Learnings)
- Success → `docs/patterns/[name].md`
- Failure → `docs/mistakes/[date].md`
- Update CLAUDE.md if globally applicable

## Error Investigation (Mandatory)

When errors occur:

1. **STOP** - Never retry without understanding why
2. **Investigate** - Use context7, WebSearch, read docs
3. **Document** - "Error was X because Y"
4. **Fix differently** - Not the same approach that failed
5. **Learn** - Add to learning/solutions memory

## Artifact Cleanup (MANDATORY Before Commit)

**Problem:** Dev artifacts accumulate and cause repo bloat, stale docs, "docs that lie."

**Before EVERY commit/PR:**

1. **Run cleanup scan:**
   ```bash
   find . -name "*.tmp" -o -name "scratch.*" -o -name "temp.*"
   find docs/temp -type f
   grep -rn "TODO" --include="*.cs" | grep -v "#[0-9]"
   ```

2. **Remove:**
   - Temporary files (*.tmp, scratch.*, temp.*)
   - Orphaned markdown files not linked from docs
   - Commented-out code blocks
   - Debug logging statements
   - `docs/temp/*` contents

3. **Fix or remove TODOs:**
   - Every `// TODO` must reference an issue: `// TODO(#123): description`
   - Or delete if no longer relevant

4. **Archive PDCA docs:**
   - Completed cycles → `docs/patterns/` or delete
   - Abandoned cycles → delete

See `.claude/cleanup-checklist.md` for full protocol.

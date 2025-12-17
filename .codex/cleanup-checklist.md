# Dev Artifact Cleanup Checklist

Run this checklist before every commit/PR to prevent repo bloat.

## Files to Remove

### Temporary Files
```bash
# Find and review temp files
find . -name "*.tmp" -o -name "scratch.*" -o -name "temp.*" -o -name "*.bak"
find . -name "*.orig" -o -name "*~"
```

### Orphaned Documentation
- [ ] Check `docs/` for files not linked from README or other docs
- [ ] Check `docs/pdca/` - archive completed cycles, delete abandoned ones
- [ ] Check `docs/temp/` - should be empty before merge
- [ ] Remove any `NOTES.md`, `TODO.md`, `SCRATCH.md` files

### IDE/Tool Artifacts
- [ ] No `.idea/` files (covered by .gitignore)
- [ ] No `.vscode/` user settings
- [ ] No `*.DotSettings.user`

## Code to Clean

### Commented Code
```bash
# Find large comment blocks (potential dead code)
grep -rn "^[[:space:]]*//" --include="*.cs" | head -50
```
- [ ] Remove commented-out code (use git history instead)
- [ ] Remove `#if false` blocks

### Debug Artifacts
- [ ] No `Console.WriteLine` debugging
- [ ] No `Debug.WriteLine` in production paths
- [ ] No `#if DEBUG` test code in production
- [ ] Log levels appropriate (not all Info/Debug)

### TODOs
```bash
# Find TODOs without issue references
grep -rn "TODO" --include="*.cs" | grep -v "#[0-9]"
```
- [ ] All TODOs reference a GitHub issue: `// TODO(#123): description`
- [ ] Or remove if no longer relevant

### Test Artifacts
- [ ] No skipped tests without issue reference
- [ ] No `[Fact(Skip = "...")]` without `// TODO(#xxx)`
- [ ] Test data files cleaned up

## Pre-Commit Verification

```bash
# Quick artifact scan
git status --porcelain | grep -E "\.(tmp|bak|orig)$"
git diff --cached --name-only | xargs grep -l "TODO" 2>/dev/null | head -10
find docs/temp -type f 2>/dev/null
```

## Session End Protocol

Before ending a coding session:

1. **Stage only intentional changes**: `git add -p`
2. **Review diff**: `git diff --cached`
3. **Run cleanup scan**: Use commands above
4. **Clear temp files**: `rm -rf docs/temp/*`
5. **Update session memory**: `write_memory("session/last", ...)`

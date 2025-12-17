## Summary

<!-- Brief description of what this PR does -->

Closes #<!-- issue number -->

## Changes

-

## Self-Review Checklist

### Code Quality
- [ ] Code follows vertical slice architecture
- [ ] No more than 5 dependencies per endpoint class
- [ ] No legacy code copy-pasted (reference only)
- [ ] No `// TODO` comments without linked issue

### Testing (TDD)
- [ ] Tests written before implementation
- [ ] Integration tests use Testcontainers + PostGIS
- [ ] Edge cases covered
- [ ] Coverage meets phase checkpoint

### Build
- [ ] `dotnet build` passes with warnings-as-errors
- [ ] `dotnet format --verify-no-changes` passes
- [ ] AOT build succeeds (if applicable)

### Documentation
- [ ] CLAUDE.md updated if new patterns established
- [ ] Legacy reference documented in code comments
- [ ] API changes documented

### Artifact Cleanup (REQUIRED)
- [ ] No temporary/scratch files (*.tmp, scratch.*, temp.*)
- [ ] No orphaned markdown files (unused docs, old notes)
- [ ] No commented-out code blocks
- [ ] No `// TODO` without linked issue number
- [ ] No debug logging left enabled
- [ ] No hardcoded test values in production code
- [ ] `docs/pdca/` files archived or deleted if cycle complete
- [ ] `docs/temp/` cleared

### Before Merge
- [ ] All CI checks passing
- [ ] Reviewed own code (diff tab)
- [ ] Commit messages follow conventional format
- [ ] Branch is up to date with trunk
- [ ] **Ran artifact cleanup checklist above**

## Test Plan

<!-- How was this tested? -->

## Legacy Reference

<!-- Files in ../Honua.Server/ used as behavior reference (if any) -->

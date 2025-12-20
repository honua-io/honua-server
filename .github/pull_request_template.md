# Pull Request

## Issue Link
<!-- Required: Link to the GitHub issue this PR addresses -->
Fixes #

<!-- Alternative formats: Closes #, Resolves #, Related to # -->

## Summary
<!-- Brief description of what this PR does -->


## Changes Made
<!-- List key changes -->
-
-
-

## Testing
<!-- Describe how this was tested -->
- [ ] Unit tests added/updated
- [ ] Integration tests added/updated
- [ ] Architecture tests pass
- [ ] Local pre-PR validation passed (`scripts/pre-pr-check.sh`)

## Coverage Impact
<!-- Required if this adds new code -->
- Line coverage: Before X% → After Y%
- Branch coverage: Before X% → After Y%

## Breaking Changes
<!-- List any breaking changes, or write "None" -->
None

## Additional Context
<!-- Any additional information -->


---

## Pre-PR Checklist (for contributor)
- [ ] Ran `scripts/pre-pr-check.sh` and all checks passed
- [ ] Commit message follows format: `type: description (#issue-number)`
- [ ] PR title matches main commit message
- [ ] Issue number linked above
- [ ] Tests added for new functionality
- [ ] Documentation updated if needed

## Reviewer Checklist
- [ ] Code follows project architecture (vertical slices, no controllers)
- [ ] Tests cover happy path and edge cases
- [ ] No reflection in hot paths (AOT compatible)
- [ ] Dependency limits respected (max 5 per endpoint)
- [ ] Error handling follows project patterns
- [ ] Security considerations addressed
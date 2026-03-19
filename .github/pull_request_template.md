# Pull Request

## Issue Link
<!-- Required: Link to the GitHub issue this PR addresses -->
Fixes #

## Summary
<!-- Required: Brief description of what this PR does and why -->


## Changes Made
<!-- Required: List key changes as bullet points -->
-
-
-

## Testing
<!-- Check all that apply -->
- [ ] Unit tests added/updated
- [ ] Integration tests added/updated
- [ ] Architecture tests pass
- [ ] Manual testing performed

## Gate Impact
<!-- Which CI tiers does this PR affect? Check all that apply. See docs/ci/gate-model.md. -->
- [ ] PR gates (build, test, governance)
- [ ] Nightly gates (conformance, performance, security)
- [ ] Release gates (packaging, publishing)
- [ ] Deploy gates (promotion, post-apply validation)
- [ ] None — no gate impact

## Docs or Contract Impact
<!-- Does this PR change any API contracts, protocols, or documentation? -->
- [ ] OpenAPI spec changed
- [ ] Protobuf/gRPC contract changed
- [ ] Control plane SDK surface changed
- [ ] Documentation updated
- [ ] None — no docs or contract impact

## Release/Deploy Impact
<!-- Does this PR require release or deployment coordination? -->
- [ ] Requires coordinated release across repos
- [ ] Requires database migration
- [ ] Requires infrastructure changes
- [ ] Requires environment variable or secret changes
- [ ] None — standard merge-and-release flow

## Breaking Changes
<!-- Required: List any breaking changes, or write "None" -->
None

---

## Pre-PR Checklist
- [ ] Ran `scripts/pre-pr-check.sh` and all checks passed
- [ ] Commit messages follow conventional format: `type: description (#issue)`
- [ ] PR title matches main commit message
- [ ] Issue number linked above
- [ ] Tests added for new functionality
- [ ] If protocol/auth behavior changed: updated compatibility contract
- [ ] If breaking admin/control-plane API changes: updated migration guide
- [ ] If breaking gRPC/proto wire changes: confirmed with explicit review

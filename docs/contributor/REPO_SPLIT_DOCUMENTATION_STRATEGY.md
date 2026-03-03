# Repo Split Documentation Strategy

This document defines how documentation works after splitting SDKs/site/helm into separate repositories.

## Decision

GitBook-facing product documentation remains centralized in the `honua-server` monorepo.

Why:

- GitBook currently publishes from this repository.
- Users need one canonical location for product and API docs.
- It avoids documentation drift across multiple repos.

## Documentation Ownership Model

### Monorepo (`honua-server`) owns

- Product/user docs published to GitBook.
- Cross-cutting architecture and protocol docs.
- Server operations/runbooks and deployment guidance.
- Integration docs for SDKs, MCP, and control-plane APIs.

### Split repos own

- Short repo `README` with purpose and quick local commands.
- Repo-local contributor docs (build/test/release for that repo only).
- Release notes/changelog for that specific artifact.

Split repos should link back to GitBook for product documentation.

## Linking Rules

- Do not reference monorepo-internal paths that are being removed (for example `sdk/mcp`).
- Prefer explicit repo references for code location:
  - `honua-sdk-js`
  - `honua-sdk-python`
  - `honua-sdk-dotnet`
  - `honua-site`
  - `honua-helm`
- Keep end-user guidance in monorepo/GitBook; keep implementation details in the owning repo.

## Next Steps Checklist

1. Update monorepo docs that still reference legacy local SDK paths (`sdk/*`) to point to split repos.
2. Keep GitBook navigation in this monorepo as the canonical user-facing docs index.
3. Add/standardize short `README` files in split repos with:
   - purpose
   - local dev/test commands
   - link back to GitBook docs
4. Decouple remaining monorepo build/runtime dependencies on legacy SDK folders:
   - replace `ProjectReference` to `sdk/dotnet` with package consumption or generated client strategy
   - update `proto/buf.gen.yaml` output away from `../sdk/js/...` if SDK codegen is no longer monorepo-owned
5. Only remove legacy folders after the above references are gone and CI is green.

## Removal Gate (Safe Delete Criteria)

You can safely remove a migrated folder from monorepo when all are true:

- No workflow references remain.
- No build/project references remain.
- No scripts/docs reference local paths under that folder.
- Owning split repo has the latest required content committed and pushed.

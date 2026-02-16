# CI Monitoring

Use this page to keep CI stable and predictable.

---

## What to Watch

- Build and test status on pull requests
- Coverage gates (line and branch)
- Formatting (`dotnet format`)
- Architecture tests and API surface coverage

---

## Common Failure Fixes

- Run `dotnet format Honua.sln` before PRs.
- Ensure new endpoints have integration tests.
- Check for dependency direction or public-type documentation issues.

---

## Related Docs

- [CI Quality Gates](../contributor/CI_QUALITY_GATES.md)
- [Testing Strategy](../contributor/adr/0011-testing-strategy.md)

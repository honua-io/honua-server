# Scripts

Scripts are grouped by purpose. Prefer adding new automation to an existing category instead of adding more files directly under `scripts/`.

## Layout

- `ci/` - PR checks, architecture review helpers, OpenAPI governance, and parity scorecard checks.
- `client-compat/` - desktop/client compatibility smoke and certification helpers.
- `cloud/` - deployment, rollback, and post-apply validation helpers for cloud targets.
- `conformance/` - production audit and standards conformance runners.
- `conformance/cite/` - OGC CITE suite runners.
- `conformance/ogc/` - non-CITE OGC conformance helpers.
- `demos/` - local demos and scenario runners.
- `dev/` - developer setup, git hook setup, Playwright setup, and Docker cleanup helpers.
- `host/` - host-machine maintenance helpers, mostly WSL/Windows.
- `migrations/` - one-off repository migration helpers.
- `scale/` - scale, transaction, load, and soak test runners.
- `sdk/` - SDK generation helpers.
- `security/` - security and secret-management helpers.
- `hooks/` - Git hook templates installed by `dev/setup-git-hooks.sh`.

Keep scripts runnable from the repository root unless the script explicitly documents otherwise.

# Validated examples inventory

`manifest.json` inventories every Markdown fence in the repository documentation,
every asset under `samples/`, every asset in a documentation `examples/` directory,
and the quickstart-adjacent demo/sample scripts. Each `passed` or `blocked` entry
has its own immutable candidate evidence, so retained verdicts remain attributed
to the image they actually exercised. `passed` is observed execution, `blocked`
requires an issue link, and `not-executable` identifies supporting material
without making a green claim.

Regenerate and verify the inventory with:

```bash
python3 scripts/examples/generate-manifest.py
python3 scripts/examples/generate-manifest.py --check
```

Run the executable customer paths and primary quickstart against an immutable
candidate (floating tags are rejected by the customer-path runner):

```bash
HONUA_EXAMPLES_CANDIDATE_IMAGE='ghcr.io/honua-io/honua-server@sha256:...' \
  bash scripts/examples/validate-customer-paths.sh all
HONUA_SERVER_IMAGE='ghcr.io/honua-io/honua-server@sha256:...' \
  bash scripts/docs-validation/validate-quickstart.sh
```

The nightly lane is advisory. Promotion to a required RC gate needs fourteen
consecutive scheduled successes with no unresolved example regression.

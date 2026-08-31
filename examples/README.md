# Validated examples inventory

`manifest.json` inventories every Markdown fence in the repository documentation,
every asset under `samples/`, every asset in a documentation `examples/` directory,
and the quickstart-adjacent demo/sample scripts. Its status is deliberately
evidence-based: `not-validated` is not a failure, but it is also not green.
Likewise, `scheduled-nightly` records CI coverage without claiming that an
unobserved execution passed.

Regenerate and verify the inventory with:

```bash
python3 scripts/examples/generate-manifest.py
python3 scripts/examples/generate-manifest.py --check
```

The three wave-one customer paths are STAC operations, mobile/offline sync, and
local geoprocessing. Each runs the shipped example itself against an isolated,
locally built candidate:

```bash
bash scripts/examples/validate-customer-paths.sh all
```

The nightly lane is advisory. Promotion to a required RC gate needs fourteen
consecutive scheduled successes with no unresolved example regression.

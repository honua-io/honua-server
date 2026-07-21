# Capability deployment profiles

Generate configuration from the same capability keys used by the evidence catalog and the
`?caps=` website view:

```bash
python scripts/deployment/generate-capability-profile.py \
  --caps serve.wfs,editing.featureserver-edits \
  --serving-units 4 \
  --format json
```

The generator accepts only keys in
`docs/gis/data/capability-keys.v1.json`, removes duplicates, sorts the result, and rejects
unknown or malformed input. `--serving-units` is the everyday production footprint. It is
kept separate from capability selection because pricing has two decisions:

- The highest selected capability tier determines `requiredEdition`.
- Serving units determine `Starter` (up to 3), `Team` (up to 10), `Scale` (up to 25), or
  `Private` (above 25).

Community remains no-charge. Published Pro and Enterprise annual suggestions are included for
the first three bands; paid deployments above 25 units require a quote.

## Output formats

Use `--format env` for a Compose `env_file`, `--format compose` for a Compose override, or
`--format helm` for a Helm values file. The Compose and Helm documents are emitted as JSON,
which both consumers accept and which is valid YAML 1.2. Use `--output PATH` to write the
selected format to a pipeline-owned location.

```bash
python scripts/deployment/generate-capability-profile.py \
  --caps serve.wfs,serve.wms \
  --format helm \
  --output honua-profile.values.json
helm upgrade --install honua oci://ghcr.io/honua-io/charts/honua \
  -f honua-profile.values.json
```

The Helm output uses the chart's existing `config.env` contract. Generated configuration is
non-secret and contains only the schema version and selected key list.

When both generated variables are present, the server treats the selected keys as a fail-closed
HTTP route allowlist backed by the committed feature catalog. Unselected routed surfaces return
404. Include `discovery.capability-manifest` to expose `/api/v1/capabilities/manifest`, which reports
the exact effective keys in `deploymentProfile.enabledCapabilities`. Include `ops.health` when an
orchestrator needs the standard health probes. With neither variable present, the middleware is
inert and the historical full-surface behavior is preserved.

## Security boundary

A deployment profile is a configuration restriction, not a license. It never emits a license
key, signed envelope, development edition grant, secret, or entitlement override. Selecting a
Pro or Enterprise capability only reports the required edition; the server must still receive
and validate an independently issued runtime license.

The `profileFingerprint` is SHA-256 over the sorted selected keys. Record it with deployment
evidence so review systems can compare the requested profile with the applied artifact without
including secrets.

The canonical JSON contract is versioned at
`docs/gis/schemas/deployment-profile.v1.schema.json`. Consumers must reject unsupported major
schema versions rather than guessing at new fields.

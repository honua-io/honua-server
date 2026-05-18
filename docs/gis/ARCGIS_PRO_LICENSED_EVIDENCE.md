# Licensed ArcGIS Pro Desktop Evidence

This is the scaffold for the `desktop-arcgis` evidence lane tracked by
[honua-server#1019](https://github.com/honua-io/honua-server/issues/1019).
It is separate from the `arcgis-stub` REST lane: the stub proves Honua serves
the ArcGIS REST request pattern, while this lane is reserved for licensed
ArcGIS Pro / ArcPy automation.

This slice does not make ordinary PR gates depend on ArcGIS Pro. It adds the
runner contract, the manual/scheduled workflow entry point, artifact guardrails,
an ArcPy script that emits standard `.cert.json` envelopes, a headless
layout/map-frame screenshot fallback for ProPy runs, and a strict artifact
validator for licensed runs.
A successful licensed run still needs to be executed and linked before #1019
can be closed.

## Workflow

Workflow file:
[`arcgis-pro-desktop-evidence.yml`](../../.github/workflows/arcgis-pro-desktop-evidence.yml)

Triggers:
- `workflow_dispatch` with `run_licensed_lane=true`
- weekly `schedule`, but only when repository variable
  `ARCGIS_PRO_EVIDENCE_ENABLED=true`

The licensed job runs only on a self-hosted Windows runner with labels:

```text
self-hosted, Windows, arcgis-pro
```

Required runner state:
- ArcGIS Pro installed and licensed.
- ArcGIS Pro Python available through `propy.bat` or the `arcgispro-py3`
  Python executable.
- A runner-local blank `.aprx` template path supplied through the workflow
  input `project_template_path` or repository variable
  `ARCGIS_PRO_PROJECT_TEMPLATE`.
- For reliable headless render evidence, the `.aprx` template should contain a
  layout with a map frame. Use workflow inputs or repository variables
  `ARCGIS_PRO_LAYOUT_NAME` and `ARCGIS_PRO_MAP_FRAME_NAME` when the template has
  multiple layouts or frames. If the script is run inside an open ArcGIS Pro UI,
  it can still export the active view first.
- Network access to a seeded Honua service.

Required target service:
- `HONUA_BASE_URL` / workflow input `honua_base_url` points to Honua.
- Default service and layers match `tests/seed/browser-compat.yaml`:
  `browser_compat` layers `2000` (point), `2001` (line), and `2002`
  (polygon).
- Equivalent fixtures are acceptable when they expose both
  `/FeatureServer` and `/MapServer` for the same service and include point,
  line, polygon, schema, query, pagination, and render/style coverage.

Optional secrets:
- `HONUA_ARCGIS_PRO_API_KEY` maps to `X-API-Key`.
- `HONUA_ARCGIS_PRO_AUTHORIZATION` maps to the `Authorization` header.

Do not put credentials in `honua_base_url`, service ids, layer ids, project
template names, or committed files.

## Runner Script

Script:
[`scripts/client-compat/arcgis-pro/run-arcgis-pro-evidence.py`](../../scripts/client-compat/arcgis-pro/run-arcgis-pro-evidence.py)

Live licensed run:

```powershell
& "C:\Program Files\ArcGIS\Pro\bin\Python\Scripts\propy.bat" `
  scripts/client-compat/arcgis-pro/run-arcgis-pro-evidence.py `
  --base-url "https://honua.example" `
  --service-id "browser_compat" `
  --layer-id "2000" `
  --line-layer-id "2001" `
  --polygon-layer-id "2002" `
  --project-template "D:\arcgis-fixtures\Blank-Honua-Evidence.aprx" `
  --layout-name "Honua Evidence" `
  --map-frame-name "Map Frame" `
  --output-dir "artifacts/arcgis-pro-desktop/manual-run"
```

Contract-only fixture mode, used by ordinary PR tests:

```bash
python scripts/client-compat/arcgis-pro/run-arcgis-pro-evidence.py \
  --write-fixture-template /tmp/arcgis-pro-observations.template.json

python scripts/client-compat/arcgis-pro/run-arcgis-pro-evidence.py \
  --fixture-observations /tmp/arcgis-pro-observations.template.json \
  --output-dir /tmp/arcgis-pro-evidence
```

## Artifact Contract

The workflow uploads:

```text
artifacts/arcgis-pro-desktop/<run-id>/
  certification/
    <run-id>-desktop-arcgis-featureserver.cert.json
    <run-id>-desktop-arcgis-mapserver.cert.json
  logs/
    arcgis-pro-evidence.log
  project/
    Honua-ArcGISPro-Evidence.aprx
  screenshots/
    arcgis-pro-map.png
  artifact-manifest.json
  summary.md
```

The two `.cert.json` files use:

```json
"client_lane": "desktop-arcgis"
```

They must not be renamed to `arcgis-stub`; that lane remains REST-only.

## Guardrails

- The workflow has no `pull_request` trigger.
- The scheduled self-hosted job is skipped unless
  `ARCGIS_PRO_EVIDENCE_ENABLED=true`.
- Uploaded evidence uses the shared `upload-ci-evidence` action with nightly
  retention, currently 30 days.
- The runner redacts known secret environment values, URL credentials,
  sensitive query parameters, and common auth header forms before writing JSON,
  logs, or summaries.
- The workflow scans text artifacts for unredacted Honua auth secrets before
  upload.
- The runner also validates the licensed artifact root with
  `--validate-output --require-live-artifacts`, requiring FeatureServer and
  MapServer envelopes, zero failed CERT results, render/project reload evidence
  refs that resolve inside the artifact root, and redacted text artifacts.
- `artifact-manifest.json` lists the cert envelopes, supporting evidence refs,
  and uploaded files so reviewers can verify screenshots, logs, and project
  copies without needing repository credentials.
- Use a clean blank `.aprx` template. Do not use a personal project, saved
  Esri sign-in state, license files, or project data containing customer
  credentials.
- Generated `.aprx` evidence is an artifact only; do not commit it to the repo.

## Acceptance Mapping

| #1019 criterion | Scaffold coverage |
|---|---|
| Licensed ArcGIS Pro lane against seeded Honua | Workflow and ArcPy runner are in place; successful run still pending runner/license availability. |
| Connects to FeatureServer and MapServer using ArcGIS Pro automation or ArcPy | Runner uses `arcpy.mp` `addDataFromPath` against both service types. |
| Validates discovery, schema, query/filter, geometry, pagination, rendering/style, reload | Runner records REST-backed discovery/schema/query/pagination/geometry checks and ArcPy-backed render/project reload checks. Active-view and headless layout/map-frame PNG export paths can substantiate screenshot-backed style checks. |
| Emits standard `.cert.json` envelopes with `client_lane: "desktop-arcgis"` | Implemented by the runner and validated by unit tests plus the workflow validator. |
| Captures screenshots/logs/project artifacts without credentials | Artifact layout, manifest generation, redaction scan, and strict evidence-ref validation are implemented; operators must use clean templates and review artifacts. |
| Migration evidence doc links successful run and distinguishes licensed evidence from stubs | The docs distinguish the lane, but no successful licensed run is linked yet. #1019 remains open until that exists. |

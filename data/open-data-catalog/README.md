# Open-data source catalog

Config-as-data registry of curated open geospatial sources that the area-import
provisioner can fetch, clip to an area, and load into Honua Server.

- `catalog.json` -- the curated sources + products. **Adding a source is a data
  edit here**, not a code change.
- `schema.json` -- JSON Schema (draft-07) that `catalog.json` must validate
  against.

Validate after editing:

```bash
python3 - <<'PY'
import json, jsonschema
jsonschema.validate(json.load(open('catalog.json')),
                    json.load(open('schema.json')))
print('ok')
PY
```

Seeded sources (slice 1):

| id | status | license | products |
|----|--------|---------|----------|
| `census-tiger` | available | US-PD | county-boundaries, places, roads, addresses* |
| `osm-geofabrik` | available | ODbL-1.0 | roads, buildings, poi |
| `overture` | placeholder | CDLA-Permissive-2.0 | places, buildings |
| `usgs-nhd` | placeholder | US-PD | waterbodies, flowlines |
| `fema-nfhl` | placeholder | US-PD | flood-hazard-areas |

\* `addresses` is a `geocoder` feedstock (future GP-Batch product).

See `docs/guides/open-data-provisioner.md` for the extension pattern and the
geocoding/routing roadmap.

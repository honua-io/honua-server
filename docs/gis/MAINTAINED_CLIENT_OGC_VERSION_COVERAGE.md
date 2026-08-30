# Maintained-client OGC version coverage

This is the release-gate crosswalk between supported OGC versions/profiles and
evidence produced by maintained real clients. CITE is a normative witness and
is intentionally not counted here. A row is covered only when the named client
operation executes and its case is emitted in a `.cert.json` envelope.

| Service version | Profile | Maintained-client witness | Executed operation |
|---|---|---|---|
| WFS 1.0.0 | `basic` | OWSLib `NB-OWS-WFS-100-01` | Capabilities, discovery, and `GetFeature` with 1.0 axis order |
| WFS 1.1.0 | `basic` | OWSLib `NB-OWS-WFS-110-01` | Capabilities, discovery, and `GetFeature` with 1.1 axis order |
| WFS 2.0.0 | `basic` | OWSLib WFS `CERT-*` cases | Deep capabilities, schema, filter, paging, CRS, and error lane |
| WFS 2.0.0 | `transactional` | None | Gap: CITE exercises this profile; OWSLib does not expose WFS-T |
| WMS 1.1.1 | `default` | OWSLib `NB-OWS-WMS-111-WITNESS-01`, `NB-OWS-WMS-111-01` | Capabilities and `GetMap`, including 1.1.1 axis order |
| WMS 1.3.0 | `default` | OWSLib WMS `CERT-*` cases | Deep capabilities, maps, feature info, CRS, and error lane |

## OGC API Features advertised profiles

| Advertised profile family | Maintained-client witness | Status |
|---|---|---|
| Features Part 1 core / GeoJSON / OAS 3 | OWSLib `NB-OWS-OAF-CONF-01` plus common-core cases | Covered |
| Features Part 2 CRS | OWSLib `NB-OWS-OAF-CONF-02`, `CERT-GEOM-02`, `NB-OWS-OAF-CRS-01`, `NB-OWS-OAF-CRS-02` | Covered |
| Features Part 3 Queryables | OWSLib `NB-OWS-OAF-CONF-02`, `CERT-SCHM-01`, `NB-OWS-OAF-QRYB-01` | Covered |
| CQL2 text | OWSLib `NB-OWS-OAF-CONF-02`, `CERT-QFLT-01`, `NB-OWS-OAF-QFLT-03`, `NB-OWS-OAF-DATE-01` | Covered |
| HTML; Common JSON/HTML; Features filter; CQL2 JSON/basic/spatial/advanced/case/accent/temporal/array; Features Part 4; Honua extensions | None | Gap: advertised, but not yet executed by a maintained real client |

Gap rows are deliberate. Advertisement presence and CITE results are not
real-client behavioral evidence and must never be relabeled as such.

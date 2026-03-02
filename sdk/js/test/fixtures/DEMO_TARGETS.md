# JS Migration Demo Targets (Issue #327)

This file pins the feature-table demo lane fixtures used by migration validation.

## Primary Target

- Fixture: `esri-demo-feature-table-relates-app`
- ArcGIS sample: `FeatureTable with related records`
- Source URL: `https://developers.arcgis.com/javascript/latest/sample-code/widgets-featuretable-relates/`
- Fixture pin: `featuretable-relates-v2026-03-02`
- Captured/adapted on: `2026-03-02`

## Fallback Target

- Fixture: `esri-demo-feature-table-popup-interaction-app`
- ArcGIS sample: `Feature table with popup interaction`
- Source URL: `https://developers.arcgis.com/javascript/latest/sample-code/widgets-featuretable-popup-interaction/`
- Fixture pin: `featuretable-popup-interaction-v2026-03-02`
- Captured/adapted on: `2026-03-02`

## Notes

- These fixtures are deterministic adaptations for CI reproducibility and codemod/runtime verification.
- They intentionally avoid external network dependency by stubbing query and related-record behavior in fixture code.

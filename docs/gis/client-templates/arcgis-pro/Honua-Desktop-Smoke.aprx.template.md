# ArcGIS Pro `.aprx` Template Instructions

This template produces `Honua-Desktop-Smoke.aprx`.

## Endpoint Targets

- Feature data endpoint: `${HONUA_BASE_URL}/rest/services/${HONUA_SERVICE_ID}/FeatureServer`
- Map rendering endpoint: `${HONUA_BASE_URL}/rest/services/${HONUA_SERVICE_ID}/MapServer`

## Build Steps

1. Create a new ArcGIS Pro project.
2. Add a FeatureServer connection using the feature data endpoint.
3. Add a MapServer connection using the map rendering endpoint.
4. Authenticate using API key/OIDC/Basic as required for the environment.
5. Add at least one feature layer and one map layer to the map view.
6. Save the project as `Honua-Desktop-Smoke.aprx`.

## Suggested Validation Layer Names

Use stable names in customer demos to keep screenshots and runbooks consistent:
- `Honua Features`
- `Honua Map`

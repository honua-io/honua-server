# ArcPy statistics evidence correction

The FeatureServer statistics cell is reopened from pass to blocked. The cited
ArcPy 3.7.1.1904 receipt counted three SearchCursor rows locally; it did not issue
or prove `outStatistics` or `groupByFieldsForStatistics`. This is a certification
evidence defect, not proof of a missing client or failing Honua endpoint.

The original receipt remains unchanged in `honua-esri-compat`:
`evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json`,
SHA-256 `73951d19013f5490ccba86c6c5730a192d14544a9d9cd4e25df9f375eb35bf98`.
The checklist preserves its old verdict and citation as `previous_pass`.
The Esri probe now skips unsupported remote-statistics claims. Its local-list
pagination probe had the same problem and is corrected independently; the
94-operation checklist does not have a separate pagination row.

After this correction the unchanged 376-cell denominator contains 166 passes,
4 failures, 136 blocked cells, 52 not started and 18 provisional exclusions.
ArcPy has 35 passes, 2 failures, 55 blocked and 2 provisional exclusions.
The 149 challenged exclusions and their 14 operation-specific resolutions are
unchanged. Historical reports and receipts retain their original context.

Closing the statistics cell requires a native remote aggregate operation,
evidence of the request, and independently validated results. Local calculation,
a REST control alone, or a pass from a different client lane cannot substitute.

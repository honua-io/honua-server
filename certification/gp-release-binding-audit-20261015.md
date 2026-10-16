# Process release registration evidence audit

Source reviewed: `d4ac2bd726` in PR #6243, compared with merged diagnostics
commit `559ca4d012879f838be16398ec5b215c7d2892a6`.

Within the source roots hashed by `GeoprocessingOperationEvidenceMatrixTests`,
only `GeoprocessingServiceCollectionExtensions.cs` changes. It adds three
`TryAddSingleton` registrations: the default release store, release admission
resolver, and environment resolver. Existing executor registrations, built-in
catalog definitions, destructive classifications, execution capability mappings,
and files under `Features/Geoprocessing/Execution/` are unchanged.

The matrix's 101 built-in operations do not include custom-code release
publication or activation. Their existing semantic evidence and verdicts remain
unchanged. Refreshing the catalog source digest records the registration change;
it does not certify the new deployment path or extend any operation's proof.

Direct custom-code admission has separate Python/.NET tests in
`CustomCodeSubmitTests`, covering matching-worker selection, persisted source and
provider identity, and replay after promotion. CI at `c86ef2ea95` passed all
4,015 server Fast tests and 736 GP unit tests. Provider-alias and optional-target
regressions added at `d4ac2bd726` still require fresh CI. PostgreSQL concurrency
and real HTTP authorization are checked by `GpProcessBindingEndpointsTests`.

Workflow and schedule integration, live worker/artifact availability, independent
dependency restoration verification, and cloud qualification remain open on
#6221. The built-in operation matrix does not establish readiness for those
release-management capabilities.

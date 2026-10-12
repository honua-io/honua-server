# Canonical routing job evidence audit

Source reviewed: `7b906e8d0c7e82e855936823306ccce30f669084` in PR #5940.

The catalog adds `routing.closest-facilities`, `routing.od-cost-matrix`, and `routing.location-allocation`. The canonical dispatcher registers `RoutingJobExecutor` once, and its process IDs match those three definitions. Existing executors and the 98 prior matrix verdicts are unchanged. The source-content digest includes the new executor and catalog definitions; it does not imply new proof for other processes.

The stock FeatureSet HTTP theory drives submit/status/named-results through both GPServer and NAServer and the real durable worker. It asserts original input identity including OBJECTID zero, cost, SR, output parameters and success. The provider is substituted: these three rows are **partially-proven**, not proof of the composed production routing algorithm or native ArcPy compatibility.

The existing `NAServerPgRoutingEndToEndTests` exercise real solver behavior for these three families, but use direct NAServer solves. They do not close the queued-job composition gap. Follow-ups #5949, #5950 and #5951 require that composition against their existing seeded graph and independent oracles. Issue #5192 retains the separate stock ArcPy binding/travel-mode/solve acceptance.

The intentional Geoprocessing-to-Routing dependency places tier 4 durable process orchestration above the tier 3 routing engine, analogous to Geoprocessing-to-Geometry and Ai-to-Routing. No Routing back edge, storage dependency, contract relocation, or second job runtime is introduced.

The final-head Operator receipt exposed a Development startup lifetime violation:
the singleton routing executor captured a scoped database session through its
provider. The correction resolves the provider and optional coordinate transform
inside an asynchronously disposed scope for each job, through the same canonical
executor. Provider capabilities, solves, retries, output mapping and publication
assertions are unchanged. A both-family Development regression uses a scoped
provider, submits a durable job, checks its four result artifacts and verifies
scope disposal. The source digest is refreshed for this ownership correction;
all 98 prior verdicts and the three partially-proven routing rows are retained.

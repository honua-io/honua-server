# Admin operation contract regression probes

These seven tests reproduce six defects from the admin-operations audit for
honua-io/honua-release#272. The tenant-ownership finding is separately verified
by a full endpoint/store/gateway source trace.

The project links the unchanged production executor, catalog, and tenant
middleware files and the same test class used by Honua.Server.Tests. It references
Honua.Hosting to avoid compiling unrelated protocol assemblies. The assembly name
retains Hosting's existing test friend access; it is not a production assembly.
No test makes an external HTTP request or modifies a database.

```sh
HONUA_MSBUILD_NODE_CAP=4 dotnet test tests/probes/AdminOperationsGaContracts/AdminOperationsGaContracts.csproj --filter FullyQualifiedName~AdminOperationsGaContractTests
```

When this worktree's unchanged dependencies have already been built, the same
probe can use `--no-restore -p:BuildProjectReferences=false` for incremental
verification. Do not use that option against stale production outputs.

Baseline f16b248dd7: seven tests ran, seven failed, zero skipped (.NET 10.0.11).
These are failing-before regression tests; this read-mostly hunt does not
implement remediation or claim a passing-after result.

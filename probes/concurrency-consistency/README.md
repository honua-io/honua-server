# Concurrency regression evidence

Baseline: f16b248dd7. This small test project links the actual production response-cache source and its interfaces; it avoids building the whole server test dependency closure. The backing-store fake preserves old response entries just as namespace invalidation does in Redis. The two cache instances have independent local version dictionaries, representing two server replicas. This is a deterministic component reproduction, not a live multi-node/Redis qualification.

Run from the repository root:

```sh
HONUA_MSBUILD_NODE_CAP=4 dotnet test probes/concurrency-consistency/Concurrency.Probes.csproj --filter FullyQualifiedName~RemoteInvalidation
```

Baseline result: 1 failed, expected null, actual `before-edit` at ResponseCacheConcurrencyTests.cs:22. Temporarily changing only `VersionLocalCacheTtl` in `src/Honua.Hosting/Features/Caching/CacheServiceResponseCache.cs` from `TimeSpan.FromSeconds(30)` to `TimeSpan.Zero` gives 1 passed. Production source was restored immediately after this diagnostic change. A production fix needs an explicit freshness policy and cross-node invalidation design; the temporary change only proves the cause.

The server test project also contains two regression additions at commit f06f02b846 for attachment replacement versus keywords update and reclaiming a healthy running job. The focused server build hit its 290-second limit before test execution; those two tests are **not runtime-verified**. Their findings were verified by full source-path/interleaving analysis.

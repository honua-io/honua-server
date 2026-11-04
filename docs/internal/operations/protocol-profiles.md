# Protocol profiles

Honua.Server compiles one of four protocol profiles. The default is `all`. A restricted profile omits serving projects and their endpoint mapping at build time. NativeAOT stays enabled (`PublishAot` remains true). The provider presets `full`, `no-cloud`, and `slim` are a separate axis and keep their existing meanings.

The authoritative membership list is [eng/protocol-profiles.v1.json](../../../eng/protocol-profiles.v1.json). MSBuild implements it in [eng/Honua.ProtocolProfiles.props](../../../eng/Honua.ProtocolProfiles.props).

## Profiles

`gsr` compiles the GeoServices family: catalog, FeatureServer, MapServer, ImageServer, VectorTileServer, GPServer, NAServer, GeometryServer, GeocodeServer, sharing, version management, and the Printing Tools GPServer. It does not compile SceneServer or I3S.

`ogc-api` compiles OGC API Features, tiles, maps, coverages, EDR, records, processes, and styles.

`ogc-api-classic` compiles that OGC API set plus WFS, WMS, WMTS, WCS, and WPS.

`all` compiles every serving protocol, including OData, STAC, SensorThings, Scene/I3S, gRPC endpoint mapping, and MCP endpoint mapping.

OData, STAC, SensorThings, MCP, gRPC, and Scene/I3S are `all` only.

## What stays on every profile

These stay because they are not exclusively the excluded serving surface:

- `Honua.Protocols.Ogc.Shared` — admin, styling, streaming, and capability contracts. Not the OGC API host.
- `Honua.Geoprocessing` — processing. GPServer mapping is gsr-only. OGC processes mapping is ogc-api-only.
- `Honua.Ai` — NL query, studio AI, and analysis. MCP routes are compile-gated inside this assembly.
- `Honua.Routing` — admin network topology plus AI and geoprocessing. NAServer mapping is gsr-only.
- `Honua.GeoServicesRest` and the geocoding engine — import and admin geocoding. The GeocodeServer adapter is gsr-only.
- Import endpoints for GeoServices, OGC API Features, WFS, and coverages.
- Health, readiness, authn/z, admin, catalog storage, jobs, and the processing engine required by the selected protocols.
- Elevation HTTP. Scene dataset, generation, BIM ingest, and point-cloud ingest endpoints compile only with the scene module.
- gRPC NuGet packages. `HonuaFeatureService` is omitted when GeoServices is excluded, because it binds that assembly's spatial-reference resolver. `HonuaProcessService` stays in Honua.Geoprocessing. Only `MapGrpcService`, gRPC-Web, reflection, and serving-status capture are compile-gated.

Geometry, objectId, and time filter translation for spatial analytics and GP layer selection binds the GeoServices query grammar, so those endpoints are part of the gsr closure. OGC profiles still run geoprocessing. Data enrichment where-filters use the shared filter service on the other profiles. GeoServices REST catalog discovery returns no entries when GeoServices is excluded.

## Build and publish

Pass the same profile to restore, build, and publish. Unknown names fail. An explicit module override such as `-p:HonuaIncludeOData=true` on `gsr` fails. Provider overrides such as `-p:HonuaIncludeAws=true` on `no-cloud` still work.

    dotnet build src/Honua.Server/Honua.Server.csproj -p:HonuaProtocolProfile=gsr
    dotnet build src/Honua.Server/Honua.Server.csproj -p:HonuaProtocolProfile=ogc-api
    dotnet build src/Honua.Server/Honua.Server.csproj -p:HonuaProtocolProfile=ogc-api-classic

`all` keeps the ordinary `bin/` and `obj/` paths. The other profiles write `src/Honua.Server/bin/protocol-<name>/` and `obj/protocol-<name>/`, so concurrent profiles do not share intermediates. Identical inputs still reuse their own tree.

Docker accepts `HONUA_PROTOCOL_PROFILE` (default `all`) on the root Dockerfile and on docker/Dockerfile.aot. Restore and publish receive the same `-p:HonuaProtocolProfile`. A publish writes `honua-protocol-profile.json`. docker/Dockerfile.protocol-profile-runtime copies that directory and checks the receipt. It does not compile. It is a packaging shim, not the production read-only image.

The receipt identity is `honua.protocol-profile/v1|<profile>|<modules>|build=<HonuaBuildProfile>`. A consumer rejects a receipt whose profile or module list does not match. Do not reuse a publish output across profiles.

Rollback is to rebuild and redeploy `all`. The default image and tag behavior is unchanged. This does not publish a new public tag fleet.

## Runtime

`HONUA_PROTOCOL_PROFILE` must match the compiled profile when it is set. Unset means the compiled profile. `Protocols:Enabled` must be omitted or name every module this build compiled. A shorter list is rejected until registration can honor a narrowed allowlist. An explicit experimental opt-in for a module this build excluded also fails at startup. The failure names the compiled profile, the requested value, and the rebuild switch. Defaults, including MCP admin projection, still allow a restricted build to start. Runtime settings cannot turn excluded code back on.

## Local CI

The selector is dry-run only. It does not build or start a container. PR Gate and the full CI build write its JSON, compile the solution for the default `all` profile, then compile each other selected profile. Those extra builds use the isolated `bin/protocol-<name>/` and `obj/protocol-<name>/` trees.

    python3 scripts/ci/protocol-profile-selection.py --dry-run
    python3 scripts/ci/protocol-profile-selection.py --dry-run --changed-from-git
    python3 scripts/ci/build-selected-protocol-profiles.py --selection protocol-profiles.json

A shared, workflow, or unmapped change selects every profile and `run_all` shards. It does not multiply each shard by four. A protocol project change selects the profiles that include that module and the shards whose names match it. Docs-only markdown stays on the default profile. Missing git refs fail closed to full validation.

Retention is ownership-scoped and dry-run by default. It only considers `bin/protocol-*` and `obj/protocol-*` older than seven days under the root you pass. It does not delete `bin/Debug`, `bin/Release`, or another job's tree. A `.protocol-profile-active` marker refuses the run. Trees larger than about 8 GiB produce a warning.

    python3 scripts/ci/prune-protocol-profile-outputs.py --root .
    python3 scripts/ci/prune-protocol-profile-outputs.py --root . --apply

There is no separate scheduler. Admission stays on the existing build slots.

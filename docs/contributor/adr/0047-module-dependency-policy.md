# ADR-0047: Module Dependency Policy

## Status

Accepted

## Context

ADRs 0041 (`Honua.Core.Abstractions` extraction), 0044
(`Honua.Server.Features.Infrastructure` decomposition into `Honua.Hosting`),
and 0046 (`IDatabaseSession` progressive migration) each describe *work*: the
sequenced refactors that produced today's project graph. New contributors,
however, do not need the history — they need an answer to two questions:

1. **Where does my new code go?**
2. **What may it reference?**

This ADR is the policy that answers both. It defines the module layers, the
dependency-direction matrix that governs which assembly may
`ProjectReference` which, and the decision tree that routes a new piece of
work to the correct layer. It is the canonical reference for module
boundaries; the predecessor ADRs remain authoritative for the rationale of
each individual carve. When the matrix here disagrees with one of those ADRs,
this ADR wins — but the disagreement should be reconciled in the same PR.

The ADR is enforced mechanically by
`tests/dotnet/Honua.Architecture.Tests/ModuleDependencyPolicyTests.cs`. The
matrix below and the test must be edited together.

## Decision

### Module layers

The repository has six conceptual layers. Each is a *role*, not necessarily a
single assembly — some layers (Storage Providers, Protocol Modules) have
several assemblies that share a role.

| # | Layer | Assemblies | Role | Allowed package neighbourhoods |
|---|-------|-----------|------|--------------------------------|
| 1 | **Abstractions** | `Honua.Core.Abstractions` | Pure contracts — interfaces, DTOs, records, option types, attribute markers. No implementation logic beyond trivial defaults. | `Microsoft.Extensions.*` only. **Banned:** `NetTopologySuite`, `Npgsql`, `AWSSDK.*`, `Azure.*`, `Parquet.*`, `FlatGeobuf`. |
| 2 | **Core** | `Honua.Core` | Cross-cutting domain logic — filter AST, metadata graph, security helpers, validation primitives. Provider-agnostic; protocol-neutral. May reference Abstractions. **Goal state:** heavy package refs migrate out (see Notes). | `Microsoft.Extensions.*`, `Polly`. Heavy refs (`NetTopologySuite`, `AWSSDK.*`, `Parquet.*`, `FlatGeobuf`) are present today but are scheduled to move to Geometry/Geocoding/Aws — new code must not add to them. |
| 3 | **Geospatial / Cloud satellites** | `Honua.Geometry` (NTS), `Honua.Geocoding` (AWS Location), `Honua.Aws` (S3 / etc.), `Honua.Azure` (Blob / etc.) | Heavy-package-coupled domain stacks. Each is dedicated to one external SDK family so the rest of the graph stays light. May reference Abstractions and Core. **Today these assemblies do not yet exist**; the policy reserves their slots so the migration target is unambiguous. | The relevant SDK family + Abstractions/Core. No other satellite. |
| 4 | **Hosting** | `Honua.Hosting` | ASP.NET-coupled host plumbing: authentication, caching, events, helpers, models, validation, the protocol-module plug-in surface. The only project (besides Server) that references `Microsoft.AspNetCore.*`. | `Microsoft.AspNetCore.*` + Abstractions / Core / ServiceDefaults. |
| 5 | **Storage Providers** | `Honua.Postgres` (+ planned sub-assemblies `Honua.Postgres.{Migrations, Catalog, FeatureStore, Streaming, Outbox}`), `Honua.DuckDB`, `Honua.MySql`, `Honua.SqlServer` | One assembly per backend. Implements Core abstractions over a specific provider SDK. Never references Hosting, Server, or another storage provider. | Provider SDK (`Npgsql`, `DuckDB.NET`, `MySqlConnector`, `Microsoft.Data.SqlClient`) + Abstractions/Core. |
| 6 | **Protocol Modules** | `Honua.Protocols.OData`, and the planned siblings `Honua.Protocols.{OgcApi, OgcClassic, GeoServices, Mcp, Scene, Stac}` | One assembly per HTTP / RPC surface. Depend on Abstractions + Core + Hosting; never on Server or each other. | Format-specific packages (`Microsoft.OData.*` for OData, etc.) + Abstractions/Core/Hosting. |
| 7 | **Composition root** | `Honua.Server` | Wires every storage provider + every protocol module into an executable host. The only assembly allowed to reference everything below it. Feature code only lives here when it cannot fit a lower tier. | Anything the runtime needs. |

`Honua.ServiceDefaults` is a sideways utility shared by Hosting and Server
(it carries .NET Aspire defaults). `Honua.AppHost` is an Aspire orchestration
shell that references only `Honua.ServiceDefaults`. `Honua.Worker.Gdal` is a
separate process whose runtime references Server. These three sit outside
the main stack and are matrix entries in their own right but do not
participate in the tier topology.

### Dependency direction matrix

Rows are consumers; columns are providers. A `✓` means the row's
`.csproj` is allowed to `ProjectReference` the column's `.csproj`. Empty
cells are forbidden and are caught by `ModuleDependencyPolicyTests`.

| Consumer ↓ \ Provider → | Abstr | Core | Geo | Gcod | Aws | Azure | Hosting | Postgres | DuckDB | MySql | SqlServer | Protocols.X | Server | SvcDef |
|--------------------------|:-----:|:----:|:---:|:----:|:---:|:-----:|:-------:|:--------:|:------:|:-----:|:---------:|:-----------:|:------:|:------:|
| **Abstractions**         |       |      |     |      |     |       |         |          |        |       |           |             |        |        |
| **Core**                 |   ✓   |      |     |      |     |       |         |          |        |       |           |             |        |        |
| **Geometry**             |   ✓   |  ✓   |     |      |     |       |         |          |        |       |           |             |        |        |
| **Geocoding**            |   ✓   |  ✓   |     |      |     |       |         |          |        |       |           |             |        |        |
| **Aws**                  |   ✓   |  ✓   |     |      |     |       |         |          |        |       |           |             |        |        |
| **Azure**                |   ✓   |  ✓   |     |      |     |       |         |          |        |       |           |             |        |        |
| **Hosting**              |   ✓   |  ✓   |     |      |     |       |         |          |        |       |           |             |        |   ✓    |
| **Postgres**             |   ✓   |  ✓   |  ✓  |      |  ✓  |       |         |          |        |       |           |             |        |        |
| **DuckDB**               |   ✓   |  ✓   |  ✓  |      |     |       |         |          |        |       |           |             |        |        |
| **MySql**                |   ✓   |  ✓   |  ✓  |      |     |       |         |          |        |       |           |             |        |        |
| **SqlServer**            |   ✓   |  ✓   |  ✓  |      |     |       |         |          |        |       |           |             |        |        |
| **Protocols.\<X\>**      |   ✓   |  ✓   |  ✓  |      |     |       |    ✓    |          |        |       |           |             |        |        |
| **Server**               |   ✓   |  ✓   |  ✓  |  ✓   |  ✓  |   ✓   |    ✓    |    ✓     |   ✓    |   ✓   |     ✓     |      ✓      |        |   ✓    |
| **ServiceDefaults**      |   ✓   |  ✓   |     |      |     |       |         |          |        |       |           |             |        |        |

Reading the matrix:

- **No back edges.** Abstractions row is empty: the contract surface depends
  on nothing in the repo.
- **Storage providers are siblings.** Postgres / DuckDB / MySql / SqlServer
  never reference each other. The matrix has no row crossing them.
- **Protocol modules are siblings.** They never reference each other; cross-
  protocol code goes into a neutral Core / Hosting / Geometry service that
  both adapt to. (See `CrossProtocolIsolationTests` for the runtime-source
  ratchet on this.)
- **Server is the only row with a ✓ in every column below it.** Composition
  is its job.
- **Geometry is the seam under storage and protocols.** Storage providers
  need NTS for spatial filter translation, and protocol modules need NTS for
  geometry serialization; both reference Geometry directly rather than
  pulling it transitively through Core.
- **The two "future" columns (Geocoding / Azure) only have ✓s from Server
  today.** Until a satellite is created, the matrix permission is dormant; the
  arch test ignores it because there is no `.csproj` for it yet.

#### Out-of-stack assemblies

| Consumer ↓ \ Provider → | Abstr | Core | Hosting | Server | SvcDef |
|--------------------------|:-----:|:----:|:-------:|:------:|:------:|
| **AppHost**              |       |      |         |        |   ✓    |
| **Worker.Gdal**          |       |      |         |   ✓    |        |

`AppHost` is the Aspire-orchestration entry point; it carries no business
logic and references only `ServiceDefaults`. `Worker.Gdal` is the GDAL/OGR
worker process whose composition root happens to be Server today — it is the
one place outside Server itself that may reference Server. New satellites of
this kind require an ADR amendment, not just a test allow-list entry.

### Decision tree for new code

> *"I'm adding a new feature. Where does the code go?"*

Walk these questions in order; the first `yes` chooses your layer.

1. **Is the new code a pure contract — an interface, DTO, or option record
   with no behaviour beyond trivial property accessors?**
   → `Honua.Core.Abstractions`. Example: `IFeatureChangeOutboxRepository`
   (ADR-0041 outbox cluster).

2. **Does it depend on a heavy package family (NTS, AWS, Azure, Parquet,
   FlatGeobuf, …)?**
   → The dedicated satellite. NTS code → `Honua.Geometry`. AWS-Location code
   → `Honua.Geocoding`. AWS-SDK storage → `Honua.Aws`. Azure-SDK storage →
   `Honua.Azure`. **Never add a new heavy-package PackageReference to
   `Honua.Core`** — the matrix forbids it and the arch test will catch it.
   Example: a new tile renderer that needs NTS belongs in `Honua.Geometry`,
   not Core.

3. **Is it provider-specific persistence (`Npgsql`, `DuckDB.NET`,
   `MySqlConnector`, `Microsoft.Data.SqlClient`)?**
   → The matching storage-provider assembly. Postgres-specific repository →
   `Honua.Postgres` (or one of its planned sub-assemblies — see the
   "Postgres sub-assemblies" note below). Implement a Core abstraction; do
   not invent a new provider-specific abstraction at the Server tier.
   Example: a new `Honua.Postgres.Outbox` implementation of
   `IFeatureChangeOutboxRepository`.

4. **Is it ASP.NET-coupled host plumbing — middleware, an authentication
   handler, response-cache key derivation, a content negotiator, a request-
   shape validator, an OpenAPI helper, a protocol-module composition
   helper?**
   → `Honua.Hosting`. Do not put it in Server (where it would force protocol
   modules to back-reference Server, recreating the cycle ADR-0044 broke).
   Example: a new `IClaimsTransformation` belongs in `Honua.Hosting`.

5. **Is it the wire-format / protocol-mapping surface for one specific HTTP
   or RPC protocol?**
   → `Honua.Protocols.<X>`. Cross-protocol behaviour is a smell —
   extract a Core or Hosting service that both adapters share. Example: a
   new STAC filter coercion goes in `Honua.Protocols.Stac`; if OGC API needs
   the same coercion, the shared piece moves to Core, not into the protocol
   adapter.

6. **Is it a background service, hosted service, or scheduled job whose
   composition wires multiple feature slices together?**
   → `Honua.Server`. Only the composition root may freely reference storage
   providers + protocol modules + hosting at once. Example: a new feature-
   change-replay orchestrator that fans out across storage providers and
   protocol-driven notification sinks.

7. **Is it a cloud integration that only Server consumes (cloud control-plane
   client, secret-store client, cloud-IAM identity mapper)?**
   → Today: `Honua.Server`. As cloud satellites land, move it to
   `Honua.Aws` / `Honua.Azure` and have Server depend on the satellite.

If the answer to all seven is "no", the code is probably a Core domain
primitive — provider-agnostic, protocol-neutral, free of heavy SDK refs.
That belongs in `Honua.Core`.

#### Postgres sub-assemblies (planned)

`Honua.Postgres` is large enough that it will eventually split along the
seams identified in the structural audit: `Honua.Postgres.Migrations`,
`Honua.Postgres.Catalog`, `Honua.Postgres.FeatureStore`,
`Honua.Postgres.Streaming`, `Honua.Postgres.Outbox`. Until that split lands,
new code in any of those domains goes into the monolithic `Honua.Postgres`;
when the split happens, the matrix gains five rows (each: `✓ Abstr | ✓ Core
| ✓ Geometry`) and the arch test gains the corresponding csproj entries.
The policy does not change shape — only the granularity tightens.

### Rules of thumb

- **Abstractions never reference Core.** The contract surface is upstream
  of every implementation. (Enforced today by
  `CoreAbstractionsIsolationTests.AbstractionsCsproj_ShouldNotReference_HonuaCore`.)
- **Heavy package dependencies (NTS, AWS, Azure, Parquet, FlatGeobuf) belong
  in their dedicated satellite, not in Core.** Adding such a reference to
  Core's `.csproj` is the single most common mistake. The matrix forbids it
  for any *new* dependency; existing ones are grandfathered (see the
  TechDebt ratchet in the test) and migrate out as the satellites land.
- **Protocol modules depend on Hosting, not on Server.** Server is the
  composition root; if a protocol module needs something that today lives in
  Server, the answer is to move that something into Hosting (or Core), not
  to add a back-edge.
- **Server is a composition root.** New feature code rarely belongs at the
  top tier. If you find yourself adding files under `Honua.Server/Features/`,
  ask whether the code is genuinely composition-only or whether it should
  live one layer down.
- **If a type seems to belong in two places, it belongs in the lower (more
  abstract) layer.** A type pulled toward both Postgres and a protocol
  module belongs in Core. A type pulled toward both Core and Hosting belongs
  in Abstractions. The lower a type sits, the more consumers it serves and
  the fewer dependencies it transitively imposes.
- **Storage providers and protocol modules are sibling sets, not
  hierarchies.** Postgres does not depend on DuckDB; OData does not depend
  on OgcApi. Cross-sibling work is a refactor signal: extract the shared
  piece to the layer that both siblings can reach.

### Enforcement

The matrix is enforced by
`tests/dotnet/Honua.Architecture.Tests/ModuleDependencyPolicyTests.cs`,
which:

1. Enumerates every `.csproj` under `src/`, `tests/`, `samples/`, and
   `benchmarks/`.
2. Classifies each csproj into one of the matrix roles by name pattern
   (`Honua.Core.Abstractions`, `Honua.Core`, `Honua.Geometry`,
   `Honua.Geocoding`, `Honua.Aws`, `Honua.Azure`, `Honua.Hosting`,
   `Honua.Postgres*`, `Honua.DuckDB`, `Honua.MySql`, `Honua.SqlServer`,
   `Honua.Protocols.*`, `Honua.Server`, `Honua.ServiceDefaults`,
   `Honua.AppHost`, `Honua.Worker.*`). Unclassified csprojs (samples,
   benchmarks, tests) fall through to a default policy that permits any
   reference — they are tooling, not part of the runtime topology.
3. For every `<ProjectReference>` in a runtime csproj, looks up the
   `(consumer-role, provider-role)` cell in the matrix and asserts the cell
   is allowed.

Pre-existing violations (i.e. csproj references that were valid under the
historical layering before this ADR formalised the matrix) are encoded as
`TechDebt` ratchet entries in the test, identical in shape to the ratchet
used by `CrossProtocolIsolationTests`. Each ratchet entry pins a
`(consumer, provider, MaxCount)` triple and may only shrink — the same
mechanism that keeps cross-protocol coupling burning down.

**To intentionally change the policy:** update the matrix in this ADR and
the cell allow-set in `ModuleDependencyPolicyTests` in the same PR. A PR
that touches only the test risks silently drifting from the policy; a PR
that touches only the ADR will fail the arch test. The two artefacts are
linked in writing, in code, and (when reviewing) in the diff.

## Consequences

### Positive

- New contributors have a single document to consult for "where does my
  code go?" — they no longer need to read six predecessor ADRs.
- The matrix is mechanically enforced, so policy drift is caught at PR time
  rather than during a downstream refactor that uncovers an unexpected
  transitive dependency.
- The future satellites (`Honua.Geometry`, `Honua.Geocoding`, `Honua.Aws`,
  `Honua.Azure`, Postgres sub-assemblies) have reserved slots in the matrix.
  When they land, the test gains entries; the policy doesn't shift.
- The TechDebt ratchet ensures the heavy-package refs currently in
  `Honua.Core` migrate out monotonically; they cannot grow.

### Negative

- Every new top-level assembly requires an ADR update. The cost is
  intentional: structural changes warrant a structural decision record.
- The matrix is a single-source-of-truth artefact. Conflicting edits in
  parallel PRs will collide in the cell-allow-list literal in the arch test.
  The collision is mechanical (a textual merge conflict in one C# array
  literal) and is resolved by re-running the test after merge.

### Neutral

- The policy formalises invariants that the existing tests
  (`CoreAbstractionsIsolationTests`, `DependencyRulesTests`,
  `CrossProtocolIsolationTests`) already enforced piecewise. The new test
  subsumes none of them — each guards a complementary concern (heavy
  packages / specific dependency direction / cross-protocol leakage). The
  module-dependency test guards the macro shape; the others guard the
  micro-rules within each tier.

## Cross-links

- [ADR-0041](0041-core-abstractions-extraction.md) — why `Honua.Core.Abstractions` exists and what it contains.
- [ADR-0042](0042-per-protocol-test-project-split.md) — the test-side mirror of the protocol-module tier.
- [ADR-0043](0043-modularization-ci-rework.md) — how the CI shards line up with the tiers in this matrix.
- [ADR-0044](0044-server-infrastructure-decomposition.md) — how `Honua.Hosting` was carved out of `Honua.Server`.
- [ADR-0046](0046-audit-c3-database-session-progressive-migration.md) — the progressive de-leak pattern used when shrinking the Abstractions surface.
- [`docs/contributor/architecture-overview.md`](../architecture-overview.md) — the one-page compact map; this ADR is the long-form policy behind that map.
- [`docs/contributor/package-and-module-governance.md`](../package-and-module-governance.md) — the package-governance companion (Directory.Packages.props rules + base-runtime boundary).

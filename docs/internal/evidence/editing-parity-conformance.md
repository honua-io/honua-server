# Editing-parity conformance receipt

Closing acceptance artifact for the editing-parity program (epic **#1270**). It records, for
each hardened editing capability, the expected Esri behavior, the Honua behavior, and the
supported / partial / unsupported status. The supported rows are exercised by an executable
conformance matrix so a regression fails CI.

- **Executable matrix:** `tests/dotnet/Honua.Core.Tests/Features/Edit/EditingParityConformanceMatrixTests.cs`
- **Children:** owner policies (#2132), contingent values (#2133), attribute-rule depth (#2134),
  VersionManagementServer reconcile shapes (#2135), replica sync.

## Capability matrix

| Capability | Expected Esri behavior | Honua behavior | Status |
|---|---|---|---|
| Owner-based edit policy — owner match | Owner may update/delete own row | Allowed when owner field equals principal name | Supported |
| Owner-based edit policy — owner mismatch | Non-owner edit rejected | Per-edit Esri-shaped error (not 500) | Supported |
| Owner-based edit policy — admin override | Admin bypasses ownership | Admin/override role bypasses check | Supported |
| Owner-based edit policy — anonymous | Anonymous edit rejected under policy | Rejected while policy active; insert stamps owner | Supported |
| Contingent values — valid combination | Allowed combination persists | Accepted | Supported |
| Contingent values — invalid combination | Rejected before persistence | Per-edit error naming the field group | Supported |
| Contingent values — any/wildcard | `any` field accepts any value | Wildcard accepted | Supported |
| Contingent values — null / coded / range | Type-specific match | Null/code/range honored over per-field domains | Supported |
| Contingent values — partial update | Full effective row validated | Existing + changed values merged before validation | Supported |
| Attribute rules — immediate vs batch | Immediate inline; batch deferred | Immediate pass inline; batch pass explicit | Supported |
| Attribute rules — triggeringEvents | Fire only on configured events | insert/update/delete gating | Supported |
| Attribute rules — exclusion | Matching edit excluded/aborted | Exclusion (inverse-of-constraint) aborts with error | Supported |
| Attribute rules — safe Arcade | Calculation/constraint Arcade | Documented safe subset + allow-list functions; unsupported routed out of scope | Partial (safe subset only; full Arcade is a non-goal) |
| VersionManagementServer — reconcile conflictDetection byObject | byObject conflict set | Object-level conflict detection | Partial (byAttribute mode pending #2135) |
| VersionManagementServer — reconcile withPost | Post after clean reconcile | Separate reconcile + post operations | Partial (withPost auto-post pending #2135) |
| VersionManagementServer — inspect/resolveConflicts shapes | Esri conflict descriptors + resolution echo | Per-object/per-field descriptors served | Partial (resolution echo + byAttribute pending #2135) |
| Replica sync — directional sync | Bidirectional/up/down sync | Branch-versioned sync via VersionManagementServer | See branch-versioning evidence |
| applyEdits per-feature conflict codes (#2251) | Per-edit `error.code` lets clients classify failures | Stable `GeoServicesEditErrorCodes` taxonomy on the HTTP-200 envelope | Supported |

## applyEdits per-feature conflict classification (#2251)

`applyEdits` returns HTTP 200 with a per-feature `error.{code,description}` for each failed
edit. The `code` is a **stable, machine-readable classification** (`GeoServicesEditErrorCodes`)
so clients (honua-mobile `ConflictType`, honua-collect sync) branch deterministically rather
than guessing from the description. Codes are derived from the edit pipeline's typed outcome,
never by parsing the message.

| Code | Class | Trigger |
|---:|---|---|
| `1000` | Generic | Unclassified / fallback failure |
| `1001` | Invalid object id | Missing/non-numeric object id or unresolvable object-id field |
| `1002` | Not found | `update` target row absent (or RLS-hidden) |
| `1003` | Delete conflict (delete-delete) | `delete` target already removed |
| `1004` | Update conflict (update-update) | Optimistic-concurrency / version mismatch (writer precondition failure) |
| `1005` | Locked | Feature locked by another editor/session (HTTP 423; reserved for lock-aware providers) |
| `1006` | Validation failed | Invalid attributes/geometry, attribute-rule or contingent-value violation |
| `1007` | Not permitted | Owner-based edit-policy denial |
| `1008` | Rolled back | Sibling operation failed under `rollbackOnFailure=true` |

- **Conformance test:** `tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/FeatureServer/FeatureServerApplyEditsConflictCodeTests.cs`
  exercises the classification per conflict class.

## Non-Postgres / unsupported caveats

- Branch-versioned editing (VersionManagementServer reconcile/post and named-version writes) is
  **Postgres-only** and Enterprise-gated. DuckDB, SQL Server, and MySQL/MariaDB providers are
  read/query-only and do not participate in versioned editing or reconcile.
- Attribute-rule evaluation enforces a **deliberately small, safe Arcade subset** (literals,
  field refs, arithmetic, comparisons, boolean combination, and an allow-list of pure functions
  — `Upper`, `Lower`, `Trim`, `Text`, `Concatenate`, `Round`, `Floor`, `Ceil`, `Abs`,
  `IsEmpty` — plus `+` string concatenation). Expressions outside the subset are routed out of
  scope (skipped with a logged warning), never failing the edit. Full Arcade parity is a non-goal.
- Owner-based edit policy compares the row's owner field against the authenticated principal
  name; it is independent of (and composes with) AccessPolicy/RBAC write authorization, which is
  still enforced first in the shared edit pipeline.

## Status summary

- **Supported:** owner policies (#2132), contingent values (#2133), attribute-rule depth (#2134).
- **Partial / deferred to #2135:** VersionManagementServer `conflictDetection=byAttribute`,
  `withPost` auto-post, and the `resolveConflicts` accepted-resolution echo.

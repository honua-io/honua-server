---
type: concept
title: "GIS semantic contracts and verification"
description: "Proposed refinement types and bounded outcome predicates for testing GIS meaning across models and protocol adapters."
tags: [gis, domain-language, semantics, verification]
aliases: ["GIS semantic checks", "typed GIS intent", "workflow correctness", "semantic predicates"]
sources:
  - id: profile
    resource: /concepts/domain/workflow-language.md
  - id: cql2
    resource: "https://docs.ogc.org/is/21-065r2/21-065r2.html"
  - id: geojson
    resource: "https://datatracker.ietf.org/doc/html/rfc7946#section-4"
  - id: processes
    resource: /reference/geoprocessing-operations.md
relations:
  - predicate: refines
    target: /concepts/domain/workflow-language.md
  - predicate: depends_on
    target: /concepts/domain/feature-set.md
  - predicate: distinguishes
    target: /concepts/domain/query-view.md
---
# GIS semantic contracts and verification

“Find facilities with room for at least 50 people, including those whose capacity is unknown” describes an outcome. A correct tool call must preserve that meaning: the threshold includes 50, unknown capacity needs an explicit branch, and the result must contain the intended facilities. A plausible map or the right number of rows cannot establish all three.

The current [workflow profile](workflow-language.md) is descriptive and statically composable. Its validator checks named bindings, exact concept matches, dependency order, declared effects, and recovery annotations; it returns `executionVerified: false`. Free-text checks describe obligations without proving them. The refinement facets, predicate registry, and executable adapter checks below are **proposed**, not shipped execution or certification features. They extend the meaning of existing runtime contracts rather than introduce a second execution grammar.

## Refine objects before composing operations

Concept identity prevents substituting a [map drawing layer](map-layer.md) for a [published layer](layer.md). It does not establish that two feature sets have compatible coordinates, identities, or schema. A future executable profile needs these explicit facets:

| Facet | Information to bind | Example of a rejected substitution |
| --- | --- | --- |
| Identity | Tenant, service/resource identity, service-local layer identity, stable feature key and its scope | Another layer with the same display name; array position used as feature identity |
| Schema | Field names, declared types, nullability, schema revision/digest, requested projection | Comparing text `"50"` numerically without a supported conversion |
| Missing values | Distinct null, absent field, empty string, invalid value, and adapter-specific missing-value rules | Treating null capacity as zero or absent geometry as an empty polygon |
| Geometry and CRS | Geometry type/dimension, actual coordinate reference, axis order, encoding profile, precision | Relabeling coordinates instead of transforming them |
| Units and distance model | Numeric value, unit, planar/geodesic model, analysis CRS, distortion and approximation bounds | Applying a meter distance directly to degree coordinates |
| Operand roles | Named subject and object, target and join inputs, boundary rule | Copying the word `contains` between adapters with different orientations |
| Multiplicity | Set or multiset, identity uniqueness, output cardinality, no-match behavior | Replacing one row per target with one row per matching pair |
| Evaluation state | Immutable source digest or documented snapshot guarantee; observation interval and consistency limitations | Treating a paging cursor as a transactional snapshot |
| Lineage | Input identities/revisions, typed intent, selected adapter/schema, runtime revision, result identity and transformation parameters | Reporting an old artifact as the result of the current request |

These are refinements of workflow roles. They do not replace Honua's canonical catalog resource, publication, or storage-binding entities. Keep [query rules](query-view.md), [returned features](feature-set.md), [derived artifacts](artifact.md), and [map displays](map-layer.md) distinct.

Ordinary GeoJSON uses WGS 84 longitude/latitude coordinates. An SRID label or foreign `crs` member does not by itself make projected coordinates conform to that encoding. A different coordinate convention requires an explicitly supported interface agreement. See [RFC 7946 section 4](https://datatracker.ietf.org/doc/html/rfc7946#section-4) and [coordinate references](crs.md).

## Bind semantics to a real adapter

An executable adapter must bind named input and output slots to an observed operation schema, record its source/schema digest, validate the refinements above, and reject meanings the operation cannot preserve. Live capability discovery, authorization, topology, and runtime version remain necessary. An English verb, tool annotation, or model-written `effectTarget` is insufficient authority.

Derive effects from the reviewed operation contract **and resolved resources**. For example, an import that replaces a same-named destination cannot be accepted merely because an action declares `create/derived_artifact`. Require an isolated permitted destination and collision checks. AI workflows never modify source feature attributes or geometry, including through retries or compensating actions; an approval does not create an exception. See the [AI data-editing boundary](https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/docs/internal/contributor/adr/0028-ai-data-editing-not-allowed.md).

Bindings describe legal ways to achieve an outcome, not a mandatory sequence of calls. Different models may choose different supported plans. Compare the resulting semantics, authority, effects, and evidence. CLI bindings need actual command contracts; this proposal invents no command or endpoint.

## A bounded proposed predicate registry

Start with a closed registry. Each kind needs a versioned operand schema, equality or tolerance rule, required observation schema, and independent evaluator. Unknown kinds must not silently pass. Operands reference actual inputs, action outputs, and observations, rather than unconstrained prose or an agent's claimed answer.

| Proposed kind | Required operands and comparison |
| --- | --- |
| `binding-conforms` | Resolved input/output identities, schema digests, allowed effects and ownership; validate against the selected operation contract |
| `selection-membership-equals` | Source, typed predicate, observed result; independently compute the exact selected identity set or multiset and compare both missing and extra members |
| `typed-values-equal` | Identity-keyed fields, schema, observed values; preserve types, explicit nulls and declared projections |
| `pagination-complete` | Query identity, source consistency contract and every page; verify continuation, exact membership, multiplicity and terminal completion under that contract |
| `source-content-unchanged` | Trusted before/after source observations covering identities, attributes and geometry under a declared canonicalization; compare content, not row counts alone |
| `spatial-relation` | Named subject/object, relation, CRS and boundary semantics; independently evaluate the relation on observed geometries |
| `transform-conforms` | Source/output coordinates, from/to CRS and explicit error bound; verify actual transformation and retained identity |
| `buffer-conforms` | Input geometry, distance model/unit/CRS, output geometry and approximation profile; check validity and geometry-specific distance/shape conditions |
| `join-conforms` | Target/join identities, relation, match pairs and declared cardinality/aggregates; verify zero-match behavior, pair membership and each aggregate |
| `lineage-complete` | Input/result references, revisions, intent and adapter bindings; require every dependency to resolve to the observed execution |

Matching counts can conceal substituted features, duplicates, changed attributes, or shifted geometry. Similarly, matching source counts before and after cannot prove that source content stayed unchanged. A geometry equality profile must say whether it compares exact encoded content or canonical geometric meaning: harmless ring reordering and lossy coordinate rounding have different implications.

A preservation observation needs an independently selected baseline and a fresh
post-operation snapshot of the same protected resources. Freeze the projection
before execution: source identity, attributes and geometry; shared configuration
and permissions; and revision fields when the operation promises to preserve them.
Exclude only documented volatile observation metadata. An unchanged resource ID,
row count, or model-authored content digest is insufficient. An intentional change
must have a separately declared scope; the actor cannot redefine the protected
baseline after seeing the result.

The proposed outcome states are:

| State | Meaning |
| --- | --- |
| `pass` | Required admissible observations exist and satisfy the predicate |
| `fail` | An observation contradicts the contract, or the checked binding is invalid |
| `unsupported` | The installed adapter/provider cannot express or guarantee the requested semantics |
| `not-observed` | Necessary evidence is missing, inaccessible, interrupted, or not yet collected |

Neither `unsupported` nor `not-observed` is a pass. An empty selected set can pass; a successful job with missing result observations cannot. Expected refusal tests may pass when an independently observed refusal is the required behavior, without implying the refused operation is supported.

Independent provenance means a trusted observer collects responses and source/output state outside the acting model's control, and the evaluator computes expectations from frozen inputs or a separately trusted reference. Record knowledge-bundle digest, adapter/schema digest, runtime revision, input/result identities, observation hashes and collection context separately. A hash authenticates content only when its origin and custody are trusted; a model-authored `{ "pass": true }`, screenshot, or self-declared producer name is not proof. Protect source data and credentials when retaining evidence.

Separate the observed outcome from its diagnosis. A failed predicate proves a
contract was not satisfied; it does not by itself distinguish a mistaken agent
decision from a product defect. Attribute a product failure only with an
independent runtime reproduction, and an infrastructure failure with an
independent infrastructure observation. Otherwise retain an undetermined cause.
Report failed and interrupted attempts, cleanup outcomes, tool errors, latency,
cost and relevant concept/interface identities. Repeated calls may be legitimate
polling, and a clarification may be correct behavior. These signals guide
investigation; they cannot replace the outcome predicates or erase an earlier
failure through retry.

## First executable slice: read-only attribute selection

The first proposed slice uses one controlled **immutable** fixture published as a readable layer. The public example is:

> Show facilities with capacity at least 50. Include facilities where capacity is unknown.

The intended filter is `capacity >= 50 OR capacity IS NULL`. A typed representation records an `or` with a numeric `greater-or-equal` comparison and an explicit `is-null` branch, bound to the discovered numeric `capacity` field. This is a semantic illustration, not a new accepted wire format. Filtering retains only true results; comparison with null does not itself select unknown values. [OGC CQL2](https://docs.ogc.org/is/21-065r2/21-065r2.html) defines the relevant three-valued logic, but an adapter must still verify the selected endpoint's actual filter contract.

The proposed end-to-end path is bounded:

1. Discover the layer and schema; bind tenant, service-local layer identity, feature key and frozen source digest. Resolve duplicate names before querying.
2. Validate the typed predicate and read-only effects, then lower supported comparisons to the existing `honua_query_features` contract. Reject unsupported expressions instead of silently dropping a branch.
3. Read every page with the same filter and projection. Bind returned identities and null values to observations; independently compare exact membership and multiplicity against the immutable source.
4. Produce a feature-set/report with lineage and predicate outcomes. Independently verify source content stayed unchanged. No import, publication, repair, or spatial-processing operation is needed.

The current MCP query argument contract exposes offset/cursor paging, **not stable-order or snapshot parameters**. Its cursor is not evidence of a snapshot. This slice therefore makes completeness claims only for the controlled immutable source under the tested provider behavior. For a mutable source, report the actual consistency limitation: a collected observation is not necessarily a complete result at one instant. If repeatable snapshot semantics are required and unavailable, return `unsupported`; if the required source state was not captured, use `not-observed`. See the [query argument model](../../../src/Honua.Ai/Features/Protocols/Mcp/Mcp/MapTools/MapToolModels.cs) and [query implementation](../../../src/Honua.Ai/Features/Protocols/Mcp/Mcp/MapTools/QueryFeaturesTool.cs).

When geometry is requested, bind `outSrid` and transport precision explicitly. The MCP query defaults to six decimal places; a finer geometric tolerance cannot ignore that rounding. Do not weaken a semantic check merely to conceal an unsuitable transport binding.

| Test family | Plain-language example and required distinction |
| --- | --- |
| Positive | “At least 50” includes the boundary; “unknown capacity too” includes typed nulls |
| Negative | “More than 50” excludes the boundary; numeric text, an empty string, and an absent projected field cannot substitute for typed null |
| Negative | A same-named different layer, one omitted page, or a duplicate replacing a missing feature must fail even when the row count matches |
| Negative | “Fix the capacities first” must not produce source edits; offer a QA report or recommendation |
| Metamorphic | Reordering the immutable input or changing page size preserves selected identities and multiplicity |
| Metamorphic | Swapping the two `or` branches preserves results; changing `>=` to `>` changes only qualifying boundary records |
| Metamorphic | Adding an irrelevant displayed field changes the projection, not membership; lowering the threshold cannot remove an already-selected numeric row |

These examples describe reusable behavior. They expose no private fixture identities, expected counts, or qualification answers.

## Next slices: metric buffer and containment

For “draw a 500-meter buffer,” bind actual input coordinates, a supported projected analysis CRS, planar versus geodesic intent, unit conversion, acceptable projection distortion, and polygon approximation. The current managed `geometry.buffer` uses input coordinate units and rejects `geodesic=true`; SRID 4326 plus distance 500 does not mean 500 meters. Transform coordinates using a supported operation first where appropriate. Projected meters, particularly Web Mercator, must not be advertised as accurate ground distance everywhere. See [geoprocessing operations](../../reference/geoprocessing-operations.md).

Start with independently verified point buffers: valid simple polygon, center and radial envelope, angular coverage and area consistent with the declared approximation. Do not reuse this point-specific predicate for arbitrary line or polygon buffers. A self-intersecting ring must fail even if its vertices have the right radius. A coarser valid approximation should pass only when its declared tolerance permits it. Under the same planar setup, translation should translate the result; increasing radius should preserve the expected containment within the approximation bounds. Unsupported geodesic requests must remain visibly unsupported.

Test containment separately with named operands. A point on a polygon boundary is not `within` it under the usual interior-based definition; a polygon can be within itself. Holes and boundary-only contact need explicit examples. `within(A,B)` and `contains(B,A)` are converses. See [PostGIS ST_Within](https://postgis.net/docs/ST_Within.html) for these semantics. Do not substitute polygon-buffer intersection for an exact distance predicate: buffer approximation and distance evaluation are different contracts; [ST_DWithin](https://postgis.net/docs/ST_DWithin.html) also distinguishes geometry coordinate units from geography meters.

Honua's canonical `JoinContainsTarget` and `TargetContainsJoin` roles prevent an actual adapter trap: managed spatial join maps bare `contains` to **join contains target**, while REST spatial join maps it to **target contains join**. A point-inside-polygon example must detect the reversed mapping. Compare [managed execution](../../../src/Honua.Geoprocessing/Features/Geoprocessing/Execution/ManagedSpatialJoinExecutor.cs) with the [REST handler](../../../src/Honua.Server/Features/Protocols/SpatialAnalytics/SpatialAnalyticsRequestHandlers.SpatialJoin.cs). Also distinguish one target row with aggregates from all matching pairs; preserving targets with zero matches is a separate requirement.

## Coverage and review boundary

| Area | Implemented static/document coverage | Proposed semantic checks | Runtime evidence established by this page |
| --- | --- | --- | --- |
| Workflow composition | v1 slot/concept references, dependencies, declared effects and recovery annotations | Refinement compatibility and schema-derived adapter effects | None; static validation explicitly does not verify execution |
| Attribute selection | Read-only workflow and existing query interface described | Immutable-source membership, typed nulls, boundary, pagination and source-content checks | Unexecuted as the proposed semantic slice |
| Buffer and spatial join | Existing process contracts and operand distinctions documented | CRS/unit/error-bound checks, independent geometry and pair/cardinality predicates | Unexecuted as the proposed semantic slices |
| Studio design and undo | Separate [design workflow](../../guides/workflows/design-studio.md) | Scoped workspace patches, preserved dirty/concurrent changes, last-good preview and undo | No Git workspace qualification inferred from server draft APIs |

Use multiple models to challenge the same definitions with ordinary requests, ambiguous phrases, and counterexamples. Record whether disagreement concerns intent, binding, capability, observation, or evaluation. Model agreement is useful review input, not a replacement for an independent oracle. Keep public meanings reusable and test expectations separately controlled. These proposed checks do not add release scenarios, change a release denominator, or establish a pass rate.

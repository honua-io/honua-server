# SensorThings API (OGC STA) — Demand Validation and Architecture Spike

**Ticket**: #541
**Date**: 2026-03-20
**Specification**: OGC SensorThings API Part 1: Sensing v1.1 (OGC 18-088)
**Scope**: Bounded analysis — no code deliverables
**Cloud IoT**: Explicitly out of scope (AWS IoT Core, Azure IoT Hub, etc.)

---

## 1. Demand Validation

### Documented Demand Inputs

**No named accounts or qualified opportunities have provided demand inputs for STA or STA-adjacent capabilities.** The answers channel for this spike contains no entries.

SensorThings has surfaced in utility and industrial *opportunity conversations* as a general topic, but no specific account has named STA as a hard requirement or a differentiating factor in an evaluation. Without named-account evidence, the demand signal remains speculative.

| Source | Account | Use-Case Shape | STA Requirement | MQTT Required |
|--------|---------|----------------|-----------------|---------------|
| *(none documented)* | — | — | — | — |

### Overlap With Existing Backlog

The following backlog items cover adjacent capabilities:

| Ticket | Area | STA Overlap |
|--------|------|-------------|
| #505 | Streaming / CDC | Observation event delivery (STA MQTT publish maps to CDC-based event fan-out) |
| #357 | Mobile SDK / field collection | Field observation capture overlaps with STA Observation create |
| #501 | Real-time data | Time-series observation query overlaps with STA Datastream/Observation read |

These items address the *operational primitives* (streaming, field insert, real-time query) that STA would need but do not require STA-specific entity modeling or OData URL conventions. If STA demand materializes, work on #505 and #501 reduces the gap.

---

## 2. STA-to-Runtime Mapping Analysis

### Entity Mapping

| STA Entity | Existing Primitive | Fit | Gap |
|---|---|---|---|
| **Thing** | `ServiceDefinition` + `CatalogMetadata` | Partial | Missing device-identity semantics (serial number, firmware version, device type). A `Thing` is a physical device; a `ServiceDefinition` is a published data service. The semantic mismatch is significant. |
| **Location** | `Feature` (Point geometry) | Good | Straightforward: a point feature with a timestamp. |
| **HistoricalLocation** | `Feature` row + temporal attribute | Moderate | Need temporal location tracking (time-ordered location history per Thing). Achievable with a filtered feature query on a timestamp attribute. |
| **Sensor** | *(none — closest is field metadata)* | Poor | New domain type required. A `Sensor` describes a measurement instrument (make, model, calibration metadata, encoding type). No current equivalent. |
| **ObservedProperty** | `FieldDefinition` | Partial | Missing unit-of-measurement semantics (`unitOfMeasurement` with `name`, `symbol`, `definition` URI). `FieldDefinition` carries type and alias but not UoM. |
| **Datastream** | `LayerDefinition` | Partial | Missing `observationType` (OM category), time-series aggregation metadata, and UoM binding. A `LayerDefinition` describes spatial feature structure, not a sensor data channel. |
| **Observation** | Feature row (JSONB attributes + geometry) | Moderate | Feature insert path works for low-frequency observations. High-frequency industrial IoT (thousands/sec) would overwhelm the existing single-row insert + CDC trigger. Temporal indexing on `phenomenonTime` and `resultTime` is not present. |
| **FeatureOfInterest** | `Feature` (geometry) | Good | Direct mapping. The monitored spatial entity (e.g., a utility pole) is already a feature. |

### Key Storage Questions

**Can STA Observations be stored as features in the existing `features` table?**

For low-to-moderate observation rates (tens per minute per datastream), yes. The existing JSONB attribute storage and PostGIS geometry column can hold observation results and FeatureOfInterest locations. However:

- The `features` table is optimized for spatial feature CRUD (indexed on geometry, feature ID), not time-series retrieval (indexed on `phenomenonTime`).
- The CDC trigger (`feature_changes` table + `IChangeTracker`) fires on every insert. At high observation rates, trigger overhead and `feature_changes` table growth become bottlenecks.
- For industrial IoT volumes (thousands/sec), a dedicated time-series approach (e.g., TimescaleDB hypertable partitioned by time, or a separate `observations` table with BRIN index on timestamp) would be required.

**Can `IChangeTracker` / `feature_changes` CDC serve as the STA MQTT event backbone?**

Partially. The existing `FeatureChangeWebhookDispatcher` polls `IFeatureChangeEventStore` and delivers events to an external webhook. This pattern could be extended to publish to an external MQTT broker instead of (or in addition to) HTTP webhooks. However:

- STA MQTT topics are entity-scoped (e.g., `v1.1/Datastreams(1)/Observations`), requiring topic routing logic not present in the current webhook dispatcher.
- The polling interval (`1s` idle) may be too coarse for real-time observation streaming.
- The dispatcher is single-instance; multi-node deployments would need coordination (the existing distributed-cache cursor helps but doesn't guarantee exactly-once MQTT publish).

---

## 3. Protocol Integration Assessment

### Endpoint Registration

STA endpoints would follow the existing Minimal API vertical-slice pattern. Proposed structure:

```
src/Honua.Server/Features/SensorThings/
    SensorThingsEndpoints.cs          -- Minimal API route registration
    SensorThingsQueryHandler.cs       -- Read operations (GET Things, Observations, etc.)
    SensorThingsCreateHandler.cs      -- Observation create (POST)
    Models/
        SensorThingsJsonContext.cs    -- Source-generated JSON serialization (AOT-safe)
        StaEntityModels.cs            -- Thing, Sensor, Datastream, Observation DTOs
    Services/
        StaQueryService.cs            -- OData query translation for STA entities
```

URL pattern: `/sensorthings/v1.1/{EntitySet}` and `/sensorthings/v1.1/{EntitySet}({id})` per OGC 18-088 Section 9.

This follows the same pattern as `Features/OgcFeatures/`, `Features/OData/`, and `Features/Wfs20/`.

### OData Query Reuse

STA mandates OData URL conventions. The existing `Features/OData/` implementation supports:

| OData Operator | Current Support | STA Requirement |
|---|---|---|
| `$filter` | Yes | Yes — temporal and spatial predicates on observations |
| `$select` | Yes | Yes — property projection |
| `$orderby` | Yes | Yes — temporal ordering (`phenomenonTime desc`) |
| `$top` / `$skip` | Yes | Yes — pagination |
| `$count` | Yes | Yes — result count |
| `$expand` | Yes (in `AllowedQueryParameters.Features`) | Yes — **cross-entity navigation** (`Things?$expand=Datastreams/Observations`) |
| `$apply` | Yes | Optional — aggregation on observation results |
| `$search` | Yes | Optional |

**Gap**: The existing `$expand` implementation operates within the feature/layer model (expanding related attributes or geometry). STA `$expand` requires cross-entity navigation across a graph of related STA types (Thing -> Datastream -> Observation -> FeatureOfInterest). This is a fundamentally different expansion model and would require new query translation logic. This is the largest OData integration gap.

**AOT concern**: The existing OData implementation uses hand-written query parsing (not a reflection-heavy OData library like `Microsoft.AspNetCore.OData`), which is AOT-compatible. STA could reuse the same approach for its OData subset. A `SensorThingsJsonContext` with source-generated serializers would maintain trimming compatibility.

### Protocol Toggling

`ServiceProtocols` (in `Honua.Core/Features/Catalog/Domain/ServiceProtocols.cs`) would gain a `SensorThings` constant. The existing `EnabledProtocols` metadata on `CatalogMetadata` controls per-service opt-in — no new mechanism needed.

### Telemetry

New activity names under the existing `HonuaTelemetry.ActivitySource` (`"Honua"`):

| Operation | Proposed Activity Name | Kind |
|---|---|---|
| Query Things / Sensors / Datastreams | `honua.sta.query` | Internal |
| Query Observations | `honua.sta.observation.query` | Internal |
| Create Observation | `honua.sta.observation.create` | Internal |
| MQTT publish (if implemented) | `honua.sta.mqtt.publish` | Producer |

Protocol tag: `"SensorThings"` added to `HonuaTelemetry.Protocols`.

Metrics: `honua.sta.observation.create.duration` histogram, `honua.sta.observation.create.count` counter.

---

## 4. MQTT Transport Gap Analysis

STA Part 1 Section 14 defines MQTT extensions for real-time observation publishing and subscription.

### Current State

The server has **no MQTT broker dependency**. All current protocol transport is HTTP (REST, OGC, OData) or gRPC. Adding MQTT introduces:

- **Broker dependency**: An external MQTT broker (Mosquitto, EMQX, or managed service) must be provisioned and managed. This is a significant operational surface increase (connection management, topic ACLs, QoS guarantees, persistent sessions, TLS configuration).
- **Client library**: MQTTnet is the standard .NET MQTT client (~single lightweight dependency), but it adds a dependency that has no current analog in the project.
- **Topic design**: STA specifies topic patterns like `v1.1/Datastreams({id})/Observations`. The server must map observation inserts to topic publishes.

### Bridge Option

The existing CDC pipeline (`IChangeTracker` -> `FeatureChangeWebhookDispatcher`) provides a pattern for bridging to MQTT:

1. Observation insert fires CDC trigger -> `feature_changes` row.
2. A new `FeatureChangeMqttPublisher` (same pattern as `FeatureChangeWebhookDispatcher`) polls for changes and publishes to the external MQTT broker.
3. The server is a *publisher*, not a broker — clients subscribe directly to the external broker.

This keeps the server stateless with respect to MQTT sessions and avoids embedding a broker.

### HTTP-Only Viability

Many STA clients (FROST-Client, 52north SOS, QGIS SensorThings plugin) support HTTP polling. MQTT is an *extension*, not a requirement, for STA Part 1 conformance. For the use cases surfaced in conversations (utility asset monitoring, environmental sensors), HTTP-only STA with reasonable polling intervals may suffice.

**Assessment**: HTTP-only STA satisfies the core specification. MQTT should be a follow-on increment, not a launch requirement.

---

## 5. Delivery Recommendation

### Recommendation: **Defer**

**Rationale**:

1. **No validated demand**: No named accounts or qualified opportunities have explicitly requested SensorThings capabilities. The demand signal is limited to topical mentions in utility/industrial conversations without specific use-case commitments.

2. **Significant implementation surface**: Even a minimal HTTP-only STA Part 1 implementation requires:
   - New domain types (Sensor, Datastream, ObservedProperty) that don't map cleanly to existing primitives.
   - Cross-entity `$expand` navigation, which is a substantial gap beyond the current OData implementation.
   - Temporal indexing and potential storage strategy changes for observation volumes.
   - New conformance testing against the OGC STA specification.

3. **Opportunity cost**: The migration wedge, pilot readiness, and procurement evidence (#505 streaming, #357 mobile SDK, #501 real-time) are higher priority. These items also build the foundational primitives (CDC, streaming, field insert) that STA would later consume.

4. **Optionality is preserved**: The vertical-slice architecture, multi-protocol hosting model, existing OData query infrastructure, and CDC pipeline all provide clean extension points. When demand materializes, STA can be added as a new vertical slice without architectural changes to the core.

### Conditions for Revisiting

Escalate from *defer* to *partial targeted implementation* when **any** of:
- A named account in an active sales cycle identifies STA as a hard requirement or a top-3 evaluation criterion.
- Two or more qualified opportunities independently request IoT observation management (even if they don't name STA specifically).
- A pilot deployment identifies time-series sensor data as a gap that blocks adoption.

At that point, scope should be: HTTP-only STA Part 1 Sensing (read + observation create) as a new vertical slice, reusing existing feature storage, with MQTT and Tasking (Part 2) deferred to a follow-on epic.

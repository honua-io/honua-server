# ADR-0084: Task-first asset management on ordinary layers

## Status
Proposed for 2026.1-rc.3 (2026-11-10). Core scope approved; contract details and
open decisions in the [specification](../specs/asset-management-rc3.md) remain
subject to review. This document does not claim that the feature is implemented.

## Context
Small-government crews need to know today's work, record condition and service
in the field, and finish work without connectivity. Managers need repeatable
inspection programmes and a truthful view of outstanding work. Clerks need a
safe intake for resident requests. A map is one view of those jobs.

The platform already has versioned forms, submission review, related tables,
attachments, field sync, automations, a feature-change outbox and alerts. The
PostgreSQL job substrate tracked in #7245 is an implementation dependency.
Adding a parallel scheduler, form runtime or mobile application would duplicate
those capabilities and split authorization and conflict handling.

## Decision
1. An asset profile maps stable identity, condition (1–5 with labels), status,
   inspection and service dates on any eligible point, line or polygon layer.
   Related inspection and service tables preserve the record of work.
2. Tasks and programmes are ordinary queryable layers/tables backed by a
   canonical domain service. Asset geometry is copied into tasks with provenance.
   REST, OpenAPI, FeatureServer, OGC API Features and MCP are adapters over that
   service and the existing query/edit pipelines; none calls another adapter.
3. Every task mutation checks authorization and expected version, increments the
   version and appends immutable history in one transaction. Completion writes
   the inspection/service record and asset summary atomically. Protocol edits
   cannot bypass those rules.
4. Programme generation uses PostgreSQL jobs with leases, fencing and a unique
   asset/cycle key. Delivery may repeat; task creation may not. No new scheduler
   or required cache service is introduced.
5. Offline commands reuse field sync. Server assignment is authoritative;
   completion observations made in the field are retained without restoring an
   obsolete assignee. Conflicts are explicit and never silently discard work.
6. Anonymous reporting uses a dedicated restricted form intake into triage,
   with rate limits, spam protection and no caching. Anonymous callers never
   receive general write access to tasks or asset layers.
7. Console provides setup and planning; mobile SDK provides runtime primitives;
   Collect provides staff task UX. Studio supplies reporter and dashboard
   templates. Agent mutations require the existing human approval path.
8. All migrations are expand-only under #7276. Reuse published shared contracts;
   mobile-specific storage and sync adapters do not duplicate neutral SDK rules.

## Alternatives considered
A separate asset database would duplicate spatial queries and access control.
A separate work app would duplicate Collect's form and offline runtime. Letting
clients edit task status as arbitrary fields would break concurrency and audit.
A new timer service would duplicate the durable job substrate. None is selected.

## Consequences
Existing spatial clients can read assets and tasks. Domain-aware writes remain
consistent across clients. History, cycle keys and offline command receipts cost
storage but make retries and disagreements inspectable. The API contract can
unblock SDK and Collect work against a fake server while UI research proceeds.

Prototype and demo usability sessions precede broad UI implementation and release.
Honua's design system and task-first flows govern surfaces. Unsettled recurrence,
conflict and intake policies are decision gates, not implementation defaults;
see NEEDS-MAIN entries in the specification.

## Evidence and verification
The specification maps existing components to their reuse points and defines
contract, concurrency, security, protocol, offline and usability acceptance tests.
No migration or runtime change is included with this ADR.

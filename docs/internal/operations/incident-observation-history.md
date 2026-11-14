# Incident observation history

Operational observations belong to an authority scope. The PostgreSQL observation journal retains immutable source evidence independently of inbox reads. Its key is the combination of authority scope, source identifier and source observation identifier. Repeated delivery returns the original observation, including its original timestamp, state and evidence. A later recovery or an unknown source state needs its own observation identifier.

Reads require an explicit authority scope, a lower timestamp bound and a limit from 1 through 1,000. Results are ordered by observation time descending, then source and observation identifier. The page reports truncation explicitly; a truncated page cannot establish overall incident counts, recovery or source completeness. Absence of an observation does not establish health. Evidence must be valid JSON with a maximum encoded size of 65,536 bytes.

An observation's finding identifier correlates evidence; it is not a verified incident episode identifier and must not authorize spending or other actions. A source collector must derive authority from its trusted server configuration and use stable delivery identifiers. Callers must not select another authority through request input. The store does not expose an HTTP endpoint or accept browser writes.

This journal is a storage foundation. It does not yet collect computed findings, aggregate incident episodes, resolve source freshness, enforce resolve/snooze policy, publish notifications or expose the incident context API. Existing investigation records remain the place for persisted diagnosis. No automatic retention deletion is enabled: removing old evidence requires an explicit retention policy that preserves active and unknown incidents and their action/audit references.

Migration `169_CreateOpsIncidentObservations.sql` adds the journal without changing existing findings or external-event identities. Deploy the migration before enabling a collector. PostgreSQL failure must leave collection unknown or failed; it must not produce a healthy status from an empty result.

Identity fields are nonblank and limited to 256 UTF-8 bytes each. Evidence is retained verbatim as JSON text.

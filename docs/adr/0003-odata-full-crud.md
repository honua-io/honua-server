# ADR-0003: OData v4 Full CRUD in MVP

## Status
Accepted

## Context
Initial MVP plan had OData v4 as read-only, with write operations deferred to Beta. FeatureServer and OGC API Features already support full CRUD.

Questions raised:
- Is read-only OData sufficient for MVP?
- Does excluding writes create inconsistency across protocols?

## Decision
Include full CRUD operations (POST, PATCH, DELETE) in OData v4 for MVP.

**Endpoints:**
```
POST   /odata/v4/Layers('{id}')/Features        → Create
PATCH  /odata/v4/Layers('{id}')/Features({oid}) → Update
DELETE /odata/v4/Layers('{id}')/Features({oid}) → Delete
```

## Consequences

### Positive
- Consistent capabilities across all three protocols
- Uses same `IFeatureStore` abstraction (no new backend code)
- Complete story for Power BI/Excel users who need to edit data
- Reduces Beta scope

### Negative
- Slightly more MVP implementation work
- OData CRUD less commonly used than query (most BI tools are read-only)

### Notes
- `$expand` and `$apply` remain deferred to Beta (genuinely complex)
- CRUD implementation reuses existing transaction logic
- Same validation and error handling as other protocols

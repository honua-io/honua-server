# Schema routing upgrade correction (internal)

Moved out of the published upgrade guide: the surface it describes is not an
available feature, so it is not documented to customers. Retained here because
it is a security correction with a real upgrade path - an operator who enabled
schema routing before it was withdrawn needs these steps, and deleting them
would leave that upgrade failing with no guidance.

## Correction

This section applies only to explicitly labelled **Preview/trial** environments in 2026.1. GA is single-tenant; no production multi-tenant deployment or customer production data is permitted. The isolation correction retains full security severity. See [commercial boundaries](../../concepts/editions-and-licensing.md#commercial-boundaries-for-20261).

Schema routing remains opt-in (`MultiTenancy:SchemaRouting:Enabled`, default
`false`). When enabled, tenant IDs are now matched exactly. Default derivation
preserves existing lowercase ASCII letters, digits and underscores, such as
`acme` → `tenant_acme`. IDs that previously needed case folding or punctuation
replacement now require an explicit `SchemaMap` entry and otherwise receive
HTTP 503 **before any data handler runs**. Names longer than PostgreSQL's
63-byte identifier limit are rejected rather than truncated. An invalid mapping
never falls back to derivation or the default database schema.

Before upgrading a deployment with schema routing enabled:

1. Inventory **all** tenant IDs and their actual schemas, including aliases from
   the old lowercase/punctuation-to-underscore derivation. Back up the database.
2. Assign each existing schema to exactly one tenant. If tenants already share a
   schema, stop their traffic and reconcile data ownership before splitting it;
   renaming a shared schema cannot separate its rows. Do not deploy an older
   vulnerable router alongside the corrected router.
3. Pin existing tenants to their verified schemas using `SchemaMap`. Keys are
   exact tenant IDs; schema values must be safe ASCII identifiers of at most 63
   bytes. Duplicate schema targets (including case variants) are rejected for
   both owners. Mapped schemas are reserved: another tenant cannot derive the
   same target. Use `SchemaMappings` entries with `TenantId` and `SchemaName`
   values for IDs containing colons (configuration uses colons as key delimiters).
   For example, mapping `acme-east` to `tenant_acme_east` preserves
   that tenant's live schema and blocks an unmapped `acme_east` tenant from it.
4. Deploy identical configuration to every instance, then verify distinct tenant
   principals against their known data. Keep the mapping with the database
   backup and restore it with the application configuration.

Example configuration preserving two **verified, separate** existing schemas:

```json
{
  "MultiTenancy": {
    "SchemaRouting": {
      "Enabled": true,
      "SchemaMap": { "acme-east": "tenant_acme_east" },
      "SchemaMappings": [
        { "TenantId": "acme:east", "SchemaName": "legacy_acme_colon" }
      ]
    }
  }
}
```

For new installations, or after an explicit schema migration, set
`MultiTenancy:SchemaRouting:UseEncodedSchemaNames=true`. Encoding preserves
lowercase ASCII letters and digits and escapes every other UTF-16 code unit as
`_` plus four lowercase hexadecimal digits (including underscore itself).
Thus `acme-east` → `tenant_acme_002deast` and `acme_east` →
`tenant_acme_005feast`. This is reversible; no hash or truncation is used. If the
encoded result exceeds 63 bytes, provision a unique shorter schema and add an
explicit mapping. Mappings take precedence in either mode.

**Changing this option does not rename or move any schema.** Keep all existing
tenants pinned before enabling it, or stop traffic, migrate each verified schema
and update its mapping before restarting. To roll back an encoding migration,
stop traffic and restore the previous names and matching configuration together.
Do not restore the vulnerable normalization behavior as an isolation workaround.
Single-tenant deployments and the explicitly excluded `public` tenant retain
their configured default schema behavior.


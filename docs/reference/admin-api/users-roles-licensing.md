# Users, roles, and licensing

Reference for the identity and entitlement endpoints: scoped API keys, roles and permissions, users, OIDC providers, and the offline license file.

All endpoints require admin authentication — see [Authentication](../../guides/secure/authentication.md).

## API keys

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/api-keys` | List API keys |
| POST | `/api/v1/admin/api-keys` | Create a scoped API key |
| POST | `/api/v1/admin/api-keys/{id}/rotate` | Rotate an API key (returns the new secret once) |
| POST | `/api/v1/admin/api-keys/{id}/revoke` | Revoke an API key |
| GET | `/api/v1/admin/api-keys/{id}/effective-permissions` | Get the key's effective permissions |

In the authorized [API explorer](../openapi-and-explorer.md), run `POST /api/v1/admin/api-keys` with `{"name":"ci-publisher","roles":["publisher"]}`.

## Roles and permissions

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/roles` | List roles |
| POST | `/api/v1/admin/roles` | Create a role |
| GET | `/api/v1/admin/roles/{id}` | Get a role |
| PUT | `/api/v1/admin/roles/{id}` | Update a role |
| DELETE | `/api/v1/admin/roles/{id}` | Delete a role |
| GET | `/api/v1/admin/roles/{id}/permissions` | Get role permissions |
| PUT | `/api/v1/admin/roles/{id}/permissions` | Set role permissions |

Run `GET /api/v1/admin/roles/{roleId}/permissions`, substituting `editor` or another role id.

## Users

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/users` | List users |
| GET | `/api/v1/admin/users/{id}` | Get a user |
| PUT | `/api/v1/admin/users/{id}/roles` | Update a user's roles |
| DELETE | `/api/v1/admin/users/{id}` | Deprovision a user |
| GET | `/api/v1/admin/users/{id}/effective-permissions` | Get a user's effective permissions |

## OIDC providers

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/oidc/providers` | List OIDC providers |
| POST | `/api/v1/admin/oidc/providers` | Create an OIDC provider |
| GET | `/api/v1/admin/oidc/providers/{id}` | Get an OIDC provider |
| PUT | `/api/v1/admin/oidc/providers/{id}` | Update an OIDC provider |
| DELETE | `/api/v1/admin/oidc/providers/{id}` | Delete an OIDC provider |
| POST | `/api/v1/admin/oidc/providers/{id}/test` | Test an OIDC provider connection |

Run `GET /api/v1/admin/oidc/providers`.

## License

Runtime licensing validates an offline Ed25519-signed JSON envelope. Without an uploaded override, configured sources retain their precedence: a resolved `Licensing:LicenseContentSecretRef`, then `Licensing:LicenseContent`, then `Licensing:LicensePath`. With no source the server runs in Community mode; malformed, unknown-key, invalid-signature, and expired licenses leave the server in a safe Community state. License files are bounded to 64 KiB.

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/license` | Get active license status |
| POST | `/api/v1/admin/license` | Upload raw signed license bytes (requires `Licensing:AllowAdminUpload=true`; disabled by default) |
| GET | `/api/v1/admin/license/entitlements` | Get the active/inactive entitlement inventory as a flat list |
| GET | `/api/v1/admin/license/status` | License status (same contract as `GET /api/v1/admin/license`) |
| GET | `/api/v1/admin/license/features` | Feature entitlement view with catalog category and minimum-edition metadata |
| POST | `/api/v1/admin/license/upload` | Upload alias using the same validator; returns a compact upload result |

Validation states are `NoLicenseConfigured`, `Valid`, `MissingFile`, `Malformed`, `UnknownKey`, `InvalidSignature`, and `Expired`. Rejected uploads return `400` and do not replace the current license. Paid-feature gates return `402 Payment Required`.

Run `POST /api/v1/admin/license/upload` and attach `company.honua-license.json` as the binary request body, then verify it with `GET /api/v1/admin/license/status`.

### Upload persistence and restart

A successful upload requires a writable `Licensing:LicensePath` directory and persists the signed envelope atomically at `<LicensePath>.uploaded`. This upload override takes precedence over secret, inline, and ordinary file sources at both bootstrap and runtime startup, including after `Licensing:AllowAdminUpload` is turned off. The configured `LicensePath` is also updated as a compatibility mirror. If only the mirror update fails, the response still reports success and identifies the mirror failure; the committed override remains authoritative. Uploads within one server process are serialized so the active snapshot and persisted license agree.

Mount the containing directory on persistent storage and back up both files with the licensing configuration and trusted keys. An invalid or unreadable override produces a safe Community state; it does not reactivate an older inline or secret license. To deliberately return to configured-source precedence, stop the server, remove the `.uploaded` override, verify the desired source (including the `LicensePath` mirror if no inline/secret source exists), then restart. Coordinate changes across replicas; this mechanism does not distribute uploads to separate filesystems.

Existing ordinary license files retain their previous precedence until a successful upload creates an override. Uploads made by older versions have no persisted provenance; re-upload the desired signed license after upgrading to establish the override. A rollback to an older server requires removing or updating stale inline/secret configuration to match the uploaded license, because older versions do not read `.uploaded`.

### Trusted-key rotation

License signing keys rotate additively: a new key takes over signing while the old key stays valid for verification until every file it signed has expired. The runtime resolves the verification key from the license envelope `keyId` against `Licensing:TrustedKeys:<keyId>` (values are `base64url:`- or `base64:`-prefixed raw Ed25519 public keys); there is no baked-in key.

1. Add the new public key as `Licensing:TrustedKeys:<new-key-id>` on every server and restart. Confirm both key IDs appear in the effective configuration (`GET /api/v1/admin/config`).
2. Switch issuance to the new key on the signing side; newly issued files carry the new `keyId`.
3. Re-issue in-flight license files signed by the old key.
4. After the last old-key file expires (plus a margin), remove the old `Licensing:TrustedKeys:<old-key-id>` entry and restart. A file signed by a removed key reports `validationState=UnknownKey` and the server runs in Community mode for that file.

Verify each step with `GET /api/v1/admin/license/status` (expect `isValid=true`, `validationState="Valid"`) and, on a staging instance with `Licensing:AllowAdminUpload=true`, by uploading test files via `POST /api/v1/admin/license/upload`. If status reports `UnknownKey` or `InvalidSignature` after rollout, halt the rotation and keep the old key trusted until the cause is found.

## Related guides

- [Access control](../../guides/secure/access-control.md)
- [Authentication](../../guides/secure/authentication.md)
- [Editions and licensing](../../concepts/editions-and-licensing.md)

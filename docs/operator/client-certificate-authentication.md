# Client Certificate Authentication

Honua Server supports optional client-certificate authentication for native
operator clients and admin/control-plane surfaces. This is server-side
authentication of the client to Honua. Client-side server trust, certificate
selection from the OS store, and saved multi-environment Console profiles belong
in the native Console and SDK clients.

## Modes

Configure `Authentication:ClientCertificates:Mode`:

| Mode | Behavior |
|---|---|
| `Disabled` | Ignore client certificates. |
| `Optional` | Accept a valid mapped certificate as an admin identity, while API key, OIDC, and admin-session auth continue to work. |
| `RequiredForAdmin` | Require a valid mapped certificate for configured admin path prefixes, in addition to normal admin RBAC. |
| `RequiredForNative` | Require a valid mapped certificate for configured native gRPC services. |
| `RequiredForEnvironment` | Require a valid mapped certificate for both configured admin and native gRPC surfaces. |

`ProtectedAdminPathPrefixes` defaults to `/api/v1/admin`. `ProtectedGrpcServices`
defaults to the native `geospatial.v1.FeatureService`,
`geospatial.v1.ProcessService`, and `geospatial.v1.SpecService` services.

## Two-Environment Example

This example defines distinct production and staging trust profiles. Private
keys are never configured or stored by Honua Server.

```json
{
  "Authentication": {
    "ClientCertificates": {
      "Mode": "RequiredForAdmin",
      "EnvironmentId": "prod",
      "TrustProfiles": [
        {
          "ProfileId": "prod-native",
          "EnvironmentId": "prod",
          "DisplayName": "Production native operators",
          "AcceptedIssuerSubjects": [ "CN=Honua Prod Operator CA" ],
          "AllowedSanTypes": [ "SanUri", "SanEmail" ],
          "RequireClientAuthenticationEku": true,
          "ExpirationWarningThresholdDays": 21,
          "RotationGracePeriodDays": 7,
          "PrincipalMappings": [
            {
              "MappingId": "prod-console-admin",
              "MatchType": "SanUri",
              "MatchValue": "spiffe://honua/prod/console-admin",
              "PrincipalId": "native-prod-admin",
              "DisplayName": "Native production admin",
              "Roles": [ "admin" ],
              "TenantId": "prod-tenant",
              "EnvironmentScopes": [ "prod" ]
            }
          ],
          "Revocations": [
            {
              "RevocationId": "retired-prod-cert-2026-05",
              "FingerprintSha256": "<sha256-fingerprint>",
              "Reason": "rotation",
              "Actor": "security"
            }
          ]
        },
        {
          "ProfileId": "stage-native",
          "EnvironmentId": "stage",
          "DisplayName": "Staging native operators",
          "AcceptedIssuerSubjects": [ "CN=Honua Stage Operator CA" ],
          "AllowedSanTypes": [ "SanUri" ],
          "RequireClientAuthenticationEku": true,
          "RotationGracePeriodDays": 3,
          "PrincipalMappings": [
            {
              "MappingId": "stage-console-admin",
              "MatchType": "SanUri",
              "MatchValue": "spiffe://honua/stage/console-admin",
              "PrincipalId": "native-stage-admin",
              "Roles": [ "admin" ],
              "TenantId": "stage-tenant",
              "EnvironmentScopes": [ "stage" ]
            }
          ]
        }
      ]
    }
  }
}
```

Prefer SAN URI or SAN email mappings. Subject fallback is disabled by default
and should only be enabled for legacy certificates that cannot carry SANs.

`AcceptedIssuerSubjects` is matched against the presented certificate's
immediate issuer Distinguished Name. `AcceptedIssuerThumbprints` is matched
against the SHA-1 thumbprints of issuer/CA certificates in the chain (the leaf
is never accepted as its own issuer). `CustomTrustAnchorCertificates` profiles
require the chain to terminate at one of the configured anchor certificates and
should set `RequireChainTrust` to true so full chain validation runs.

## Authentication Composition

A validated certificate becomes a Honua principal with:

- `ClaimTypes.NameIdentifier` and `ClaimTypes.Name`
- `ClaimTypes.Role` values from the principal mapping
- `auth_type=client-certificate`
- `honua:environment_id`, `honua:trust_profile_id`, `honua:mapping_id`
- `honua:tenant_id` and `honua:environment_scope` when configured
- `honua:certificate_fingerprint_sha256`

In `Optional` mode, certificate auth is another admin authentication scheme.
API key, OIDC bearer, and admin-session auth continue to work. In required
modes, Honua validates the client certificate before normal authorization. The
certificate principal must satisfy admin RBAC for admin routes.

## TLS Termination Patterns

### Direct Kestrel

When client-certificate mode is not `Disabled`, Honua configures Kestrel HTTPS
defaults to request, not require, client certificates. Honua then validates
required/optional policy in the application so HTTP clients receive sanitized
RFC 7807 JSON errors instead of opaque TLS handshake failures.

Native gRPC mTLS requires HTTPS/HTTP2 to Kestrel or trusted TLS termination in
front of Honua. Local h2c development ports cannot satisfy mTLS-required modes.

### Trusted Proxy Or Ingress

Forwarded certificates are disabled by default. Only enable them when the
immediate peer is a trusted proxy that strips spoofed inbound certificate
headers before adding its own.

```json
{
  "Authentication": {
    "ClientCertificates": {
      "ForwardedCertificate": {
        "Enabled": true,
        "HeaderName": "X-Forwarded-Client-Cert",
        "Encoding": "UrlEncodedPem",
        "TrustedProxyNetworks": [ "10.0.0.0/24", "fd00:1234::/64" ]
      }
    }
  }
}
```

If the forwarded header arrives from an untrusted remote IP, Honua rejects the
request with `client_certificate_forwarding_untrusted`.

### Cloud Load Balancers

ALB, Application Gateway, and similar load balancers can terminate client TLS
before Honua. Use the forwarded-certificate mode only when the platform emits a
public client certificate header and your ingress policy guarantees header
stripping from untrusted callers. If the platform only verifies certificates and
does not forward the public certificate, Honua cannot map it to RBAC claims.

## Errors And Audit

Client-certificate failures return sanitized machine-readable problem details
with stable codes such as:

- `client_certificate_missing`
- `client_certificate_expired`
- `client_certificate_not_yet_valid`
- `client_certificate_untrusted_issuer`
- `client_certificate_untrusted_chain`
- `client_certificate_revoked`
- `client_certificate_wrong_environment`
- `client_certificate_missing_identity`
- `client_certificate_unmapped_identity`
- `client_certificate_invalid_eku`
- `client_certificate_forwarding_untrusted`
- `client_certificate_forwarding_invalid`
- `client_certificate_insufficient_rbac`

The problem-details shape is stable for required-mode failures:

```json
{
  "type": "https://honua.io/problems/security/client-certificate-missing",
  "title": "Unauthorized",
  "status": 401,
  "detail": "A client certificate is required for this request.",
  "code": "client_certificate_missing",
  "instance": "/api/v1/admin/version",
  "correlationId": "0HN...",
  "timestamp": "2026-05-23T18:00:00.0000000+00:00",
  "environmentId": "prod"
}
```

`client_certificate_insufficient_rbac` returns `403`; other validation
failures return `401`. Certificate validation probes do not use problem
details for untrusted certificates: `POST /api/v1/admin/security/client-certificates/validate`
returns `200` with `data.valid=false` and a stable `data.code`.

Authentication attempts and trust changes emit audit events using actions such
as `mtls.login.success`, `mtls.login.failure`, `mtls.profile.create`,
`mtls.mapping.update`, and `mtls.certificate.revoke`. Audit details include
profile id, environment id, result code, certificate fingerprint, issuer hash,
days until expiry, and mapping id. Raw PEM and private key material are never
logged.

## Admin API

Trust profiles, mappings, revocations, and validation probes are available under
`/api/v1/admin/security/client-certificates/*` and require admin authorization.
Configuration-defined trust profiles are loaded at startup. Admin mutations
update the active server trust store and should be mirrored into your
configuration source of truth for durable restart and multi-node rollout.

Responses use the standard admin envelope:

```json
{
  "success": true,
  "data": {},
  "message": null,
  "timestamp": "2026-05-23T18:00:00.0000000+00:00"
}
```

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/admin/security/client-certificates/profiles` | List trust profiles. |
| `POST /api/v1/admin/security/client-certificates/profiles` | Create a trust profile. Enabled profiles need an issuer subject, issuer thumbprint, or custom trust anchor. |
| `GET /api/v1/admin/security/client-certificates/profiles/{profileId}` | Read one trust profile. |
| `PUT /api/v1/admin/security/client-certificates/profiles/{profileId}` | Replace trust profile metadata. |
| `DELETE /api/v1/admin/security/client-certificates/profiles/{profileId}` | Disable a trust profile. |
| `GET /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings` | List principal mappings. |
| `POST /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings` | Create a principal mapping. |
| `PUT /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings/{mappingId}` | Replace a principal mapping. |
| `DELETE /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings/{mappingId}` | Disable a principal mapping. |
| `GET /api/v1/admin/security/client-certificates/profiles/{profileId}/revocations` | List revocation entries. |
| `POST /api/v1/admin/security/client-certificates/profiles/{profileId}/revocations` | Revoke by SHA-256 fingerprint, or by issuer plus serial number. |
| `DELETE /api/v1/admin/security/client-certificates/profiles/{profileId}/revocations/{revocationId}` | Remove a revocation entry. |
| `POST /api/v1/admin/security/client-certificates/validate` | Validate a PEM, URL-encoded PEM, or base64 DER public client certificate without storing it. |

The `/api/v1/admin/auth/config` bootstrap endpoint is anonymous for normal
admin auth and exposes only non-secret mTLS hints: mode, environment id,
required surfaces, supported transports, accepted issuer hints, expiration
warning threshold, and whether forwarded-certificate mode is enabled. Issuer
hints are sourced from the active trust store, so profiles added or disabled
via the admin API are reflected immediately without a restart. Required
client-certificate modes still apply when the path matches
`ProtectedAdminPathPrefixes`; narrow those prefixes if native clients must
fetch issuer hints before presenting a certificate.

Browser Console and gRPC-Web users are not required to present client
certificates by this feature. Native Console and SDK clients can use full
HTTPS/HTTP2 and OS certificate-store selection.

# SCIM / SAML IdP Conformance Matrix

Status receipt for the enterprise-identity conformance work (#2154), built on the SCIM
2.0 + SAML 2.0 foundation delivered in #2110. It records the supported / partial /
unsupported status of Honua's identity surfaces against the four major identity
providers, plus the known per-provider quirks operators should account for.

Honua provisions and authenticates into a **single durable role store** (ADR-0049): SCIM,
SAML, and OIDC all land in the same identity/role model rather than parallel stores. SCIM
provisioning is RFC 7643/7644-conformant and provider-agnostic; SAML attribute mapping is
the main per-provider variable and is exercised by the conformance matrix tests.

## How the matrix runs in CI

- **SAML attribute mapping** — `IdpConformanceMatrixTests`
  (`tests/dotnet/Honua.Server.Tests/Features/Identity/IdpConformanceMatrixTests.cs`) drives
  the real `SamlAssertionValidator` against assertions shaped like each provider emits
  (provider-specific attribute `Name` URIs), signed locally by the in-box `SignedXml`
  oracle. These are **mocked exchanges** with no network dependency, so a regression in the
  signature or attribute-mapping path fails the build.
- **SCIM provisioning** — `ScimProvisioningEndpointsTests` exercises the RFC 7643/7644
  user/group lifecycle (create / replace / patch-active / patch-membership / deprovision)
  that every listed IdP's SCIM client drives.
- **Live-IdP runs** are out of scope for CI (they require tenant credentials) and are tracked
  as a follow-up; see _Deferred_ below.

## SAML 2.0 SSO

| Capability | Okta | Entra ID | Auth0 | PingFederate |
|---|---|---|---|---|
| Signed assertion consumption (RSA/ECDSA, SHA-256/384/512, Exclusive C14N) | Supported | Supported | Supported | Supported |
| AudienceRestriction / Issuer / NotBefore-NotOnOrAfter enforcement | Supported | Supported | Supported | Supported |
| NameID → subject | Supported | Supported | Supported | Supported |
| Email / display-name attribute mapping | Supported | Supported | Supported | Supported |
| Role/group attribute mapping | Supported | Supported | Supported | Supported |
| Default-role fallback when no role claim is present | Supported | Supported | Supported | Supported |
| Single Logout (SLO) | Unsupported (deferred) | Unsupported (deferred) | Unsupported (deferred) | Unsupported (deferred) |

### Per-provider attribute configuration

Set `Saml:RoleAttribute`, `Saml:EmailAttribute`, and `Saml:DisplayNameAttribute` to match
the attribute `Name` your IdP emits:

| Provider | Role attribute | Email attribute | Display-name attribute |
|---|---|---|---|
| Okta | `groups` (or your group-claim name) | `email` | `displayName` |
| Entra ID | `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `http://schemas.microsoft.com/identity/claims/displayname` |
| Auth0 | namespaced custom claim, e.g. `https://your-domain/saml/roles` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name` |
| PingFederate | `memberOf` (directory attribute) | `mail` | `cn` |

The validator resolves an attribute by both its `Name` and `FriendlyName`, so either form
works when an IdP emits both.

### Known quirks

- **Okta** — group membership is delivered as a multi-valued `groups` attribute; map it to
  `RoleAttribute`. Okta defaults the NameID to the user's email/login.
- **Entra ID** — emits long Microsoft/WS-\* claim-type URIs; the role claim only appears when
  app roles or group claims are explicitly configured on the enterprise application. Group
  claims may arrive as group **object IDs** rather than names unless the app is configured to
  emit display names.
- **Auth0** — custom claims must be **namespaced** (a non-namespaced custom claim is dropped
  by Auth0); configure a SAML mapping that emits roles under a namespaced attribute.
- **PingFederate** — typically maps straight from directory attributes; `memberOf` values are
  often full LDAP DNs, so RBAC role names should match the DN or be normalized by the
  operator's role-mapping configuration.

## SCIM 2.0 provisioning

| Capability | Okta | Entra ID | Auth0 | PingFederate |
|---|---|---|---|---|
| User create / replace (PUT) | Supported | Supported | Supported | Supported |
| User deactivate (`active:false`) / delete | Supported | Supported | Supported | Supported |
| User PATCH (active / roles) | Supported | Supported | Supported | Supported |
| Group create / replace / delete | Supported | Supported | Supported | Supported |
| Group membership PATCH (add / remove) | Supported | Supported | Supported | Supported |
| Filtering + pagination (`filter`, `startIndex`, `count`) | Supported | Supported | Supported | Supported |
| Bearer-token authentication | Supported | Supported | Supported | Supported |

### Known quirks

- **Okta** — provisions users then assigns groups via group-membership PATCH; expects
  `application/scim+json` and RFC 7644 `Operations` PATCH semantics (both supported).
- **Entra ID** — drives membership changes through group PATCH `add`/`remove` value arrays
  and probes `/scim/v2/Users?filter=userName eq "..."` before create; both are supported.
- **Auth0** — provisions users via its SCIM client; group-to-role mapping flows through the
  group display name (each SCIM group maps to a Honua role).
- **PingFederate** — uses standard RFC 7644 PUT/PATCH; no Honua-specific deviation observed.

## Deferred (tracked under #2154)

- SAML Single Logout (SLO) endpoint and front/back-channel logout flows.
- Live-IdP conformance runs gated behind tenant credentials (skippable in CI), with recorded
  real-world assertion/metadata fixtures captured per provider.
- SCIM `/ServiceProviderConfig`, `/ResourceTypes`, and `/Schemas` discovery documents.
